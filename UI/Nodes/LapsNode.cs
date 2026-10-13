using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timing;
using Tools;

namespace UI.Nodes
{
    public class LapsNode : Node, IUpdateableNode
    {
        private LapNode[] lapNodes;

        public Pilot Pilot { get; private set; }

        public EventManager EventManager { get; private set; }

        private int lapsPerRow;

        private ColorNode backgroundNode;

        private Color channelColor;
        public Color ChannelColor { get => channelColor; set { channelColor = value; UpdateLapCount(); } }

        public bool BackgroundVisible { get { return backgroundNode.Visible; } set { backgroundNode.Visible = value; } }

        private object locker;

        public int LapLines { get; set; }

        public Action<Lap> OnSeekToLap { get; set; }
        public Action<DateTime> OnSeek { get; set; }
        public Action<Pilot, DateTime> OnAddLap { get; set; }

        // When set (by the replay), shows the selected sector's times instead of the laps.
        private SectorSelection sectorSelection;
        public SectorSelection SectorSelection
        {
            get { return sectorSelection; }
            set
            {
                if (sectorSelection != null)
                {
                    sectorSelection.OnChanged -= RefreshData;
                }

                sectorSelection = value;

                if (sectorSelection != null)
                {
                    sectorSelection.OnChanged += RefreshData;
                }
            }
        }

        private TableNode table;

        private DateTime? playbackTime;

        public IEnumerable<Lap> Laps
        {
            get
            {
                foreach (var node in lapNodes)
                {
                    if (node.Lap != null)
                    {
                        yield return node.Lap;
                    }
                }
            }
        }

        public LapsNode(EventManager em)
        {
            locker = new object();
            table = new TableNode();
            EventManager = em;

            backgroundNode = new ColorNode(Theme.Current.PilotViewTheme.LapBackground);
            AddChild(backgroundNode);

            AddChild(table);
            LapLines = 1;

            UpdateLapCount();

            em.RaceManager.OnLapDisqualified += RaceManager_OnLapDisqualified;
        }

        public override void Dispose()
        {
            EventManager.RaceManager.OnLapDisqualified -= RaceManager_OnLapDisqualified;
            SectorSelection = null;
            base.Dispose();
        }

        // See RaceManager.GetSplitTimingSystems. Kept until the data's next refreshed, as the playback time's set every frame.
        private int[] splitTimingSystems;
        private int[] SplitTimingSystems
        {
            get
            {
                if (splitTimingSystems == null)
                {
                    splitTimingSystems = EventManager.RaceManager.GetSplitTimingSystems();
                }
                return splitTimingSystems;
            }
        }

        // 0 when showing the laps.
        private int GetSector(Race race)
        {
            if (sectorSelection == null || sectorSelection.Sector == 0 || race == null || sectorSelection.Sector > race.GetSectorCount(SplitTimingSystems))
                return 0;

            return sectorSelection.Sector;
        }

        private SectorTime[] GetSectorTimes(Race race, int sector)
        {
            IEnumerable<SectorTime> sectorTimes = race.GetSectorTimes(Pilot, SplitTimingSystems, sector);

            if (playbackTime.HasValue)
            {
                sectorTimes = sectorTimes.Where(st => st.End <= playbackTime.Value);
            }

            return sectorTimes.ToArray();
        }

        private void RaceManager_OnLapDisqualified(Lap lap)
        {
            if (lap.Pilot == Pilot)
            {
                RefreshData();
            }
        }

        private void UpdateLapCount()
        {
            lock (locker)
            {
                if (lapNodes != null)
                {
                    foreach (var n in lapNodes)
                    {
                        n.Dispose();
                    }
                }

                lapsPerRow = GetLapsPerRowCount();
                table.SetSize(LapLines, lapsPerRow);

                lapNodes = new LapNode[table.CellCount];

                for (int i = 0; i < lapNodes.Length; i++)
                {
                    LapNode tn = new LapNode(EventManager, ChannelColor);
                    tn.OnRightClick += Menu;
                    tn.IncludeLapNumber = true;
                    tn.Visible = false;

                    Node container = table.GetCell(i);
                    if (container != null)
                    {
                        container.AddChild(tn);
                        lapNodes[i] = tn;
                    }
                }
            }
        }

