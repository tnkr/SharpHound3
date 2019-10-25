﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using SharpHound3.Enums;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;

namespace SharpHound3.Tasks
{
    internal class LocalGroupTasks
    {
        internal static async Task<LdapWrapper> GetLocalGroupMembers(LdapWrapper wrapper)
        {
            if (wrapper is Computer computer && !computer.PingFailed)
            {
                var opts = Options.Instance.ResolvedCollectionMethods;
                if ((opts & CollectionMethodResolved.DCOM) != 0)
                    computer.DcomUsers = (await GetNetLocalGroupMembers(computer, LocalGroupRids.DcomUsers)).ToArray();

                if ((opts & CollectionMethodResolved.LocalAdmin) != 0)
                    computer.LocalAdmins = (await GetNetLocalGroupMembers(computer, LocalGroupRids.Administrators)).ToArray();

                if ((opts & CollectionMethodResolved.RDP) != 0)
                    computer.RemoteDesktopUsers = (await GetNetLocalGroupMembers(computer, LocalGroupRids.RemoteDesktopUsers)).ToArray();

                if ((opts & CollectionMethodResolved.PSRemote) != 0)
                    computer.PSRemoteUsers = (await GetNetLocalGroupMembers(computer, LocalGroupRids.PsRemote)).ToArray();
            }

            return wrapper;
        }

        private static readonly Lazy<byte[]> LocalSidBytes = new Lazy<byte[]>(() =>
        {
            var sid = new SecurityIdentifier("S-1-5-32");
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            return bytes;
        });

        private static async Task<List<GroupMember>> GetNetLocalGroupMembers(Computer computer, LocalGroupRids rid)
        {
            var sids = new IntPtr[0];

            var task = Task.Run(() => CallLocalGroupApi(computer, rid, out sids));

            var success = task.Wait(TimeSpan.FromSeconds(10));

            var groupMemberList = new List<GroupMember>();

            if (!success)
            {
                OutputTasks.AddComputerStatus(new ComputerStatus
                {
                    ComputerName = computer.DisplayName,
                    Status = "Timeout",
                    Task = $"GetNetLocalGroup-{rid}"
                });
                return groupMemberList;
            }

            var taskResult = task.Result;
            if (!taskResult)
                return groupMemberList;

            if (Options.Instance.DumpComputerStatus)
                OutputTasks.AddComputerStatus(new ComputerStatus
                {
                    ComputerName = computer.DisplayName,
                    Status = "Success",
                    Task = $"GetNetLocaGroup-{rid}"
                });

            //Take our pointers to sids and convert them into string sids for matching
            var convertedSids = new List<string>();
            for (var i = 0; i < sids.Length; i++)
            {
                try
                {
                    var sid = new SecurityIdentifier(sids[i]).Value;
                    convertedSids.Add(sid);
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    //Set the IntPtr to zero, so we can GC those
                    sids[i] = IntPtr.Zero;
                }
            }
            //Null out sids, so garbage collection takes care of it
            sids = null;

            //Extract the domain SID from the computer's sid, to avoid creating more SecurityIdentifier objects
            var domainSid = computer.ObjectIdentifier.Substring(0, computer.ObjectIdentifier.LastIndexOf('-'));
            string machineSid;
            // The first account in our list should always be the default RID 500 for the machine, but we'll take some extra precautions
            try
            {
                machineSid = convertedSids.First(x => x.EndsWith("-500") && !x.StartsWith(domainSid));
            }
            catch
            {
                machineSid = "DUMMYSTRING";
            }

            foreach (var sid in convertedSids)
            {
                if (sid.StartsWith(machineSid))
                    continue;

                LdapTypeEnum type;
                string finalSid = sid;
                if (CommonPrincipal.GetCommonSid(finalSid, out var common))
                {
                    finalSid = Helpers.ConvertCommonSid(sid, null);
                    type = common.Type;
                }
                else
                {
                    type = await Helpers.LookupSidType(sid);
                }

                groupMemberList.Add(new GroupMember
                {
                    MemberType = type,
                    MemberId = finalSid
                });
            }

            return groupMemberList;
        }

