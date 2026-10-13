using Composition;
using Composition.Input;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using RaceLib;
using RaceLib.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using UI.Nodes;

namespace UI.Video
{
    public class SeekNode : Node
    {
        public event Action<DateTime> Seek;

        private ProgressBarNode progressBar;
        private Node progressBarLineContainer;

        public DateTime CurrentTime
        {
            get
            {
                return FactorToTime(progressBar.Progress);
            }
            set
            {
                progressBar.Progress = TimeToFactor(value);
            }
        }

        public ImageButtonNode PlayButton { get; private set; }
        public TextCheckBoxNode SlowCheck { get; private set; }
        public ImageButtonNode StopButton { get; private set; }

        public TextButtonNode ShowAll { get; private set; }

        private Node buttonsNode;
        private Node barContainer;

        private Node flagLabels;

        // Picks the laps, or a sector, for the markers and the pilots' lap times. Only there for races with laps.
        private SectorSelection sectorSelection;
        private SectorSelectorNode sectorSelector;

        private const float slowWidth = 0.05f;
        private const float showAllWidth = 0.05f;
        private const float sectorWidth = 0.06f;

        private EventManager eventManager;

        private Race race;
        private DateTime mediaStart;
        private DateTime mediaEnd;

        private Pilot filterPilot;

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public SeekNode(EventManager eventManager, Color color, SectorSelection sectorSelection)
        {
            this.eventManager = eventManager;
            this.sectorSelection = sectorSelection;
            sectorSelection.OnChanged += SectorSelection_OnChanged;

            barContainer = new Node();
            barContainer.RelativeBounds = new RectangleF(0, 0.0f, 1, 0.6f);
            AddChild(barContainer);

            buttonsNode = new Node();
            buttonsNode.RelativeBounds = new RectangleF(0, 0, 0.02f, 1);
            barContainer.AddChild(buttonsNode);

            PlayButton = new ImageButtonNode(@"img\start.png", Color.Transparent, Theme.Current.Hover.XNA, color);
            buttonsNode.AddChild(PlayButton);

            StopButton = new ImageButtonNode(@"img\stop.png", Color.Transparent, Theme.Current.Hover.XNA, color);
            buttonsNode.AddChild(StopButton);

            sectorSelector = new SectorSelectorNode(sectorSelection, Color.Transparent, Theme.Current.Hover.XNA, color);
            sectorSelector.RelativeBounds = new RectangleF(1 - (showAllWidth + slowWidth + sectorWidth), 0, sectorWidth, 1);
            sectorSelector.Scale(1, 0.8f);
            sectorSelector.Visible = false;
            barContainer.AddChild(sectorSelector);

            SlowCheck = new TextCheckBoxNode("Slow", color, false);
            SlowCheck.RelativeBounds = new RectangleF(1 - (showAllWidth + slowWidth), 0, slowWidth, 1);
            SlowCheck.SetRatio(0.6f, 0.05f);
            SlowCheck.Scale(1, 0.8f);
            barContainer.AddChild(SlowCheck);

            ShowAll = new TextButtonNode("Show All", Color.Transparent, Theme.Current.Hover.XNA, color);
            ShowAll.RelativeBounds = new RectangleF(1 - showAllWidth, 0, showAllWidth, 1);
            ShowAll.Scale(1, 0.8f);
            ShowAll.TextNode.RelativeBounds = new RectangleF(0, 0, 1, 1);
            barContainer.AddChild(ShowAll);

            progressBar = new ProgressBarNode(color);
            barContainer.AddChild(progressBar);

            progressBarLineContainer = new Node();
            progressBar.AddChild(progressBarLineContainer);

            flagLabels = new Node();
            AddChild(flagLabels);

            LayoutBar();
        }

        public override void Dispose()
        {
            sectorSelection.OnChanged -= SectorSelection_OnChanged;
            base.Dispose();
        }

        // The bar takes up the room the sector selector would when it's not there.
        private void LayoutBar()
        {
            float right = showAllWidth + slowWidth;
            if (sectorSelector.Visible)
            {
                right += sectorWidth;
            }

            progressBar.RelativeBounds = new RectangleF(buttonsNode.RelativeBounds.Right, 0, 1 - (buttonsNode.RelativeBounds.Right + right), 1);
            flagLabels.RelativeBounds = new RectangleF(progressBar.RelativeBounds.X, barContainer.RelativeBounds.Bottom, progressBar.RelativeBounds.Width, 1 - barContainer.RelativeBounds.Bottom);
            RequestLayout();
        }

        private void SectorSelection_OnChanged()
        {
            if (race != null)
            {
                SetRace(race, mediaStart, mediaEnd);
            }
        }

        public void ClearFlags()
        {
            flagLabels.ClearDisposeChildren();
            progressBarLineContainer.ClearDisposeChildren();
        }

        public void ClearRace()
        {
            ClearFlags();
            race = null;
            filterPilot = null;
        }

        private DateTime FactorToTime(float factor)
        {
            TimeSpan length = End - Start;
            return Start + TimeSpan.FromSeconds(length.TotalSeconds * factor);
        }

