﻿using Microsoft.Xna.Framework;
using RaceLib.Format;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Timing;
using Tools;

namespace RaceLib
{
    public class EventManager : IDisposable
    {
        public RaceManager RaceManager { get; private set; }

        public Channel[] Channels { get { return Event.Channels; } }

        private Dictionary<Channel, Microsoft.Xna.Framework.Color> channelColour;

        private Event eventObj;
        public Event Event { get { return eventObj; } set { eventObj = value; OnEventChange?.Invoke(); } }


        public event System.Action OnEventChange;
        public event System.Action OnPilotRefresh;

        public LapRecordManager LapRecordManager { get; set; }
        public ResultManager ResultManager { get; set; }
        public TimedActionManager TimedActionManager { get; set; }
        public RoundManager RoundManager { get; set; }
        public SpeedRecordManager SpeedRecordManager { get; set; }

        public event PilotChannelDelegate OnPilotChangedChannels;

        public event System.Action OnChannelsChanged;

        public ExportColumn[] ExportColumns { get; set; }

        public EventManager()
        {
            RaceManager = new RaceManager(this);
            LapRecordManager = new LapRecordManager(RaceManager);
            ResultManager = new ResultManager(this);
            TimedActionManager = new TimedActionManager();
            RoundManager = new RoundManager(this);
            SpeedRecordManager = new SpeedRecordManager(RaceManager);

            ExportColumns = ExportColumn.Read();

            channelColour = new Dictionary<Channel, Microsoft.Xna.Framework.Color>();

            RaceManager.TimingSystemManager.Connect();
        }

        public void Dispose()
        {
            RaceManager.Dispose();
        }

        public Channel GetChannel(Pilot p)
        {
            PilotChannel pc = GetPilotChannel(p);
            if (pc != null)
            {
                return pc.Channel;
            }

            return Channels.FirstOrDefault();
        }

        public Pilot GetCreatePilot(string pilotName)
        {
            Pilot p = null;

            // Check the event..
            var pc = eventObj.PilotChannels.FirstOrDefault(pa => !string.IsNullOrEmpty(pa.Pilot.Name) && pa.Pilot.Name.ToLower() == pilotName.ToLower());

            if (pc != null)
            {
                return pc.Pilot;
            }

            using (Database db = new Database())
            {
                // check the db.
                if (pc == null)
                {
                    p = db.Pilots.FindAll().FirstOrDefault(pa => !string.IsNullOrEmpty(pa.Name) && pa.Name.ToLower() == pilotName.ToLower());
                }

                // Create
                if (p == null)
                {
                    p = new Pilot(pilotName);
                    db.Pilots.Insert(p);
                }
            }
            return p;
        }

        public bool AddPilot(Pilot p)
        {
            Channel channel = LeastUsedChannel();

            return AddPilot(p, channel);
        }

        public Channel LeastUsedChannel()
        {
            Dictionary<Channel, int> counts = new Dictionary<Channel, int>();
            foreach (Channel[] shared in Channels.GetChannelGroups())
            {
                int count = Event.PilotChannels.Count(r => r.Channel.InterferesWith(shared));
                counts.Add(shared.FirstOrDefault(), count);
            }

            if (counts.Any())
            {
                return counts.OrderBy(kvp => kvp.Value).First().Key;
            }

            return Channels.FirstOrDefault();
        }

        public bool AddPilot(Pilot p, Channel c)
        {
            return AddPilot(new PilotChannel(p, c));
        }

        public bool AddPilot(PilotChannel pc)
        {
            if (eventObj.PilotChannels.Any(a => a.Pilot == pc.Pilot))
            {
                return false;
            }

            eventObj.PilotChannels.Add(pc);

            if (eventObj.RemovedPilots.Contains(pc.Pilot))
            {
                eventObj.RemovedPilots.Remove(pc.Pilot);
            }

            using (Database db = new Database())
            {
                db.PilotChannels.Upsert(pc);
                db.Events.Update(eventObj);
            }

            OnPilotRefresh?.Invoke();

            return true;
        }

