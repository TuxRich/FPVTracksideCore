﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Spreadsheets;
using Tools;

namespace RaceLib.Format
{
    public class SheetFormatManager : IDisposable
    {
        public RoundManager RoundManager { get; private set; }
        public EventManager EventManager { get; private set; }
        public RaceManager RaceManager { get; private set; }

        private List<RoundSheetFormat> roundSheetFormats;

        private List<SheetFile> sheets;

        public IEnumerable<SheetFile> Sheets
        {
            get
            {
                return sheets;
            }
        }

        public SheetFormatManager(RoundManager roundManager)
            :this(roundManager, new DirectoryInfo("formats"))
        {
        }

        public SheetFormatManager(RoundManager roundManager, DirectoryInfo directory)
        {
            RoundManager = roundManager;
            EventManager = roundManager.EventManager;
            RaceManager = EventManager.RaceManager;

            EventManager.OnPilotRefresh += EventManager_OnPilot;

            roundSheetFormats = new List<RoundSheetFormat>();

            sheets = GetSheetFiles(directory).ToList();
        }
        
        public bool CanAddFormat(Round round)
        {
            if (!sheets.Any())
                return false;

            Round next = RoundManager.NextRound(round);
            if (next != null)
            {
                if (next.HasSheetFormat)
                    return false;

                if (RaceManager.GetRaces(next).Any())
                    return false;
            }

            return true;
        }

        public void Load()
        {
            foreach (Round r in RoundManager.Rounds)
            {
                if (r.HasSheetFormat)
                {
                    LoadSheet(r, null, false);
                }
            }
        }

        public void Dispose()
        {
            EventManager.OnPilotRefresh -= EventManager_OnPilot;

            foreach (var format in roundSheetFormats)
            {
                format.Dispose();
            }
        }

        private IEnumerable<SheetFile> GetSheetFiles(DirectoryInfo directory)
        {
            if (directory.Exists)
            {
                foreach (FileInfo fileInfo in directory.GetFiles("*.xlsx"))
                {
                    if (fileInfo.Name.StartsWith("~"))
                        continue;

                    SheetFile sheetFile = null;
                    try
                    {
                        sheetFile = new SheetFile(fileInfo);
                    }
                    catch (Exception e)
                    {
                        Logger.AllLog.LogException(this, e);
                    }

                    if (sheetFile != null)
                    {
                        yield return sheetFile;
                    }
                }
            }
        }

        private void EventManager_OnPilot()
        {
            foreach (var format in roundSheetFormats)
            {
                format.CreatePilotMap(null);
            }
        }


        public SheetFile GetSheetFile(string filename)
        {
            SheetFile sheetFile = sheets.FirstOrDefault(r => r.FileInfo.Name == filename);
            if (sheetFile != null)
            {
                if (sheetFile.FileInfo.Exists)
                {
                    return sheetFile;
                }
            }

            return null;
        }

        public RoundSheetFormat GetRoundSheetFormat(Round round)
        {
            return roundSheetFormats.FirstOrDefault(r => r.HasRound(round));
        }


        public void LoadSheet(Round startRound, Pilot[] assignedPilots, bool generate)
        {
            if (startRound.HasSheetFormat)
            {
                SheetFile sheetFile = GetSheetFile(startRound.SheetFormatFilename);
                RoundSheetFormat sheetFormat = new RoundSheetFormat(startRound, this, sheetFile.FileInfo);
                sheetFormat.CreatePilotMap(assignedPilots);

                foreach (Round round in sheetFormat.Rounds)
                {
                    IEnumerable<Race> endedRaces = RaceManager.GetRaces(r => r.Round == round && r.Ended);
                    foreach (Race race in endedRaces)
                    {
                        sheetFormat.SetResult(race);
                    }
                }

                if (generate)
                {
                    sheetFormat.GenerateRounds();
                }

                roundSheetFormats.Add(sheetFormat);
            }
        }

        public void OnRaceResultChange(Race race)
        {
            foreach (var format in roundSheetFormats)
            {
                if (format.HasRound(race.Round))
                {
                    format.SetResult(race);
                    format.GenerateRounds();
                }
            }
        }

        public class SheetFile
        {
            public int Channels { get; private set; }
            public int Pilots { get; private set; }
            public string Name { get; private set; }

            public FileInfo FileInfo { get; private set; }

