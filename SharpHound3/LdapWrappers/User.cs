﻿using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpHound3.JSON;

namespace SharpHound3.LdapWrappers
{
    internal class User : LdapWrapper
    {
        internal User(SearchResultEntry entry) : base(entry)
        {
            AllowedToDelegate = new string[0];
            SPNTargets = new SPNTarget[0];
        }

        public string[] AllowedToDelegate { get; set; }

        public SPNTarget[] SPNTargets { get; set; }

        public string PrimaryGroupSid { get; set; }
    }
}