        private int GetLapsPerRowCount()
        {
            if (!EventManager.RaceManager.RaceType.HasLapCountLimit())
                return 5;

            Race currentRace = EventManager.RaceManager.CurrentRace;
            if (currentRace != null)
            {
                int laps = currentRace.TargetLaps;

                if (EventManager.Event.PrimaryTimingSystemLocation == PrimaryTimingSystemLocation.Holeshot)
                    laps++;

                // Get the laps the pilot has done..
                if (Pilot != null)
                {
                    int pilotLaps = currentRace.GetValidLapsCount(Pilot, true);
                    laps = Math.Max(laps, pilotLaps);
                }

                return Math.Min(5, Math.Max(2, laps));
            }

            return 5;
        }

        public void SetPilot(Pilot pilot)
        {
            if (Pilot != pilot)
            {
                Pilot = pilot;
            }
            RefreshData();
        }

        public void RefreshData()
        {
            splitTimingSystems = null;

            if (Pilot == null)
                return;

            if (lapsPerRow != GetLapsPerRowCount() || LapLines != table.Rows)
            {
                UpdateLapCount();
            }

            Race r = EventManager.RaceManager.CurrentRace;

            if (r == null)
                return;

            if (!r.HasPilot(Pilot))
                return;

            int sector = GetSector(r);
            if (sector > 0)
            {
                RefreshData(GetSectorTimes(r, sector));
                return;
            }

            Lap[] laps = r.GetValidLaps(Pilot, true).OrderBy(l => l.End).ToArray();

            if (playbackTime.HasValue)
            {
                // Use <= so a lap whose detection time is exactly the current playback
                // position (e.g. one just added via "Add Lap Now" while paused) is included.
                // Must match the comparison in SetPlaybackTime, otherwise this path (reached
                // via ChannelNodeBase's OnLapDetected handler) runs after it and hides the lap.
                laps = laps.Where(lap => lap.Detection.Time <= playbackTime.Value).ToArray();
            }

            RefreshData(laps);
        }

        public void RefreshData(Lap[] laps)
        { 
            lock (locker)
            {
                if (lapNodes == null)
                    return;

                int lapStart = Math.Max(0, laps.Length - lapNodes.Length);
                for (int i = 0; i < lapNodes.Length; i++)
                {
                    LapNode lapNode = lapNodes[i];
                    if (lapNode == null)
                        continue;

                    int lapIndex = lapStart + i;
                    if (lapIndex < laps.Length)
                    {
                        Lap lap = laps[lapIndex];
                        if (lap == null)
                            continue;

                        lapNode.SetLap(lap);
                        // Apply visibility here too, not only in Update(): while replay is
                        // paused the per-frame Update tick doesn't reveal a freshly added lap,
                        // so set it immediately to keep the data and the displayed rows in sync.
                        lapNode.Visible = true;

                        int pbLaps = EventManager.Event.PBLaps;

                        if (EventManager.LapRecordManager.IsRecord(lap, pbLaps, out bool overalBest))
                        {
                            if (overalBest)
                            {
                                lapNode.Tint = Theme.Current.OverallBestTime.XNA;
                            }
                            else
                            {
                                lapNode.Tint = Theme.Current.NewPersonalBest.XNA;
                            }
                        }
                        else
                        {
                            lapNode.Tint = Color.White;
                        }
                    }
                    else
                    {
                        lapNodes[i].Clear();
                        lapNodes[i].Visible = false;
                    }
                }
            }

            RequestLayout();
        }

        public void RefreshData(SectorTime[] sectorTimes)
        {
            lock (locker)
            {
                if (lapNodes == null)
                    return;

                int start = Math.Max(0, sectorTimes.Length - lapNodes.Length);
                for (int i = 0; i < lapNodes.Length; i++)
                {
                    LapNode lapNode = lapNodes[i];
                    if (lapNode == null)
                        continue;

                    int index = start + i;
                    if (index < sectorTimes.Length)
                    {
                        lapNode.SetSectorTime(sectorTimes[index]);
                        lapNode.Visible = true;
                        lapNode.Tint = Color.White;
                    }
                    else
                    {
                        lapNode.Clear();
                        lapNode.Visible = false;
                    }
                }
            }

            RequestLayout();
        }