            public SheetFile(FileInfo fileInfo)
            {
                FileInfo = fileInfo;
                
                SheetFormat sheetFormat = new SheetFormat(FileInfo);
                Pilots = sheetFormat.GetPilots().Count();
                Channels = sheetFormat.Channels;
                Name = sheetFormat.Name;
            }
        }
    }

    public class RoundSheetFormat : IDisposable
    {
        private Dictionary<string, Pilot> pilotMap;
        
        public SheetFormatManager SheetFormatManager { get; private set; }

        public Round StartRound { get; private set; }
        public List<Round> Rounds { get; private set; }
        public SheetFormat SheetFormat { get; private set; }

        public Action OnGenerate;

        public string Name
        {
            get
            {
                return SheetFormat.Name;
            }
        }

        public int Offset
        {
            get
            {
                if (StartRound == null)
                    return 0;

                return StartRound.RoundNumber - 1;
            }
        }

        public RoundSheetFormat(Round startRound, SheetFormatManager sheetFormatManager, FileInfo file)
        {
            SheetFormatManager = sheetFormatManager;
            StartRound = startRound;
            Rounds = new List<Round>();
            SheetFormat = new SheetFormat(file);
            pilotMap = new Dictionary<string, Pilot>();
        }
        public void Dispose()
        {
            SheetFormat.Dispose();
        }

        public bool HasRound(Round round)
        {
            lock (Rounds)
            {
                return Rounds.Contains(round);
            }
        }