        public bool RemovePilot(Pilot pilot)
        {
            PilotChannel pilotChannel = GetPilotChannel(pilot); 
            if (pilotChannel != null)
            {
                Event.PilotChannels.Remove(pilotChannel);
                Event.RemovedPilots.Add(pilotChannel.Pilot);

                using (Database db = new Database())
                {
                    db.Events.Update(eventObj);
                }
                OnPilotRefresh?.Invoke();
                return true;
            }

            return false;
        }

        public void SetEventLaps(int laps)
        {
            using (Database db = new Database())
            {
                Event.Laps = laps;
                db.Events.Update(Event);
            }

            RaceManager.SetTargetLaps(laps);

            OnEventChange?.Invoke();
        }

        public void SetEventType(EventTypes type)
        {
            using (Database db = new Database())
            {
                Event.EventType = type;
                db.Events.Update(Event);

                Race current = RaceManager.CurrentRace;
                if (current != null)
                {
                    current.Round.EventType = type;
                    db.Rounds.Update(current.Round);
                }
            }

            OnEventChange?.Invoke();
        }

        public void LoadEvent(WorkSet workSet, WorkQueue workQueue, Event eve)
        {
            workQueue.Enqueue(workSet, "Loading Event", () =>
            {
                using (Database db = new Database())
                {
                    Event = db.Events
                    .Include(e => e.PilotChannels)
                    .Include(e => e.PilotChannels.Select(pc => pc.Pilot))
                    .Include(e => e.PilotChannels.Select(pc => pc.Channel))
                    .Include(e => e.RemovedPilots)
                    .Include(e => e.Rounds)
                    .Include(e => e.Club)
                    .Include(e => e.Tracks)
                    .Include(e => e.Channels)
                    .FindById(eve.ID);

                    UpdateRoundOrder(db);
                }

                if (eve.Channels == null || !eve.Channels.Any())
                {
                    Event.Channels = Channel.Read();
                }
            });

            workQueue.Enqueue(workSet, "Finding Profile Pictures", () =>
            {
                DirectoryInfo photoDir = new DirectoryInfo("pilots");

                string[] extensions = new string[] { ".png", ".jpg" };

                if (photoDir.Exists)
                {
                    FileInfo[] files = photoDir.GetFiles();
                    foreach (Pilot p in Event.Pilots)
                    {
                        if (string.IsNullOrEmpty(p.PhotoPath))
                        {
                            IEnumerable<FileInfo> matches = files.Where(f => f.Name.ToLower().Contains(p.Name.ToLower()));
                            matches = matches.Where(f => extensions.Contains(f.Extension.ToLower()));

                            if (matches.Any())
                            {
                                using (Database db = new Database())
                                {
                                    p.PhotoPath = matches.OrderByDescending(f => f.Extension).FirstOrDefault().FullName;
                                    db.Pilots.Update(p);
                                }
                            }
                        }
                    }
                }
            });
        }

        public void UpdateRoundOrder(Database db)
        {
            if (Event.Rounds.Any(r => r.Order < 0))
            {
                Round[] rounds = Event.Rounds.OrderBy(r => r.Order).ThenBy(r => r.Creation).ThenBy(r => r.RoundNumber).ToArray();

                int order = 100;
                foreach (Round r in rounds)
                {
                    r.Order = order;
                    order += 100;
                }

                db.Rounds.Update(rounds);
            }
        }

        public void LoadRaces(WorkSet workSet, WorkQueue workQueue, Event eve)
        {
            workQueue.Enqueue(workSet, "Loading Races", () =>
            {
                //Load any existing races
                RaceManager.LoadRaces(eve);
            });

            workQueue.Enqueue(workSet, "Loading Results", () =>
            {
                // Load points
                ResultManager.Load(Event);
            });

            workQueue.Enqueue(workSet, "Updating Records", () =>
            {
                LapRecordManager.UpdatePilots(Event.PilotChannels.Select(pc => pc.Pilot));
                SpeedRecordManager.Initialize();
            });

            workQueue.Enqueue(workSet, "Loading Sheets", () =>
            {
                RoundManager.SheetFormatManager.Load();
            });
        }

