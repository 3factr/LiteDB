﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LiteDB.Engine
{
    internal enum DbFileMode : byte
    {
        Datafile = 0,
        Logfile = 1
    }
}