        public void CreatePilotMap(Pilot[] assignedPilots)
        {
            pilotMap.Clear();

            string[] sheetPilots = null;

            Round round = GetCreateRounds().FirstOrDefault();

            // If we don't have assigned pilots, we're probably continuing...
            if (assignedPilots == null || !assignedPilots.Any())
            {
                assignedPilots = SheetFormatManager.RaceManager.GetRaces(round).OrderBy(r => r.RaceNumber).SelectMany(r => r.Pilots).ToArray();
                sheetPilots = SheetFormat.GetFirstRoundPilots().ToArray();
            }
            else
            { 
                sheetPilots = SheetFormat.GetPilots().ToArray();
            }

            if (assignedPilots == null || !assignedPilots.Any())
            {
                assignedPilots = SheetFormatManager.EventManager.Event.Pilots.ToArray();
            }

            int length = Math.Min(assignedPilots.Length, sheetPilots.Length);

            int i;
            for (i = 0; i < length; i++)
            {
                if (!pilotMap.ContainsKey(sheetPilots[i]))
                {
                    pilotMap.Add(sheetPilots[i], assignedPilots[i]);
                }
            }

            for (; i < sheetPilots.Length; i++)
            {
                if (!pilotMap.ContainsKey(sheetPilots[i]))
                {
                    Pilot pilot = SheetFormatManager.EventManager.Event.Pilots.Where(p => !pilotMap.ContainsValue(p)).FirstOrDefault();
                    if (pilot != null)
                    {
                        pilotMap.Add(sheetPilots[i], pilot);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public bool GetPilotMapped(string text, out Pilot pilot)
        {
            return pilotMap.TryGetValue(text, out pilot);
        }

        public IEnumerable<Round> GetCreateRounds()
        {
            string[] rounds = SheetFormat.GetRounds().ToArray();

            lock (Rounds)
            {
                Rounds.Clear();

                foreach (string round in rounds)
                {
                    Round r = GetCreateRound(round);
                    if (r != null)
                    {
                        Rounds.Add(r);
                    }
                }
                return Rounds.ToArray();
            }

        }

        public Round GetCreateRound(string name)
        {
            string[] splits = name.Split(' ');

            EventTypes eventType = EventTypes.Race;
            int round = -1;

            foreach (string s in splits)
            {
                EventTypes t = GetEventType(s);
                if (t >= 0)
                {
                    eventType = t;
                }
                int i;
                if (int.TryParse(s, out i))
                {
                    round = i;
                }
            }

            if (round > 0)
            {
                if (round == 1 && StartRound.EventType != eventType)
                {
                    using (Database db = new Database())
                    {
                        StartRound.EventType = eventType;
                        db.Rounds.Update(StartRound);
                    }
                }

                return SheetFormatManager.RoundManager.GetCreateRound(round + Offset, eventType);
            }

            return null;
        }

        public void GenerateRounds()
        {
            Round[] rounds = GetCreateRounds().ToArray();

            foreach (Round round in rounds)
            {
                var races = SheetFormatManager.RaceManager.GetRaces(round);
                if (!races.All(r => r.Ended) || !races.Any())
                {
                    GenerateRound(round);
                }
            }
            OnGenerate?.Invoke();
        }

        public void GenerateSingleRound(Round round)
        {
            GenerateRound(round);
            OnGenerate?.Invoke();
        }

        private void GenerateRound(Round round)
        {
            int count = 0;

            var brackets = Enum.GetValues(typeof(Race.Brackets)).OfType<Race.Brackets>().Where(e => e >= Race.Brackets.A && e <= Race.Brackets.Z).ToArray();

            SheetRace[] sfRaces = SheetFormat.GetRaces(round.EventType.ToString(), round.RoundNumber - Offset).ToArray();
            foreach (SheetRace sfRace in sfRaces)
            {
                Race race = GetCreateRace(round, sfRace);
                if (SheetFormat.CreateBrackets && count < brackets.Length)
                {
                    race.Bracket = brackets[count];
                }
                count++;
            }
        }

        private Race GetCreateRace(Round round, SheetRace srace)
        {
            Race race = SheetFormatManager.RaceManager.GetCreateRace(round, srace.Number);
            if (race.Ended)
            {
                return race;
            }

            using (Database db = new Database())
            {
                race.ClearPilots(db);

                foreach (var pc in srace.PilotChannels)
                {
                    IEnumerable<Channel> channels = SheetFormatManager.EventManager.GetChannelGroup(pc.ChannelSlot);
                    Pilot pilot = GetPilot(pc.Pilot);
                    if (pilot != null && channels.Any())
                    {
                        Channel currentChannel = SheetFormatManager.EventManager.GetChannel(pilot);
                        if (currentChannel != null)
                        {
                            Channel channel = channels.FirstOrDefault(r => r.Band.GetBandType() == currentChannel.Band.GetBandType());
                            if (channel == null)
                            {
                                channel = channels.FirstOrDefault();
                            }

                            if (channel != null)
                            {
                                race.SetPilot(db, channel, pilot);
                            }
                        }
                    }
                }

                if (!SheetFormat.LockChannels && !race.Ended)
                {
                    SheetFormatManager.EventManager.RaceManager.OptimiseChannels(db, race);
                }
            }

            return race;
        }

        public void SetResult(Race race)
        {
            List<SheetResult> results = new List<SheetResult>();

            foreach (var pc in race.PilotChannelsSafe)
            {
                Result result = SheetFormatManager.EventManager.ResultManager.GetResult(race, pc.Pilot);

                int channelGroup = SheetFormatManager.EventManager.GetChannelGroupIndex(pc.Channel);
                string pilotName = GetPilotRef(pc.Pilot);

                if (!string.IsNullOrEmpty(pilotName) && result != null)
                {
                    SheetResult sr;
                    if (result.DNF)
                    {
                        sr = new SheetResult(pilotName, channelGroup, " ");
                    }
                    else
                    {
                        sr = new SheetResult(pilotName, channelGroup, result.Position);
                    }

                    results.Add(sr);
                }
            }
            SheetFormat.SetResults(race.Round.EventType.ToString(), race.RoundNumber - Offset, race.RaceNumber, results);
        }

        private Pilot GetPilot(string pilotRef)
        {
            Pilot p;
            if (pilotMap.TryGetValue(pilotRef, out p))
            {
                return p;
            }
            return null;
        }

        private string GetPilotRef(Pilot pilot)
        {
            foreach (var kvp in pilotMap)
            {
                if (pilot == kvp.Value)
                {
                    return kvp.Key;
                }
            }
            return "";
        }

        public EventTypes GetEventType(string eventtypestring)
        {
            foreach (EventTypes eventType in Enum.GetValues(typeof(EventTypes)))
            {
                if (eventType.ToString().ToLower() == eventtypestring.ToLower())
                {
                    return eventType;
                }
            }

            return (EventTypes)(-1);
        }
        public void Save(string fileName)
        {
            IEnumerable<string> eventTypes = Enum.GetValues(typeof(EventTypes)).OfType<EventTypes>().Select(r => r.ToString());

            Dictionary<string, string> pilotNameMap = pilotMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);
            SheetFormat.Save(fileName, pilotNameMap, eventTypes);
        }

    }
}
