﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform_Manhattan.DataModel
{
    public class ManhattanLocationData
    {
        public int location_id { get; set; } // Location External Identifier
        public string warehouse_id { get; set; } // Manhattan Warehouse ID
        public string shipcenter_location { get; set; }
    }
}