        private float TimeToFactor(DateTime dateTime)
        {
            TimeSpan length = End - Start;
            TimeSpan time = dateTime - Start;

            float factor = (float)(time.TotalSeconds / length.TotalSeconds);

            return Math.Clamp(factor, 0, 1);
        }

        // Limits the lap / game point markers to a single pilot. Null shows every pilot in the race.
        public void SetPilotFilter(Pilot pilot)
        {
            if (filterPilot == pilot)
                return;

            filterPilot = pilot;

            if (race != null)
            {
                SetRace(race, mediaStart, mediaEnd);
            }
        }

        public void SetRace(Race race, DateTime mediaStart, DateTime mediaEnd)
        {
            ClearFlags();

            this.race = race;
            this.mediaStart = mediaStart;
            this.mediaEnd = mediaEnd;

            Start = race.Start > mediaStart ? mediaStart : race.Start;
            End = race.End < mediaEnd ? mediaEnd : race.End;

            int[] splitTimingSystems = eventManager.RaceManager.GetSplitTimingSystems();
            int sectorCount = race.GetSectorCount(splitTimingSystems);
            sectorSelector.SectorCount = sectorCount;
            if (sectorSelector.Visible != sectorCount > 0)
            {
                sectorSelector.Visible = sectorCount > 0;
                LayoutBar();
            }

            // e.g. a race with fewer sectors than the one replayed before it.
            if (sectorSelection.Sector > sectorCount)
            {
                // Which comes back here to show the laps.
                sectorSelection.Select(0);
                return;
            }

            int sector = sectorSelection.Sector;

            foreach (PilotChannel pilotChannel in race.PilotChannelsSafe)
            {
                if (filterPilot != null && pilotChannel.Pilot != filterPilot)
                    continue;

                Color tint = eventManager.GetRaceChannelColor(race, pilotChannel.Channel);

                if (sector > 0)
                {
                    foreach (SectorTime sectorTime in race.GetSectorTimes(pilotChannel.Pilot, splitTimingSystems, sector))
                    {
                        AddTimeMarker(sectorTime.End, tint, "L" + sectorTime.LapNumber + " S" + sector);
                    }
                }
                else
                {
                    Lap[] laps = race.GetValidLaps(pilotChannel.Pilot, true);
                    foreach (Lap l in laps)
                    {
                        DateTime time = l.Detection.Time;

                        string lapNumber = "L" + l.Number.ToString();
                        if (l.Number == 0)
                        {
                            lapNumber = "HS";
                        }

                        AddTimeMarker(time, tint, lapNumber);
                    }
                }
            }

            AddFlagAtTime(race.Start, Color.Green);
            AddFlagAtTime(race.End, Color.White);

            if (race.Event.Flags != null)
            {
                IEnumerable<DateTime> flags = race.Event.Flags.Where(f => race.Start <= f && race.End >= f);
                foreach (DateTime flag in flags)
                {
                    AddFlagAtTime(flag, Color.Yellow);
                }
            }

            if (race.GamePoints != null)
            {
                foreach (GamePoint gamePoint in race.GamePoints)
                {
                    if (filterPilot != null && race.GetPilot(gamePoint.Channel) != filterPilot)
                        continue;

                    Color tint = eventManager.GetRaceChannelColor(race, gamePoint.Channel);

                    AddTimeMarker(gamePoint.Time, tint, gamePoint.Channel.DisplayName);
                }
            }

            RequestLayout();
        }


        private void AddFlagAtTime(DateTime time, Color tint)
        {
            float factor = AddLineAtTime(time, tint);
            ImageNode flag = new ImageNode(@"img/raceflag.png", tint);

            flag.RelativeBounds = new RectangleF(factor, 0, 0.02f, 1f);
            flag.KeepAspectRatio = false;
            flag.CanScale = false;
            flag.Alignment = RectangleAlignment.CenterLeft;
            flagLabels.AddChild(flag);
        }

        private void AddTimeMarker(DateTime time, Color tint, string text)
        {
            float factor = AddLineAtTime(time, tint);
            TextNode textNode = new TextNode(text, tint);
            textNode.RelativeBounds = new RectangleF(factor, 0, 1, 1.2f);
            textNode.Alignment = RectangleAlignment.BottomLeft;
            flagLabels.AddChild(textNode);
        }

        private float AddLineAtTime(DateTime time, Color tint)
        {
            float factor = TimeToFactor(time);
            ImageNode flag = new ImageNode(@"img/flag.png", tint);
            flag.RelativeBounds = new RectangleF(factor, 0, 1, 1f);
            flag.Alignment = RectangleAlignment.CenterLeft;
            flag.CanScale = false;
            progressBarLineContainer.AddChild(flag);

            return factor;
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (Mouse.GetState().LeftButton == ButtonState.Pressed && Seek != null)
            {
                if (progressBar.Contains(mouseInputEvent.Position))
                {
                    int x = mouseInputEvent.Position.X - progressBar.Bounds.X;
                    float factor = x / (float)progressBar.Bounds.Width;

                    DateTime time = FactorToTime(factor);
                    Seek(time);
                }
            }

            return base.OnMouseInput(mouseInputEvent);
        }
    }
}