        public void Update(GameTime gameTime)
        {
            if (RaceManager != null)
            {
                RaceManager.Update(gameTime);
            }

            if (TimedActionManager != null)
            {
                TimedActionManager.Update();
            }
        }

        public void SetChannelColors(IEnumerable<Microsoft.Xna.Framework.Color> colors)
        {
            channelColour.Clear();

            if (Channels == null || !Channels.Any())
                return;

            var ordered = Channels.OrderBy(c => c.Frequency).ThenBy(r => r.Band);
            var colorEnumer = colors.GetEnumerator();

            Channel last = null;
            foreach (Channel channel in ordered)
            {
                if (!channel.InterferesWith(last))
                {
                    if (!colorEnumer.MoveNext())
                    {
                        colorEnumer = colors.GetEnumerator();
                        colorEnumer.MoveNext();
                    }
                }

                Color color = colorEnumer.Current;
                if (!channelColour.ContainsKey(channel))
                {
                    channelColour.Add(channel, color);
                }
                last = channel;
            }

            OnChannelsChanged?.Invoke();
        }
        
        public Microsoft.Xna.Framework.Color GetChannelColor(Channel c)
        {
            Microsoft.Xna.Framework.Color color;
            if (channelColour.TryGetValue(c, out color))
            {
                return color;
            }
            return  Microsoft.Xna.Framework.Color.Red;
        }

        public void RemovePilots()
        {
            eventObj.RemovedPilots.AddRange(eventObj.PilotChannels.Where(p => !eventObj.RemovedPilots.Contains(p.Pilot)).Select(pc => pc.Pilot));
            eventObj.PilotChannels.Clear();

            using (Database db = new Database())
            {
                db.Events.Update(eventObj);
            }
        }

        public void RedistrubuteChannels()
        {
            var channelLanes = Channels.GetChannelGroups().ToArray();

            int counter = 0;
            foreach (var p in Event.PilotChannels.OrderBy(p => p.Pilot.Name))
            {
                Channel c = channelLanes[counter % channelLanes.Length].First();

                SetPilotChannel(p.Pilot, c);
                counter++;
            }
        }

        public PilotChannel GetPilotChannel(Pilot p)
        {
            return Event.PilotChannels.FirstOrDefault(pc => pc.Pilot == p);
        }

        public void SetPilotChannels(Race race)
        {
            var cache = race.PilotChannelsSafe;
            foreach (var pc in cache)
            {
                SetPilotChannel(pc.Pilot, pc.Channel);
            }
        }

        public void SetPilotChannel(Pilot pi, Channel c)
        {
            if (pi == null)
                return;

            PilotChannel pc = GetPilotChannel(pi);
            if (pc == null)
                return;

            if (pc.Channel != c)
            {
                pc.Channel = c;

                using (Database db = new Database())
                {
                    db.PilotChannels.Update(pc);
                }

                if (RaceManager.HasPilot(pi) && !RaceManager.RaceRunning)
                {
                    RaceManager.ChangeChannel(c, pi);
                }

                OnPilotChangedChannels?.Invoke(pc);
            }
        }

        public void ToggleSumPoints(Round round)
        {
            if (round.PointSummary == null)
            {
                round.PointSummary = new PointSummary(ResultManager.PointsSettings);
            }
            else
            {
                round.PointSummary = null;
            }

            using (Database db = new Database())
            {
                db.Events.Update(Event);
                db.Rounds.Update(round);
            }
        }