        public void AddLap(Lap lap)
        {
            RefreshData();
        }

        public void ClearLaps()
        {
            lock (locker)
            {
                for (int i = 0; i < lapNodes.Length; i++)
                {
                    lapNodes[i].Clear();
                }
                RequestLayout();
            }
        }

        public void Update(GameTime gameTime)
        {
            lock (locker)
            {
                for (int i = 0; i < lapNodes.Length && i < lapNodes.Length; i++)
                {
                    bool hasLap = lapNodes[i].HasTime;
                    lapNodes[i].Visible = hasLap;
                }
            }
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (!base.OnMouseInput(mouseInputEvent))
            {
                if (mouseInputEvent.Button == MouseButtons.Right && mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    Menu(mouseInputEvent, null);
                    return true;
                }
                return false;
            }

            return true;
        }

        private void Menu(MouseInputEvent mouseInputEvent, LapNode lapNode)
        {
            Race race = EventManager.RaceManager.CurrentRace;
            int sector = GetSector(race);
            if (sector > 0)
            {
                SectorMenu(mouseInputEvent, race, sector, lapNode?.SectorTime);
                return;
            }

            MouseMenu mm = new MouseMenu(this);

            Lap lap = null;
            if (lapNode != null)
            {
                lap = lapNode.Lap;
            }

            if (lap != null && OnSeekToLap != null)
            {
                mm.AddItem("Seek to Lap", () => { OnSeekToLap(lap); });
                mm.AddBlank();
            }

            if (EventManager.RaceManager.RaceRunning)
            {
                mm.AddItem("Add Lap Now", () =>
                {
                    EventManager.RaceManager.AddManualLap(Pilot, DateTime.Now);
                });
#if DEBUG
                mm.AddItem("Add Sector Now", () =>
                {
                    EventManager.RaceManager.AddManualSector(Pilot, DateTime.Now, 1);
                });
#endif
            }  

            if (OnSeekToLap != null)
            {
                mm.AddItem("Add Lap Now", () =>
                {
                    if (playbackTime.HasValue)
                        OnAddLap?.Invoke(Pilot, playbackTime.Value);
                });
            }
            if (EventManager.RaceManager.RaceStarted)
            {
                mm.AddItem("Add Lap time...", () =>
                {
                    GetLayer<PopupLayer>().Popup(new AddLapTimeNode(EventManager.RaceManager, Pilot));
                });

                mm.AddBlank();

               
                if (!EventManager.RaceManager.RaceRunning && EventManager.RaceManager.TimingSystemManager.AllSystems.OfType<IRemoteMarshalUpdatable>().Any(ts => ts.MarshalSupported))
                {
                    mm.AddItem("Edit Laps / Marshal", () =>
                    {
                        OpenMarshal();
                    });
                }
                else
                {
                    mm.AddItem("Edit Laps", () =>
                    {
                        EditLaps();
                    });
                }

                if (Laps.Any())
                {
                    mm.AddItem("Copy Laps", () =>
                    {
                        PlatformTools.Clipboard.SetText(Laps.ToTSV());
                    });
                }

                mm.AddItem("Paste Laps", () =>
                {
                    string lapLine = PlatformTools.Clipboard.GetText();
                    EditLaps(lapLine.TSVToDouble());
                });
            }

            if (lap != null)
            {
                mm.AddBlank();

                mm.AddItem("Disqualify Lap", () =>
                {
                    EventManager.RaceManager.DisqualifyLap(lap);
                });

                mm.AddSubmenu("Split Lap into...", (i) =>
                {
                    EventManager.RaceManager.SplitLap(lap, i);
                }, new int[] { 2, 3, 4, 5, 6, 7 });
            }

            mm.Show(mouseInputEvent);
        }

