﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaceLib
{
    public class Club : BaseDBObject
    {
        public string Name { get; set; }

        public SyncWith SyncWith { get; set; }

        public Club()
        {
        }
    }
}