        public void ToggleTimePoints(Round round)
        {
            if (round.TimeSummary == null)
            {
                round.TimeSummary = new TimeSummary();
            }
            else
            {
                round.TimeSummary = null;
            }

            using (Database db = new Database())
            {
                db.Events.Update(Event);
                db.Rounds.Update(round);
            }
        }

        public void ToggleLapCount(Round round)
        {
            round.LapCountAfterRound = !round.LapCountAfterRound;

            using (Database db = new Database())
            {
                db.Events.Update(Event);
                db.Rounds.Update(round);
            }
        }

        public bool CanExport(ExportColumn.ColumnTypes type)
        {
            return ExportColumns.Any(ec => ec.Enabled && ec.Type == type);
        }


        public IEnumerable<Tuple<Pilot, Channel>> GetPilotsFromLines(IEnumerable<string> pilots, bool assignChannel)
        {
            int channelIndex = 0;

            var channelLanes = Channels.GetChannelGroups().ToArray();

            foreach (string untrimmed in pilots)
            {
                string pilotname = untrimmed.Trim();

                Pilot p = Event.Pilots.FirstOrDefault(pa => pa.Name.ToLower() == pilotname.ToLower());
                if (p != null)
                {
                    Channel c = GetChannel(p);
                    if (assignChannel)
                    {
                        if (channelIndex < channelLanes.Length)
                        {
                            var laneOptions = channelLanes[channelIndex];

                            var chosen = laneOptions.FirstOrDefault(r => r.Band.GetBandType() == c.Band.GetBandType());
                            if (chosen != null)
                            {
                                c = chosen;
                            }
                        }
                    }

                    yield return new Tuple<Pilot, Channel>(p, c);
                }
                channelIndex++;
                channelIndex = channelIndex % Channels.Length;
            }
        }

        public void AddPilotsFromLines(IEnumerable<string> pilots)
        {
            IEnumerable<Tuple<Pilot, Channel>> pcs = GetPilotsFromLines(pilots, true);
            foreach (Tuple<Pilot, Channel> pc in pcs)
            {
                Pilot p = pc.Item1;
                Channel c = pc.Item2;

                RaceManager.AddPilot(c, p);
            }
        }

        public string GetResultsText()
        {
            Race currentRace = RaceManager.CurrentRace;
            if (currentRace != null)
            {
                string textResults = ResultManager.GetResultsText(currentRace);

                return textResults;
            }

            return "";
        }

        public Event[] GetOtherEvents()
        {
            using (Database db = new Database())
            {
                var events = db.Events.Include(a => a.PilotChannels)
                                             .Include(a => a.PilotChannels.Select(pc => pc.Pilot))
                                             .Include(a => a.PilotChannels.Select(pc => pc.Channel)).FindAll();
                return events.Where(e => e != Event).ToArray();
            }
        }

        public Pilot GetPilot(Guid iD)
        {
            return Event.Pilots.FirstOrDefault(p => p.ID == iD);
        }

        public Pilot GetPilot(string name)
        {
            return Event.Pilots.FirstOrDefault(p => p.Name == name);
        }

        public void RefreshPilots(Database db)
        {
            Event.RefreshPilots(db);
            foreach (Race r in RaceManager.Races)
            {
                r.RefreshPilots(db);
            }

            OnPilotRefresh?.Invoke();
        }

        public int GetMaxPilotsPerRace()
        {
            return Channels.GetChannelGroups().Count();
        }

        public int GetChannelGroupIndex(Channel channel)
        {
            int i = 0;
            foreach (var channelGroup in Channels.GetChannelGroups())
            {
                if (channelGroup.Contains(channel))
                {
                    return i;
                }
                i++;
            }
            return -1;
        }

        public IEnumerable<Channel> GetChannelGroup(int slot)
        {
            int i = 0;
            foreach (var channelGroup in Channels.GetChannelGroups())
            {
                if (i == slot)
                {
                    return channelGroup;
                }
                i++;
            }

            return new Channel[0];
        }
    }
}