        // In the replay, times can be added or moved to where the video is, to line them up with the pilot crossing the gate.
        private void SectorMenu(MouseInputEvent mouseInputEvent, Race race, int sector, SectorTime sectorTime)
        {
            MouseMenu mm = new MouseMenu(this);
            string name = SectorSelection.GetName(sector);

            if (sectorTime != null && OnSeek != null)
            {
                mm.AddItem("Seek to " + name, () => { OnSeek(sectorTime.Start); });
                mm.AddBlank();
            }

            if (playbackTime.HasValue && OnSeek != null)
            {
                mm.AddItem("Add " + name + " Time Now", () =>
                {
                    if (playbackTime.HasValue)
                    {
                        EventManager.RaceManager.AddManualSectorWithRace(race, Pilot, playbackTime.Value, sector);
                        UpdateRecords();
                    }
                });

                if (sectorTime != null)
                {
                    mm.AddItem("Move Time to Now", () =>
                    {
                        if (playbackTime.HasValue)
                        {
                            EventManager.RaceManager.SetDetectionTime(race, sectorTime.Detection, playbackTime.Value);
                            UpdateRecords();
                        }
                    });
                }
            }

            if (EventManager.RaceManager.RaceStarted)
            {
                mm.AddItem("Edit " + name + " Times", EditLaps);
            }

            if (sectorTime != null)
            {
                mm.AddBlank();
                mm.AddItem("Disqualify " + name + " Time", () => { EventManager.RaceManager.DisqualifySectorTime(sectorTime); });
            }

            mm.Show(mouseInputEvent);
        }

        // Lap and speed records follow the times.
        private void UpdateRecords()
        {
            EventManager.LapRecordManager.ClearPilot(Pilot);
            EventManager.LapRecordManager.UpdatePilot(Pilot);
            EventManager.SpeedRecordManager.UpdatePilot(Pilot);
            RefreshData();
        }

        private void EditLaps()
        {
            EditLaps(new double[0]);
        }

        private void EditLaps(IEnumerable<double> lapsToAdd)
        {
            Race currentRace = EventManager.RaceManager.CurrentRace;
            if (currentRace == null)
                return;

            Channel channel = currentRace.GetChannel(Pilot);
            if (channel == null)
                return;

            Lap[] editLaps = currentRace.GetLaps(l => l.Pilot == Pilot).ToArray();

            LapEditorNode editor = new LapEditorNode(EventManager.RaceManager, currentRace, Pilot, editLaps, ChannelColor, sectorSelection);

            if (lapsToAdd.Any())
            {
                editor.AddManualLaps(lapsToAdd);
            }

            GetLayer<PopupLayer>().Popup(editor);
            editor.OnOK += (v) =>
            {
                using (IDatabase db = DatabaseFactory.Open(EventManager.EventId))
                {
                    db.Update(editLaps);
                    currentRace.ReCalculateLaps(db, Pilot);
                }

                UpdateRecords();
            };
        }

        private void OpenMarshal()
        {
            Race currentRace = EventManager.RaceManager.CurrentRace;
            if (currentRace == null)
                return;

            Channel channel = currentRace.GetChannel(Pilot);
            if (channel == null)
                return;

            Lap[] editLaps = currentRace.GetLaps(l => l.Pilot == Pilot).ToArray();

            RSSIWaveform waveform = EventManager.RaceManager.TimingSystemManager.AllSystems
                .OfType<IRemoteMarshalUpdatable>()
                .Where(ts => ts.MarshalSupported)
                .Select(ts => ts.GetWaveform(currentRace.ID, Pilot.ID))
                .FirstOrDefault(w => w != null);

            MarshalEditorNode editor = new MarshalEditorNode(EventManager.RaceManager, currentRace, Pilot, editLaps, ChannelColor, waveform, sectorSelection);

            GetLayer<PopupLayer>().Popup(editor);
            editor.OnOK += (v) =>
            {
                using (IDatabase db = DatabaseFactory.Open(EventManager.EventId))
                {
                    db.Update(editLaps);
                    currentRace.ReCalculateLaps(db, Pilot);
                }

                UpdateRecords();
            };
        }

