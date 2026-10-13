using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaceLib
{
    // A pilot's time through one sector of a lap. Split timing systems divide each lap into sectors:
    // sector 1 ends at the first split timing system, sector 2 at the next and so on, with the last
    // sector ending at the end of the lap.
    public class SectorTime
    {
        public Race Race { get; private set; }

        // The detection that ends the sector. For the last sector it's the lap's.
        public Detection Detection { get; private set; }

        public DateTime Start { get; private set; }
        public DateTime End { get; private set; }
        public TimeSpan Length { get { return End - Start; } }

        public bool Valid { get; private set; }

        public int Sector { get; private set; }
        public int LapNumber { get; private set; }

        public SectorTime(Race race, Detection detection, DateTime start, DateTime end, bool valid, int sector, int lapNumber)
        {
            Race = race;
            Detection = detection;
            Start = start;
            End = end;
            Valid = valid;
            Sector = sector;
            LapNumber = lapNumber;
        }

        public override string ToString()
        {
            return "L" + LapNumber + " S" + Sector + " " + Length.TotalSeconds.ToString("0.00");
        }
    }
}
