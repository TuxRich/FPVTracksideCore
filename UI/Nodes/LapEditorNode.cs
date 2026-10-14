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
using System.Text;
using System.Threading.Tasks;
using Timing;
using Tools;

namespace UI.Nodes
{
    public class LapEditorNode : AspectNode
    {
        public Lap[] Laps { get; private set; }
        public Race Race { get; private set; }
        public Pilot Pilot { get; private set; }

        protected Node raceNode;
        protected Node lapsNode;
        protected Node buttonContainer;
        protected BorderPanelShadowNode background;

        protected TextButtonNode okButton;
        protected TextButtonNode cancelButton;
        protected TextButtonNode addLapButton;
        protected TextButtonNode copyLapsButton;
        protected TextButtonNode pasteLapsButton;

        public event Action<Lap[]> OnOK;
        public event Action<Lap[]> OnCancel;

        public Color ChannelColor { get; private set; }

        protected List<LapEditorContainer> lapContainers;

        private LapRowListNode lapRows;

        protected IEnumerable<LapEditorContainer> orderedLapContainers { get { return lapContainers.OrderBy(l => l.End); } }

        // The editor shows either the laps, or the times of one of the sectors they're divided into (see
        // Race.GetSectorTimes), even without split timing so they can be added. Each sector's times are made
        // the first time it's shown.
        private SectorSelection sectorSelection;
        private int[] splitTimingSystems;
        private int sectorCount;
        private Dictionary<int, List<LapEditorContainer>> sectorContainers;

        // 0 when showing the laps.
        protected int Sector
        {
            get
            {
                if (sectorSelection.Sector > sectorCount)
                    return 0;

                return sectorSelection.Sector;
            }
        }

        protected IEnumerable<LapEditorContainer> shownContainers
        {
            get
            {
                List<LapEditorContainer> containers;
                if (Sector > 0 && sectorContainers.TryGetValue(Sector, out containers))
                {
                    return containers.OrderBy(l => l.End);
                }

                return orderedLapContainers;
            }
        }

        public RaceManager RaceManager { get; private set; }

        public const float DisabledAlpha = 0.3f;

        // Overridable layout regions, so a subclass (e.g. one that adds a graph above the
        // race timeline) can shift things down without duplicating the whole constructor.
        protected virtual string TitleText => "Lap Editor - " + Pilot.Name;
        protected virtual RectangleF TitleBounds => new RectangleF(0, 0, 0.9f, 0.08f);
        protected virtual RectangleF RaceNodeBounds => new RectangleF(0.01f, 0.09f, 0.98f, 0.1f);
        protected virtual RectangleF LapsNodeBounds => new RectangleF(0.01f, 0.22f, 0.98f, 0.65f);
        protected virtual float ButtonContainerTop => 1 - 0.10f;
        protected virtual RectangleF SectorSelectorBounds => new RectangleF(0.79f, 0.01f, 0.2f, 0.06f);

        // Grid sizing. Rows are a fixed height, so more laps scroll rather than shrinking
        // every time down to nothing. Fewer columns and rows than the area could hold means
        // each lap gets more room, which is what makes the times readable.
        protected virtual int LapsPerRow => 6;
        protected virtual int VisibleLapRows => 6;