        public void SetPlaybackTime(DateTime time)
        {
            playbackTime = time;
            if (playbackTime.HasValue)
            {
                Race r = EventManager.RaceManager.CurrentRace;

                if (r == null)
                    return;

                if (!r.HasPilot(Pilot))
                    return;

                // Grow/shrink the lap-cell table to fit the pilot's current lap count. Without
                // this, a lap added during replay has no cell to display in (the array-based
                // RefreshData never resizes the table), so it only shows once an UpdateLapCount
                // happens on unpause.
                if (lapsPerRow != GetLapsPerRowCount() || LapLines != table.Rows)
                    UpdateLapCount();

                int sector = GetSector(r);
                if (sector > 0)
                {
                    SectorTime[] sectorTimes = GetSectorTimes(r, sector);

                    SectorTime lastShown = lapNodes.LastOrDefault(ln => ln != null && ln.SectorTime != null)?.SectorTime;
                    int shown = lapNodes.Count(ln => ln != null && ln.SectorTime != null);

                    if (Math.Min(sectorTimes.Length, lapNodes.Length) != shown ||
                        (sectorTimes.Any() && sectorTimes.Last().Detection != lastShown?.Detection))
                    {
                        RefreshData(sectorTimes);
                    }
                    return;
                }

                Lap[] laps = r.GetValidLaps(Pilot, true).OrderBy(l => l.End).Where(lap => lap.Detection.Time <= playbackTime.Value).ToArray();

                int visibleCount = lapNodes.Count(ln => ln != null && ln.Visible);
                if (laps.Length != visibleCount ||
                    (laps.Any() && laps.FirstOrDefault() != lapNodes.FirstOrDefault()?.Lap))
                {
                    RefreshData(laps);
                }
            }
        }
    }

    public class LapNode : Node
    {
        public delegate void OnLapNode(MouseInputEvent mie, LapNode lapNode);
        public event OnLapNode OnRightClick;

        public Lap Lap { get; private set; }

        // Shown instead of a lap, see LapsNode.SectorSelection.
        public SectorTime SectorTime { get; private set; }

        public bool HasTime { get { return Lap != null || SectorTime != null; } }

        private const string empty = "";

        public bool IncludeRace { get; set; }
        public bool IncludeLapNumber { get; set; }

        private EventManager em;

        public string Title { get; set; }

        private TextNode textNode;
        private ImageNode imageNode;

        public Color Tint { get { return textNode.Tint; } set { textNode.Tint = value; } }

        public LapNode(EventManager em, Color channelColor) 
        {
            imageNode = new ImageNode(@"img/lapbg.png");
            imageNode.KeepAspectRatio = false;
            imageNode.Tint = channelColor;
            AddChild(imageNode);

            textNode = new TextNode("", Theme.Current.TextMain.XNA);
            textNode.Alignment = RectangleAlignment.Center;
            textNode.RelativeBounds = new RectangleF(0.1f, 0, 0.8f, 0.9f);

            imageNode.AddChild(textNode);

            this.em = em;
            IncludeRace = false;
            IncludeLapNumber = false;
        }

        public void Clear()
        {
            textNode.Text = Title + empty;
            Lap = null;
            SectorTime = null;
        }

        public void SetLap(Lap lap)
        {
            if (lap == null)
            {
                Clear();
                Lap = null;
                return;
            }

            string newText = Title;
            if (IncludeRace)
            {
                newText += "R" + lap.Race.RaceNumber + " ";
            }
            if (IncludeLapNumber)
            {
                newText += Lap.LapNumberToString(lap.Number) + " ";
            }

            newText += lap.Length.ToStringRaceTime();

            textNode.Text = newText;
            Lap = lap;
            SectorTime = null;
        }

        public void SetSectorTime(SectorTime sectorTime)
        {
            if (sectorTime == null)
            {
                Clear();
                return;
            }

            string newText = Title;
            if (IncludeLapNumber)
            {
                newText += Lap.LapNumberToString(sectorTime.LapNumber) + " ";
            }

            newText += "S" + sectorTime.Sector + " " + sectorTime.Length.ToStringRaceTime();

            textNode.Text = newText;
            Lap = null;
            SectorTime = sectorTime;
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (HasTime)
            {
                if (mouseInputEvent.Button == MouseButtons.Right && mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    OnRightClick?.Invoke(mouseInputEvent, this);
                    return true;
                }

                if (mouseInputEvent.ButtonState == ButtonStates.Released && CompositorLayer.InputEventFactory.AreControlKeysDown())
                {
                    if (Lap != null)
                    {
                        em.RaceManager.DisqualifyLap(Lap);
                    }
                    else
                    {
                        em.RaceManager.DisqualifySectorTime(SectorTime);
                    }
                    return true;
                }
            }

            return base.OnMouseInput(mouseInputEvent);
        }
    }
}
