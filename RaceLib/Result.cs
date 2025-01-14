﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaceLib
{
    public class Result : BaseDBObject
    {
        public int Points { get; set; }
        public int Position { get; set; }

        public bool Valid { get; set; }

        [System.ComponentModel.Browsable(false)]
        [LiteDB.BsonRef("Event")]
        public Event Event { get; set; }

        [System.ComponentModel.Browsable(false)]
        [LiteDB.BsonRef("Pilot")]
        public Pilot Pilot { get; set; }

        [System.ComponentModel.Browsable(false)]
        [LiteDB.BsonRef("Race")]
        public Race Race { get; set; }

        [System.ComponentModel.Browsable(false)]
        [LiteDB.BsonRef("Round")]
        public Round Round { get; set; }

        public bool DNF { get; set; }

        public enum ResultTypes
        {
            Race,
            RoundRollOver
        }

        public ResultTypes ResultType { get; set; }

        public Result()
        {
            Valid = true;
        }

        public override string ToString()
        {
            if (DNF)
                return Pilot.Name + " DNF";

            return Pilot.Name + " " + Points + " " + Position.ToStringPosition();
        }
    }
}