        // sectorSelection is shared with whatever opened the editor (e.g. the replay), so both show the same times.
        public LapEditorNode(RaceManager raceManager, Race race, Pilot pilot, IEnumerable<Lap> laps, Color channel, SectorSelection sectorSelection = null)
        {
            RaceManager = raceManager;
            lapContainers = new List<LapEditorContainer>();

            ChannelColor = channel;

            AspectRatio = 3;
            Race = race;
            Pilot = pilot;

            Laps = laps.Where(l => l.Race == race && l.Pilot == pilot).OrderBy(l => l.End).ToArray();

            background = new BorderPanelShadowNode(Theme.Current.Editor.Background, Theme.Current.Editor.Border.XNA);
            AddChild(background);

            TextNode title = new TextNode(TitleText, Theme.Current.Editor.Text.XNA);
            title.RelativeBounds = TitleBounds;
            background.Inner.AddChild(title);

            AddExtraContent(background.Inner);

            raceNode = new ColorNode(Theme.Current.Editor.Foreground.XNA);
            raceNode.RelativeBounds = RaceNodeBounds;
            background.Inner.AddChild(raceNode);

            lapsNode = new ColorNode(Theme.Current.Editor.Foreground.XNA);
            lapsNode.RelativeBounds = LapsNodeBounds;
            background.Inner.AddChild(lapsNode);

            float topOfButtonContainer = ButtonContainerTop;

            buttonContainer = new Node();
            buttonContainer.RelativeBounds = new RectangleF(0, topOfButtonContainer, 1, 1 - topOfButtonContainer);
            background.Inner.AddChild(buttonContainer);

            okButton = new TextButtonNode("Ok", Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
            cancelButton = new TextButtonNode("Cancel", Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);

            addLapButton = new TextButtonNode("Add Lap", Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
            copyLapsButton = new TextButtonNode("Copy Laps", Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
            pasteLapsButton = new TextButtonNode("Paste Laps", Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);

            okButton.OnClick += OkButton_OnClick;
            cancelButton.OnClick += CancelButton_OnClick;
            addLapButton.OnClick += AddLapButton_OnClick;

            copyLapsButton.OnClick += CopyLapsButton_OnClick;
            pasteLapsButton.OnClick += PasteLapsButton_OnClick;

            buttonContainer.AddChild(cancelButton, addLapButton, copyLapsButton, pasteLapsButton, okButton);
            AlignHorizontally(0.05f, cancelButton, null, addLapButton, copyLapsButton, pasteLapsButton, null, okButton);

            Scale(0.5f, 0.9f);

            foreach (Lap lap in Laps)
            {
                LapEditorContainer lc = new LapEditorContainer(lap, ChannelColor);
                lc.OnValidityChanged += UpdateNumbersEtc;
                lc.OnSplitLap += OnSplitLap;
                lc.OnTimeChanged += () => { UpdateNumbersEtc(); };
                lc.RecordTint = GetRecordTint(lap);
                lapContainers.Add(lc);
                OnLapContainerCreated(lc);
            }

            sectorContainers = new Dictionary<int, List<LapEditorContainer>>();
            splitTimingSystems = raceManager.GetSplitTimingSystems();
            sectorCount = race.GetSectorCount(splitTimingSystems);

            this.sectorSelection = sectorSelection ?? new SectorSelection();
            this.sectorSelection.OnChanged += SectorSelection_OnChanged;

            if (sectorCount > 0)
            {
                SectorSelectorNode sectorSelector = new SectorSelectorNode(this.sectorSelection, Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
                sectorSelector.SectorCount = sectorCount;
                sectorSelector.RelativeBounds = SectorSelectorBounds;
                background.Inner.AddChild(sectorSelector);
            }

            if (Sector > 0)
            {
                PrepareSectorContainers(Sector);
            }

            UpdateButtons();
            Layout();

            UpdateRaceNode();
        }

        public override void Dispose()
        {
            sectorSelection.OnChanged -= SectorSelection_OnChanged;

            // Whichever aren't shown aren't disposed along with the rest.
            foreach (LapEditorContainer lc in lapContainers.Concat(sectorContainers.Values.SelectMany(c => c)).Where(c => c.Parent == null))
            {
                lc.Dispose();
            }

            base.Dispose();
        }

        // e.g. for a change that only applies to the laps.
        protected void ShowLaps()
        {
            sectorSelection.Select(0);
        }

        private void SectorSelection_OnChanged()
        {
            int sector = Sector;
            if (sector > 0)
            {
                PrepareSectorContainers(sector);
            }

            UpdateButtons();
            Layout();

            if (sector == 0)
            {
                // Picks up any changes made to the last sector, which ends with the lap.
                UpdateNumbersEtc();
            }
            else
            {
                UpdateRaceNode();
            }
        }

        // Adding and pasting are for laps.
        private void UpdateButtons()
        {
            bool laps = Sector == 0;

            addLapButton.Visible = laps;
            pasteLapsButton.Visible = laps;
            copyLapsButton.Text = laps ? "Copy Laps" : "Copy Times";
        }

        // Makes the sector's times, or updates them for changes made to the laps and other sectors since.
        private void PrepareSectorContainers(int sector)
        {
            // The laps as they stand in the editor. New ones get a stand in detection.
            Dictionary<Detection, LapEditorContainer> lapEnds = new Dictionary<Detection, LapEditorContainer>();
            foreach (LapEditorContainer lc in lapContainers)
            {
                Detection lapEnd = lc.Lap != null ? lc.Lap.Detection : new Detection(TimingSystemType.Manual, 0, Pilot, null, lc.End, 0, true, 0);
                lapEnds[lapEnd] = lc;
            }

            // The split times as they stand in the sectors shown so far.
            Dictionary<Detection, LapEditorContainer> splits = sectorContainers.Values.SelectMany(c => c).Where(c => c.LinkedLap == null).ToDictionary(c => c.Detection);

            Func<Detection, LapEditorContainer> getEdited = (d) =>
            {
                LapEditorContainer lc;
                if (lapEnds.TryGetValue(d, out lc) || splits.TryGetValue(d, out lc))
                {
                    return lc;
                }
                return null;
            };

            IEnumerable<Detection> detections = lapEnds.Keys.Concat(Race.GetDetections(d => d.Pilot == Pilot && !d.IsLapEnd));
            SectorTime[] sectorTimes = Race.GetSectorTimes(Pilot, splitTimingSystems, sector, detections, d => getEdited(d)?.End ?? d.Time, d => getEdited(d)?.Valid ?? d.Valid, true);

            List<LapEditorContainer> containers;
            if (!sectorContainers.TryGetValue(sector, out containers))
            {
                containers = new List<LapEditorContainer>();
                sectorContainers.Add(sector, containers);
            }

            if (sector == sectorCount)
            {
                // The last sector ends with the lap, so its times stand for the laps' ends. Made afresh, as the laps may have changed.
                foreach (LapEditorContainer old in containers)
                {
                    old.Dispose();
                }
                containers.Clear();

                foreach (SectorTime sectorTime in sectorTimes)
                {
                    LapEditorContainer lap = lapEnds[sectorTime.Detection];

                    LapEditorContainer lc = new LapEditorContainer(sectorTime, ChannelColor);
                    lc.LinkedLap = lap;
                    lc.OnTimeChanged += () => { lap.End = lc.End; lap.Refresh(); UpdateRaceNode(); };
                    lc.OnValidityChanged += () => { lap.Valid = lc.Valid; lap.Refresh(); UpdateRaceNode(); };
                    containers.Add(lc);
                }
            }
            else
            {
                foreach (SectorTime sectorTime in sectorTimes)
                {
                    LapEditorContainer lc = containers.FirstOrDefault(c => c.Detection == sectorTime.Detection);
                    if (lc == null)
                    {
                        lc = new LapEditorContainer(sectorTime, ChannelColor);
                        lc.OnTimeChanged += UpdateRaceNode;
                        lc.OnValidityChanged += UpdateRaceNode;
                        containers.Add(lc);
                    }
                    else
                    {
                        // Starts where the time before it ends, which may have changed.
                        lc.Start = sectorTime.Start;
                        lc.Number = sectorTime.LapNumber;
                        lc.Refresh();
                    }
                }
            }
        }

        // Personal / overall best colours, resolved the same way the lap bar under the pilot
        // feed does it (LapsNode.RefreshData). Records come from saved race data, so this
        // reflects the lap as stored, not any unsaved edit in this dialog.
        protected Color? GetRecordTint(Lap lap)
        {
            if (lap == null)
                return null;

            LapRecordManager records = RaceManager?.EventManager?.LapRecordManager;
            if (records == null)
                return null;

            int pbLaps = RaceManager.EventManager.Event.PBLaps;

            if (!records.IsRecord(lap, pbLaps, out bool overallBest))
                return null;

            return overallBest ? Theme.Current.OverallBestTime.XNA : Theme.Current.NewPersonalBest.XNA;
        }

        // No-op hook for a subclass to insert extra content (e.g. an RSSI waveform graph)
        // between the title and the race timeline. Called before raceNode/lapsNode are laid
        // out, so it has no dependency on them.
        protected virtual void AddExtraContent(Node inner)
        {
        }

        // No-op hook called whenever a new LapEditorContainer is created (initial load, manual
        // add, split, or a subclass's own recalculate), so a subclass can wire up per-container
        // events (e.g. MarshalEditorNode's row-select-highlights-graph behavior) in one place.
        protected virtual void OnLapContainerCreated(LapEditorContainer lc)
        {
        }

        private void PasteLapsButton_OnClick(MouseInputEvent mie)
        {
            string lapLine = PlatformTools.Clipboard.GetText();
            AddManualLaps(lapLine.TSVToDouble());
        }

        private void CopyLapsButton_OnClick(MouseInputEvent mie)
        {
            PlatformTools.Clipboard.SetText(shownContainers.Where(lc => lc.Valid).Select(lc => lc.Length).ToTSV());
        }

        private void AddLapButton_OnClick(MouseInputEvent mie)
        {
            GetLayer<PopupLayer>().Popup(new AddLapTimeNode(Pilot, AddManualLap));
        }

        public void AddManualLaps(IEnumerable<double> laps)
        {
            DateTime from = Race.Start;
            foreach (double seconds in laps)
            {
                TimeSpan time = TimeSpan.FromSeconds(seconds);

                AddManualLap(time);
                from += time;
            }
        }

        private void AddManualLap(TimeSpan timeSinceLast)
        {
            AddManualLap(Pilot, timeSinceLast);
        }

        private void AddManualLap(Pilot pilot, TimeSpan timeSinceLast)
        {
            DateTime start = Race.Start;

            LapEditorContainer last = lapContainers.Where(r => r.Valid).LastOrDefault();
            if (last != null)
            {
                start = last.End;
            }

            LapEditorContainer newLC = new LapEditorContainer(start, start + timeSinceLast, ChannelColor);
            newLC.OnValidityChanged += UpdateNumbersEtc;
            newLC.OnSplitLap += OnSplitLap;
            newLC.OnTimeChanged += () => { UpdateNumbersEtc(); };
            lapContainers.Add(newLC);
            OnLapContainerCreated(newLC);

            Layout();
            UpdateNumbersEtc();
        }

        protected void Layout()
        {
            foreach (LapEditorContainer lc in lapContainers.Concat(sectorContainers.Values.SelectMany(c => c)))
            {
                lc.Remove();
            }

            if (lapRows == null)
            {
                lapRows = new LapRowListNode(Theme.Current.ScrollBar.XNA);
                lapRows.VisibleRows = VisibleLapRows;
                lapRows.ItemPadding = 2;
                lapsNode.AddChild(lapRows);
            }
            else
            {
                lapRows.ClearDisposeChildren();
            }

            int perRow = LapsPerRow;
            List<Node> rows = new List<Node>();

            foreach (LapEditorContainer lc in shownContainers)
            {
                Node row = rows.LastOrDefault();

                if (row == null || row.ChildCount >= perRow)
                {
                    row = new Node();
                    rows.Add(row);
                }
                row.AddChild(lc);
            }

            foreach (Node row in rows)
            {
                AlignHorizontally(0.01f, perRow, row.Children.ToArray());
                lapRows.AddChild(row);
            }

            // e.g. without split timing. They're added in the replay, where the pilot can be seen crossing the gate.
            if (Sector > 0 && !rows.Any())
            {
                TextNode noTimes = new TextNode("No " + SectorSelection.GetName(Sector) + " times yet - add them in the replay by right clicking the pilot's times", Theme.Current.Editor.Text.XNA);
                lapRows.AddChild(noTimes);
            }

            lapRows.RequestLayout();
        }

        protected void OnSplitLap(LapEditorContainer original, int splits)
        {
            TimeSpan newLength = TimeSpan.FromSeconds(original.Length.TotalSeconds / splits);

            DateTime lapStart = original.Start;
            DateTime lapEnd = lapStart;

            List<LapEditorContainer> newLapConts = new List<LapEditorContainer>();
            for (int i = 0; i < (splits - 1); i++)
            {
                lapEnd = lapStart + newLength;

                LapEditorContainer newLC = new LapEditorContainer(lapStart, lapEnd, ChannelColor);
                newLC.OnValidityChanged += UpdateNumbersEtc;
                newLC.OnSplitLap += OnSplitLap;
                newLC.OnTimeChanged += () => { UpdateNumbersEtc(); };
                lapContainers.Add(newLC);
                OnLapContainerCreated(newLC);

                lapStart = lapEnd;

                newLapConts.Add(newLC);
            }

            original.Splits = newLapConts;
            original.RecordTint = null;

            Layout();
            UpdateNumbersEtc();
        }

        protected virtual void UpdateNumbersEtc()
        {
            DateTime prev = Race.Start;
            int number = Race.Event.PrimaryTimingSystemLocation == PrimaryTimingSystemLocation.Holeshot ? 0 : 1;

            foreach (LapEditorContainer lve in orderedLapContainers)
            {
                if (lve.Valid)
                {
                    lve.Number = number;
                    lve.Start = prev;
                    prev = lve.End;
                    lve.Refresh();

                    number++;
                }
            }

            UpdateRaceNode();
        }

        protected void UpdateRaceNode()
        {
            float textHeight = 0.8f;

            raceNode.ClearDisposeChildren();

            TextNode start = new TextNode("0.00", Theme.Current.Editor.Text.XNA);
            start.RelativeBounds = new RectangleF(0, 0 - textHeight, 1, textHeight);
            start.Alignment = RectangleAlignment.TopLeft;
            raceNode.AddChild(start);

            TextNode end = new TextNode(Race.Length.TotalSeconds.ToString("0.00"), Theme.Current.Editor.Text.XNA);
            end.RelativeBounds = new RectangleF(0, 0 - textHeight, 1, textHeight);
            end.Alignment = RectangleAlignment.TopRight;
            raceNode.AddChild(end);

            DateTime last = Race.Start;
            var validOrderedLapContainers = shownContainers.Where(r => r.Valid);
            
            if (validOrderedLapContainers.Any())
            {
                last = validOrderedLapContainers.Select(l => l.End).Max();
            }
            TimeSpan length = last - Race.Start;

            double maxSeconds = Math.Min(Race.Length.TotalSeconds, length.TotalSeconds);

            float width = 0.0025f;

            DateTime raceStart = Race.Start;

            float prevFactor = 0;
            foreach (LapEditorContainer lve in shownContainers)
            {
                TimeSpan sinceRaceStart = lve.End - raceStart;

                // Clamped defensively - a lap end past the race length (e.g. a spurious
                // recalculated crossing from RSSI data that extends beyond the race) would
                // otherwise place its tick outside raceNode's own bounds entirely.
                float factor = Math.Clamp((float)(sinceRaceStart.TotalSeconds / maxSeconds), 0f, 1f);

                ColorNode colorNode = new ColorNode(ChannelColor);
                colorNode.Alpha = lve.Valid ? 1 : DisabledAlpha;
                colorNode.RelativeBounds = new RectangleF(factor - (width / 2), 0.1f, width, 0.8f);
                raceNode.AddChild(colorNode);

                if (lve.Valid)
                {
                    TextNode lapTime = new TextNode(Lap.LapNumberToString(lve.Number), Theme.Current.Editor.Text.XNA);
                    lapTime.RelativeBounds = new RectangleF(prevFactor, (1 - textHeight) / 2, factor - prevFactor, textHeight);
                    raceNode.AddChild(lapTime);

                    prevFactor = factor;
                }
            }
            raceNode.RequestLayout();
        }

        private void CancelButton_OnClick(Composition.Input.MouseInputEvent mie)
        {
            OnCancel?.Invoke(Laps);
            Dispose();
        }

        protected virtual void OkButton_OnClick(Composition.Input.MouseInputEvent mie)
        {
            LapEditorContainer focused = lapContainers.FirstOrDefault(lc => lc.TextEditorFocused);
            if (focused != null)
            {
                focused.HasFocus = false;
            }

            foreach (var lc in lapContainers.Where(lc => lc.Lap == null))
            {
                lc.CreateLap(RaceManager, Pilot);
            }

            // Do any splits we gotta do.
            foreach (var lc in lapContainers.Where(lc => lc.Splits != null && lc.Lap != null))
            {
                lc.DoSplit(RaceManager);
            }

            using (IDatabase db = DatabaseFactory.Open(Race.Event.ID))
            {
                foreach (var lc in lapContainers)
                {
                    lc.SaveChanges(db);
                }

                // The split times. The last sector's are the laps', saved above.
                foreach (var lc in sectorContainers.Values.SelectMany(c => c))
                {
                    lc.SaveChanges(db);
                }
            }

            RaceManager.RecalcuateLaps(Pilot, Race);

            OnOK?.Invoke(Laps);
            Dispose();
        }
    }

    // A list of lap rows that keeps every row the same readable height no matter how many
    // there are, scrolling once they overflow. ItemHeight is in pixels, so it is derived from
    // the laid out height rather than fixed, to hold up across resolutions and dialog sizes.
    public class LapRowListNode : ListNode<Node>
    {
        public int VisibleRows { get; set; }

        public LapRowListNode(Color scrollColor)
            : base(scrollColor)
        {
            VisibleRows = 6;
        }

        public override void Layout(RectangleF parentBounds)
        {
            RectangleF bounds = CalculateRelativeBounds(parentBounds);

            int rows = Math.Max(1, VisibleRows);
            ItemHeight = Math.Max(1, (int)(bounds.Height / rows) - ItemPadding);

            base.Layout(parentBounds);
        }
    }

    public class LapEditorContainer : Node
    {
        public Lap Lap { get; private set; }

        // Set for a sector's time rather than a lap: the detection it ends at.
        public Detection Detection { get; private set; }

        // The last sector ends with the lap, so its times stand for the laps' ends and change them rather than saving themselves.
        public LapEditorContainer LinkedLap { get; set; }

        public bool IsSectorTime { get { return Detection != null; } }

        public bool Valid { get; set; }
        public int Number { get; set; }
        public TimeSpan Length { get { return End - Start; } }

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        private TextEditNode lapTime;
        private TextNode lapNumber;

        public event System.Action OnValidityChanged;
        public event Action<LapEditorContainer, int> OnSplitLap;
        public event System.Action OnTimeChanged;
        public event Action<LapEditorContainer> OnSelected;

        public List<LapEditorContainer> Splits { get; set; }

        public bool TextEditorFocused { get { return lapTime.HasFocus; } }

        private ColorNode selectionOverlay;

        // Personal / overall best highlight. Null means this lap isn't a record. Matches the
        // lap bar under the pilot feed, which tints the text rather than the channel-coloured
        // background (see LapNode.Tint).
        private Color? recordTint;

        public Color? RecordTint
        {
            get { return recordTint; }
            set
            {
                recordTint = value;
                ApplyTextTint();
            }
        }

        public bool IsSelected
        {
            get { return selectionOverlay.Visible; }
            set { selectionOverlay.Visible = value; }
        }

        private void ApplyTextTint()
        {
            // An invalidated lap is no longer a record, so drop the highlight until it's valid again.
            // Tint multiplies the text colour, so White leaves it as Editor.Text (same as LapsNode).
            Color tint = (Valid && recordTint.HasValue) ? recordTint.Value : Color.White;

            lapNumber.Tint = tint;
            lapTime.Tint = tint;
        }


        public LapEditorContainer(Lap lap, Color channelColor)
            :this(lap.Start, lap.End, channelColor)
        {
            Lap = lap;
            Valid = lap.Detection.Valid;
            Number = lap.Number;

            Refresh();
        }

        public LapEditorContainer(SectorTime sectorTime, Color channelColor)
            : this(sectorTime.Start, sectorTime.End, channelColor)
        {
            Detection = sectorTime.Detection;
            Valid = sectorTime.Valid;
            Number = sectorTime.LapNumber;

            Refresh();
        }

        public LapEditorContainer(DateTime start, DateTime end, Color channelColor)
        {
            Start = start;
            End = end;
            Valid = true;

            ImageNode imageNode = new ImageNode(@"img/lapbg.png");
            imageNode.KeepAspectRatio = false;
            imageNode.Tint = channelColor;
            AddChild(imageNode);

            lapNumber = new TextNode("", Theme.Current.Editor.Text.XNA);
            lapNumber.Alignment = RectangleAlignment.CenterLeft;
            AddChild(lapNumber);

            lapTime = new TextEditNode("", Theme.Current.Editor.Text.XNA);
            lapTime.TextChanged += LapTime_TextChanged;
            AddChild(lapTime);

            lapNumber.RelativeBounds = new RectangleF(0.05f, 0, 0.3f, 1);

            float starts = lapNumber.RelativeBounds.Right + 0.05f;

            lapTime.RelativeBounds = new RectangleF(starts, 0, 1 - starts, 1);

            selectionOverlay = new ColorNode(Color.Cyan);
            selectionOverlay.Alpha = 0.35f;
            selectionOverlay.Visible = false;
            AddChild(selectionOverlay);

            Refresh();
        }

        private void LapTime_TextChanged(string obj)
        {
            try
            {
                float seconds;
                if (float.TryParse(obj, out seconds))
                {
                    if (seconds < 0)
                    {
                        seconds = 0;
                    }
                    if (seconds > 3600)
                    {
                        seconds = 3600;
                    }

                    End = Start + TimeSpan.FromSeconds(seconds);

                    // Record status was for the saved time, not this edited one.
                    recordTint = null;
                }
            }
            catch
            {
                End = DateTime.MaxValue;
            }

            Refresh();
            OnTimeChanged?.Invoke();
        }

        public void Refresh()
        {
            Alpha = Valid ? 1 : LapEditorNode.DisabledAlpha;

            ApplyTextTint();

            if (Valid)
            {
                lapNumber.Text = Lap.LapNumberToString(Number);
            }
            else
            {
                lapNumber.Text = "";
            }
            if (!lapTime.HasFocus)
            {
                lapTime.Text = Length.ToStringRaceTime();
            }
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (mouseInputEvent.Button == MouseButtons.Left)
            {
                if (CompositorLayer.InputEventFactory.AreControlKeysDown() && mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    lapTime.HasFocus = false;
                    ToggleValidity();
                    return true;
                }

                if (mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    OnSelected?.Invoke(this);
                }
            }
            else if (mouseInputEvent.Button == MouseButtons.Right)
            {
                lapTime.HasFocus = false;
                if (mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    MouseMenu mm = new MouseMenu(this);

                    string time = IsSectorTime ? "Time" : "Lap";

                    string valid = "Re-qualify " + time;
                    if (Valid)
                        valid = "Disqualify " + time;

                    mm.AddItem(valid, () =>
                    {
                        ToggleValidity();
                    });

                    if (!IsSectorTime)
                    {
                        mm.AddSubmenu("Split Lap into...", (i) =>
                        {
                            OnSplitLap?.Invoke(this, i);
                        }, new int[] { 2, 3, 4, 5, 6, 7 });
                    }

                    mm.Show(mouseInputEvent);
                }
            }

            return base.OnMouseInput(mouseInputEvent);
        }

        private void ToggleValidity()
        {
            Valid = !Valid;

            Refresh();
            OnValidityChanged?.Invoke();
        }

        public void CreateLap(RaceManager raceManager, Pilot pilot)
        {
            if (Lap == null && Valid)
            {
                raceManager.AddManualLap(pilot, End);
            }
        }

        public void SaveChanges(IDatabase db)
        {
            if (Lap != null)
            {
                Lap.Detection.Valid = Valid;
                Lap.End = End;

                db.Update(Lap.Detection);
                db.Update(Lap);
            }
            else if (IsSectorTime && LinkedLap == null)
            {
                if (Detection.Valid != Valid)
                {
                    Detection.Valid = Valid;
                    Detection.ValidityType = RaceLib.Detection.ValidityTypes.ManualOverride;
                }
                Detection.Time = End;

                db.Update(Detection);
            }
        }

        public void DoSplit(RaceManager raceManager)
        {
            Lap[] newLaps = raceManager.SplitLap(Lap, Splits.Count + 1).ToArray();

            for (int i = 0; i < newLaps.Length && i < Splits.Count; i++)
            {
                Splits[i].Lap = newLaps[i];

                if (Splits[i].Splits != null)
                {
                    Splits[i].DoSplit(raceManager);
                }
            }
        }
    }
}