        /// <summary>
        /// Modified version of GetNetLocalGroupMembers which eliminates several unnecessary LSA/SAMRPC calls
        /// </summary>
        /// <param name="computer"></param>
        /// <param name="rid"></param>
        /// <param name="sids"></param>
        /// <returns></returns>
        private static bool CallLocalGroupApi(Computer computer, LocalGroupRids rid, out IntPtr[] sids)
        {
            //Initialize pointers for later
            var serverHandle = IntPtr.Zero;
            var domainHandle = IntPtr.Zero;
            var aliasHandle = IntPtr.Zero;
            var members = IntPtr.Zero;
            sids = new IntPtr[0];
            
            //Create some objects required for SAMRPC calls
            var server = new UNICODE_STRING(computer.APIName);
            var objectAttributes = new OBJECT_ATTRIBUTES();

            try
            {
                //Step 1: Call SamConnect to open a handle to the computer's SAM
                //0x1 = SamServerLookupDomain, 0x20 = SamServerConnect
                var status = SamConnect(ref server, out serverHandle, 0x1 | 0x20, ref objectAttributes);

                switch (status)
                {
                    case NtStatus.StatusRpcServerUnavailable:
                        if (Options.Instance.DumpComputerStatus)
                            OutputTasks.AddComputerStatus(new ComputerStatus
                            {
                                ComputerName = computer.DisplayName,
                                Status = status.ToString(),
                                Task = $"GetNetLocalGroup-{rid}"
                            });
                        
                        return false;
                    case NtStatus.StatusSuccess:
                        break;
                    default:
                        if (Options.Instance.DumpComputerStatus)
                            OutputTasks.AddComputerStatus(new ComputerStatus
                            {
                                ComputerName = computer.DisplayName,
                                Status = status.ToString(),
                                Task = $"GetNetLocalGroup-{rid}"
                            });
                        return false;
                }

                //Step 2 - Open the built in domain, which is identified by the SID S-1-5-32
                //0x200 = Lookup
                status = SamOpenDomain(serverHandle, 0x200, LocalSidBytes.Value, out domainHandle);

                if (status != NtStatus.StatusSuccess)
                {
                    if (Options.Instance.DumpComputerStatus)
                        OutputTasks.AddComputerStatus(new ComputerStatus
                        {
                            ComputerName = computer.DisplayName,
                            Status = status.ToString(),
                            Task = $"GetNetLocalGroup-{rid}"
                        });
                    return false;
                }

                //Step 3 - Open the alias that corresponds to the group we want to enumerate.
                //0x4 = ListMembers
                status = SamOpenAlias(domainHandle, 0x4, (int)rid, out aliasHandle);

                if (status != NtStatus.StatusSuccess)
                {
                    if (Options.Instance.DumpComputerStatus)
                        OutputTasks.AddComputerStatus(new ComputerStatus
                        {
                            ComputerName = computer.DisplayName,
                            Status = status.ToString(),
                            Task = $"GetNetLocalGroup-{rid}"
                        });

                }

                //Step 4 - Get the members of the alias we opened in step 3. 
                status = SamGetMembersInAlias(aliasHandle, out members, out var count);

                if (status != NtStatus.StatusSuccess)
                {
                    if (Options.Instance.DumpComputerStatus)
                        OutputTasks.AddComputerStatus(new ComputerStatus
                        {
                            ComputerName = computer.DisplayName,
                            Status = status.ToString(),
                            Task = $"GetNetLocalGroup-{rid}"
                        });
                    return false;
                }

                //If we didn't get any objects, just return false
                if (count == 0)
                {
                    return false;
                }

                //Copy the IntPtr to an array so we can loop over it
                sids = new IntPtr[count];
                Marshal.Copy(members, sids, 0, count);

                return true;
            }
            finally
            {
                //Free memory from handles acquired during the process
                if (serverHandle != IntPtr.Zero)
                    SamCloseHandle(serverHandle);
                if (domainHandle != IntPtr.Zero)
                    SamCloseHandle(domainHandle);
                if (aliasHandle != IntPtr.Zero)
                    SamCloseHandle(aliasHandle);

                if (members != IntPtr.Zero)
                    SamFreeMemory(members);
            }
        }

        private enum LocalGroupRids
        {
            Administrators = 544,
            RemoteDesktopUsers = 555,
            DcomUsers = 562,
            PsRemote = 580
        }

        #region SamRPC Imports

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamConnect(ref UNICODE_STRING serverName, out IntPtr serverHandle, int desiredAccess,
            ref OBJECT_ATTRIBUTES objectAttributes);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamOpenDomain(IntPtr serverHandle, int desiredAccess, IntPtr domainId,
            out IntPtr domainHandle);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamLookupDomainInSamServer(IntPtr serverHandle, ref UNICODE_STRING name,
            out IntPtr securityIdentifier);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamOpenDomain(IntPtr serverHandle, int desiredAccess,
            [MarshalAs(UnmanagedType.LPArray)] byte[] securityIdentifierBytes, out IntPtr domainHandle);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamOpenAlias(IntPtr domainHandle, int desiredAccess, int aliasId,
            out IntPtr aliasHandle);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamGetMembersInAlias(IntPtr aliasHandle, out IntPtr members, out int count);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamCloseHandle(IntPtr handle);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamFreeMemory(IntPtr pointer);

        [DllImport("samlib.dll")]
        internal static extern NtStatus SamEnumerateDomainsInSamServer(IntPtr serverHandle, ref int enumerationContext,
            out IntPtr domains, int PrefMaxLen, out int count);
        #endregion

        #region PInvoke Structs/Enums

        internal enum NtStatus
        {
            StatusSuccess = 0x0,
            StatusMoreEntries = 0x105,
            StatusSomeMapped = 0x107,
            StatusInvalidHandle = unchecked((int)0xC0000008),
            StatusInvalidParameter = unchecked((int)0xC000000D),
            StatusAccessDenied = unchecked((int)0xC0000022),
            StatusObjectTypeMismatch = unchecked((int)0xC0000024),
            StatusNoSuchDomain = unchecked((int)0xC00000DF),
            StatusRpcServerUnavailable = unchecked((int)0xC0020017),
            StatusRpcCallFailedDidNotExecute = unchecked((int)0xC002001C)
        }

        internal struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            [MarshalAs(UnmanagedType.LPWStr)] private string buffer;

            internal UNICODE_STRING(string s)
            {
                if (string.IsNullOrEmpty(s))
                    buffer = string.Empty;
                else
                    buffer = s;

                Length = (ushort)(2 * buffer.Length);
                MaximumLength = Length;
            }

            public override string ToString()
            {
                if (Length != 0)
                    return buffer.Substring(0, (int)(Length / 2));

                return string.Empty;
            }
        }

        internal struct OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr QualityOfService;
            private IntPtr _objectName;
            public UNICODE_STRING ObjectName;

            public void Dispose()
            {
                if (_objectName == IntPtr.Zero)
                    return;

                Marshal.DestroyStructure(_objectName, typeof(UNICODE_STRING));
                Marshal.FreeHGlobal(_objectName);
                _objectName = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SamSidEnumeration
        {
            public IntPtr sid;
            public UNICODE_STRING Name;
        }
        #endregion
    }
}