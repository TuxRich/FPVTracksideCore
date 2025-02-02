﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RaceLib
{
    public class LapRecordManager
    {
        public Dictionary<Pilot, PilotLapRecord> Records { get; private set; }

        public int[] ConsecutiveLapsToTrack { get; set; }

        public event PilotLapRecord.NewRecord OnNewPersonalBest;
        public event PilotLapRecord.NewRecord OnNewOveralBest;

        public RaceManager RaceManager { get; private set; }
        public EventManager EventManager { get; private set; }

        private Dictionary<int, Lap[]> overallBest;

        public LapRecordManager(RaceManager rm)
        {
            RaceManager = rm;
            rm.OnLapDetected += RecordLap;
            rm.OnLapDisqualified += DisqLap;
            rm.OnLapSplit += Rm_OnLapSplit;
            rm.OnRaceRemoved += Rm_OnRaceRemoved; 

            EventManager = RaceManager.EventManager;
            EventManager.OnEventChange += UpdateAll;

            Records = new Dictionary<Pilot, PilotLapRecord>();
            ConsecutiveLapsToTrack = new int[0];
            overallBest = new Dictionary<int, Lap[]>();
        }

        private void Rm_OnRaceRemoved(Race race)
        {
            UpdateAll();
        }

        private void Rm_OnLapSplit(IEnumerable<Lap> laps)
        {
            Pilot pilot = laps.Select(l => l.Pilot).FirstOrDefault();
            if (pilot != null)
            {
                UpdatePilot(pilot);
            }
        }

        public int GetTimePosition(Pilot pilot)
        {
            return GetPosition(pilot, EventManager.Event.Laps);
        }

        public int GetPBTimePosition(Pilot pilot)
        {
            return GetPosition(pilot, EventManager.Event.PBLaps);
        }

        public int GetPosition(Pilot pilot, int laps)
        {
            int position;
            Pilot behindWho;
            TimeSpan behind;

            GetPosition(pilot, laps, out position, out behindWho, out behind);
            return position;
        }

        public bool GetPosition(Pilot pilot, int laps, out int position, out Pilot behindWho, out TimeSpan behind)
        {
            lock (Records)
            {
                position = Records.Count;
                behindWho = null;
                behind = TimeSpan.Zero;

                if (pilot == null)
                    return false;

                if (!ConsecutiveLapsToTrack.Contains(laps))
                    return false;

                PilotLapRecord thisRecord = GetPilotLapRecord(pilot);

                TimeSpan thisTime = thisRecord.GetBestConsecutiveLaps(laps).TotalTime();
                if (thisTime == TimeSpan.MaxValue)
                    return false;
                position = 1;

                IEnumerable<PilotLapRecord> ordered = Records.Values.OrderBy(record => record.GetBestConsecutiveLaps(laps).TotalTime()).ThenBy(plr => plr.Pilot.Name);
                PilotLapRecord prev = null;
                foreach (PilotLapRecord record in ordered)
                {
                    if (record.Pilot == thisRecord.Pilot)
                    {
                        if (prev != null)
                        {
                            TimeSpan prevTime = prev.GetBestConsecutiveLaps(laps).TotalTime();
                            behindWho = prev.Pilot;
                            behind = thisTime - prevTime;
                        }
                        return true;
                    }
                    position++;
                    prev = record;
                }
            }
            return false;
        }

        private void UpdateLapCounts()
        {
            if (EventManager.Event != null)
            {
                List<int> toAdd = new List<int>();
                toAdd.Add(1);
                toAdd.Add(EventManager.Event.PBLaps);
                toAdd.Add(EventManager.Event.Laps);

                if (EventManager.Event.PrimaryTimingSystemLocation == PrimaryTimingSystemLocation.Holeshot)
                {
                    toAdd.Add(0);
                }

                ConsecutiveLapsToTrack = toAdd.Distinct().OrderBy(i => i).ToArray();
            }
        }

        public void RecordLap(Lap lap)
        {
            Race race = lap.Race;
            if (race == null)
                return;

            Lap[] laps = race.GetLaps(lap.Pilot);
            
            if (IsRecord(lap.Pilot, laps))
            {
                UpdatePilot(lap.Pilot);
            }
        }

        private bool IsRecord(Pilot pilot, Lap[] laps)
        {
            if (laps.Any())
            {
                PilotLapRecord plr = GetPilotLapRecord(pilot);
                foreach (int consecutive in ConsecutiveLapsToTrack)
                {
                    if (plr.GetBestConsecutiveLaps(consecutive).TotalTime() > laps.BestConsecutive(consecutive).TotalTime())
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public void DisqLap(Lap lap)
        {
            if (overallBest.Values.SelectMany(s => s).Contains(lap))
            {
                overallBest.Clear();
                UpdateAll();
            }
            else
            {
                UpdatePilot(lap.Pilot);
            }
        }

        public void ResetRace(Race race)
        {
            foreach (Pilot p in race.Pilots)
            {
                UpdatePilot(p);
            }
        }

        public void UpdatePilot(Pilot pilot)
        {
            PilotLapRecord plr = GetPilotLapRecord(pilot);
            foreach (int consecutive in ConsecutiveLapsToTrack)
            {
                plr.UpdateBestConsecutiveLaps(consecutive);
            }
        }

        private PilotLapRecord GetPilotLapRecord(Pilot pilot)
        {
            if (pilot == null)
                return null;

            PilotLapRecord plr = null;
            lock (Records)
            {
                if (!Records.TryGetValue(pilot, out plr))
                {
                    plr = new PilotLapRecord(this, pilot);
                    Records.Add(pilot, plr);
                    plr.OnNewBest += OnNewPilotBest;
                }
            }

            return plr;
        }

        private void OnNewPilotBest(Pilot p, int lapCount, Lap[] laps)
        {
            OnNewPersonalBest?.Invoke(p, lapCount, laps);

            if (laps.Length != 0 && IsOverallBest(lapCount, laps))
            {
                OnNewOveralBest?.Invoke(p, lapCount, laps);
            }
        }

        public bool IsOverallBest(int lapCount, Lap[] laps)
        {
            bool isBest = false;

            if (laps.Length == 0)
            {
                return false;
            }

            Lap[] bestLaps;
            if (overallBest.TryGetValue(lapCount, out bestLaps))
            {
                if (bestLaps.TotalTime() > laps.TotalTime())
                {
                    overallBest[lapCount] = laps;
                    isBest = true;
                }

                if (bestLaps.FirstOrDefault() == laps.FirstOrDefault() && bestLaps.FirstOrDefault() != null)
                {
                    isBest = true;
                }
            }
            else
            {
                overallBest.Add(lapCount, laps);
                isBest = true;
            }

            return isBest;
        }

        public void UpdateAll()
        {
            UpdatePilots(Records.Keys);
        }

        public void UpdatePilots(IEnumerable<Pilot> pilots)
        {
            UpdateLapCounts();

            foreach (Pilot p in pilots)
            {
                GetPilotLapRecord(p);
            }

            lock (Records)
            {
                foreach (PilotLapRecord plr in Records.Values)
                {
                    plr.Clear();
                }

                foreach (PilotLapRecord plr in Records.Values)
                {
                    foreach (int consecutive in ConsecutiveLapsToTrack)
                    {
                        plr.UpdateBestConsecutiveLaps(consecutive);
                    }
                }
            }
        }

        public Lap[] GetPBLaps(Pilot pilot)
        {
            Lap[] output;
            bool overall;
            if (GetBestLaps(pilot, EventManager.Event.PBLaps, out output, out overall))
            {
                return output;
            }

            return new Lap[0];
        }

        public bool GetBestLaps(Pilot pilot, int lapCount, out Lap[] laps, out bool overalBest)
        {
            PilotLapRecord plr = null;
            if (Records.TryGetValue(pilot, out plr))
            {
                laps = plr.GetBestConsecutiveLaps(lapCount);
                if (laps != null && laps.Any())
                {
                    overalBest = IsOverallBest(lapCount, laps);
                    return true;
                }
            }

            overalBest = false;
            laps = new Lap[0];
            return false;
        }

        public bool IsRecordLap(Lap lap, out bool overalBest)
        {
            int lapCount = 1;
            overalBest = false;

            Lap[] laps;
            if (overallBest.TryGetValue(lapCount, out laps))
            {
                if (laps[0].ID == lap.ID)
                {
                    overalBest = true;
                    return true;
                }
            }

            PilotLapRecord plr = GetPilotLapRecord(lap.Pilot);
            if (plr != null)
            {
                laps = plr.GetBestConsecutiveLaps(lapCount);
                if (laps.Any())
                {
                    if (laps[0].ID == lap.ID)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public string ExportPBsCSV()
        {
            List<string> line = new List<string>();

            string output = "";

            line.Add("Pilot");

            foreach (int lap in ConsecutiveLapsToTrack)
            {
                if (lap == 0)
                {
                    line.Add("Holeshot");
                }
                else if (lap == 1)
                {
                    line.Add(lap.ToString() + " Lap");
                }
                else
                {
                    line.Add(lap.ToString() + " Laps");
                }
            }

            line.Add("");

            int[] morethanone = ConsecutiveLapsToTrack.Where(r => r > 1).ToArray();

            foreach (int consecutive in morethanone)
            {
                for (int i = 0; i < consecutive; i++)
                {
                    line.Add(consecutive + " Laps - Lap " + (i + 1));
                }
                line.Add("");
            }

            output += string.Join(",", line.ToArray()) + "\n";

            lock (Records)
            {
                foreach (var plr in Records.Values.Where(p => !p.Pilot.PracticePilot).OrderBy(r => r.Pilot.Name))
                {
                    line.Clear();

                    line.Add(plr.Pilot.Name);

                    foreach (int consecutive in ConsecutiveLapsToTrack)
                    {
                        Lap[] laps = plr.GetBestConsecutiveLaps(consecutive);
                        if (laps != null && laps.Any())
                        {
                            TimeSpan timeSpan = laps.TotalTime();
                            line.Add(timeSpan.TotalSeconds.ToString("0.000"));
                        }
                        else
                        {
                            line.Add("");
                        }
                    }

                    line.Add("");

                    foreach (int consecutive in morethanone)
                    {
                        Lap[] laps = plr.GetBestConsecutiveLaps(consecutive);

                        foreach (Lap lap in laps)
                        {
                            line.Add(lap.Length.TotalSeconds.ToString("0.000"));
                        }
                        line.Add("");
                    }

                    output += string.Join(",", line.ToArray()) + "\n";
                }
            }
            return output;
        }

        public void Clear()
        {
            overallBest.Clear();
            Records.Clear();
        }

        public void ClearPilot(Pilot pilot)
        {
            lock (Records)
            {
                Records.Remove(pilot);
            }
        }

        public IEnumerable<Lap> GetBestLaps(IEnumerable<Race> races, Pilot pilot, int consecutive)
        {
            IEnumerable<Lap> laps = races.SelectMany(r => r.GetValidLaps(pilot, false));
            return laps.BestConsecutive(consecutive);
        }

        public Pilot[] GetPositions(IEnumerable<Pilot> pilots, int laps)
        {
            return Records.Values.Where(r => pilots.Contains(r.Pilot)).OrderBy(pr => pr.GetBestConsecutiveLaps(laps).TotalTime().TotalSeconds).Select(pr => pr.Pilot).ToArray();
        }

    }

    public class PilotLapRecord
    {
        public delegate void NewRecord(Pilot p, int recordLapCount, Lap[] laps);

        public event NewRecord OnNewBest;

        public Pilot Pilot { get; private set; }
        private Dictionary<int, Lap[]> best;

        public LapRecordManager RecordManager { get; private set; }

        public string[] Records
        {
            get
            {
                return best.Select(kvp => kvp.Key + ", " + kvp.Value.TotalTime().TotalSeconds).ToArray();
            }
        }

        public PilotLapRecord(LapRecordManager recordManager, Pilot p)
        {
            RecordManager = recordManager;
            Pilot = p;
            best = new Dictionary<int, Lap[]>();
        }

        public void SetBestConsecutiveLaps(IEnumerable<Lap> laps, int lapCount)
        {
            Lap[] aLaps = laps.ToArray();
            if (best.ContainsKey(lapCount))
            {
                best[lapCount] = aLaps;
            }
            else
            {
                best.Add(lapCount, aLaps);
            }
        }

        public Lap[] GetBestConsecutiveLaps(int lapCount)
        {
            Lap[] laps = null;
            if (!best.TryGetValue(lapCount, out laps))
            {
                return new Lap[0];
            }
            return laps;
        }

        public void UpdateBestConsecutiveLaps(int lapCount)
        {
            Lap[] bestLaps;
            
            if (lapCount == 0)
            {
                bestLaps = BestHoleshot().ToArray();
            }
            else
            {
                bestLaps = TopConsecutive(lapCount).ToArray();
            }

            if (bestLaps.Any())
            {
                if (best.ContainsKey(lapCount))
                {
                    bool oldBestInvalid = best[lapCount].Any(l => !l.Detection.Valid);

                    if (best[lapCount].TotalTime() != bestLaps.TotalTime() || oldBestInvalid)
                    {
                        SetBestConsecutiveLaps(bestLaps.ToArray(), lapCount);

                        OnNewBest?.Invoke(Pilot, lapCount, bestLaps);
                    }
                }
                else
                {
                    SetBestConsecutiveLaps(bestLaps.ToArray(), lapCount);
                    OnNewBest?.Invoke(Pilot, lapCount, bestLaps);
                }
            }
            else
            {
                best.Remove(lapCount);
                OnNewBest?.Invoke(Pilot, lapCount, new Lap[0]);
            }
        }

        public void Clear()
        {
            best.Clear();
        }

        private IEnumerable<Lap> BestHoleshot()
        {
            Race[] races = RecordManager.RaceManager.GetRaces(r => r.Type != EventTypes.Practice && r.HasPilot(Pilot)).ToArray();

            Lap best = null;

            foreach (Race race in races)
            {
                Lap holeShot = race.GetHoleshot(Pilot);
                if (holeShot == null)
                    continue;

                if (best == null)
                {
                    best = holeShot;
                }
                else if (best.Length > holeShot.Length)
                {
                    best = holeShot;
                }
            }

            if (best != null)
                yield return best;
        }

        private IEnumerable<Lap> TopConsecutive(int consecutive)
        {
            Race[] races = RecordManager.RaceManager.GetRaces(r => r.HasPilot(Pilot) && r.Type != EventTypes.Practice).ToArray();

            IEnumerable<Lap> best = null;

            foreach (Race r in races)
            {
                IEnumerable<Lap> inRace = r.GetLaps(Pilot);
                IEnumerable<Lap> bestInRace = inRace.BestConsecutive(consecutive);
                if (bestInRace.Any() && (best == null || bestInRace.TotalTime() < best.TotalTime()))
                {
                    best = bestInRace;
                }
            }

            if (best != null)
            {
#if DEBUG
                System.Diagnostics.Debug.Assert(best.Count() == consecutive);
#endif
                return best;
            }
            else
            {
                return new Lap[0];
            }
        }
    }
}
