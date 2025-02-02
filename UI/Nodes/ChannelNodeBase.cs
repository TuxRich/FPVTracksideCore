﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using RaceLib;
using Sound;
using Tools;
using UI.Widgets;

namespace UI.Nodes
{
    public class ChannelNodeBase : AnimatedNode, IButtonNode
    {
        public delegate void ChannelNodeDelegate(ChannelNodeBase cn);

        public Pilot Pilot { get; private set; }

        public event MouseInputDelegate OnClick;
        public event System.Action OnCloseClick;
        public event System.Action OnCrashedOutClick;
        public event System.Action OnFullscreen;
        public event System.Action OnShowAll;

        public Channel Channel { get; private set; }

        public Node DisplayNode { get; private set; }
        public LapsNode LapsNode { get; private set; }
        public SplitsNode SplitsNode { get; private set; }

        public CloseNode CloseButton { get; private set; }

        public Color ChannelColor { get; private set; }

        private ChannelPilotNameNode pilotNameNode;

        private ImageNode pbBackground;
        private PBContainerNode PBNode;

        protected ColorNode resultBackground;
        private TextNode finalPosition;
        private TextNode raceSummary1;
        private TextNode raceSummary2;
        private TextNode channelInfo;
        
        private TextNode rssiNode;
        private TextNode sensitivityNode;

        public EventManager EventManager { get; private set; }

        private ChangeAlphaTextNode recentPositionNode;
        private ChangeAlphaTextNode behindTime;

        public int Position { get; set; }

        public TimeSpan PBTime { get { return PBNode.PBTimeNode.Time; } }

        public enum CrashOutType
        {
            None,
            Auto,
            Manual
        }

        public bool CrashedOut
        {
            get
            {
                return CrashedOutType != CrashOutType.None;
            }
        }

        public CrashOutType CrashedOutType { get; set; }
        public bool Finished { get { return resultBackground.Visible; } }

        public bool BiggerChannelInfo 
        { 
            get 
            { 
                return channelInfo.RelativeBounds.Height > 0.05f; 
            } 
            set
            {
                float height = value ? 0.12f : 0.05f; 
                channelInfo.RelativeBounds = new RectangleF(0.0f, 0.01f, 0.99f, height);
            } 
        }

        private ColorNode crashedOut;

        private bool needUpdatePosition;
        private Detection nextDetection;
        private bool needsLapRefresh;
        private bool needsSplitClear;

        public event Action RequestReorder;
        public event Action OnPBChange;

        public const float LapLineHeight = 0.073f;

        public bool SingleRow { get; set; }
        
        private DateTime? playbackTime;

        public PilotProfileNode PilotProfile { get; set; }

        public WidgetManagerNode WidgetManager { get; set; }

        public ChannelNodeBase(EventManager eventManager, Channel channel, Color channelColor)
        {
            EventManager = eventManager;
            CrashedOutType = CrashOutType.None;
            ChannelColor = channelColor;
            Channel = channel;
            Position = EventManager.GetMaxPilotsPerRace();

            EventManager.OnEventChange += EventManager_OnEventChange;
            EventManager.RaceManager.OnSplitDetection += RaceManager_OnSplitDetection;
            EventManager.RaceManager.OnLapDetected += RaceManager_OnLapDetected;
            EventManager.RaceManager.OnLapDisqualified += RaceManager_OnLapDisqualified;
            EventManager.RaceManager.OnPilotAdded += RaceManager_OnPilotChanged;
            EventManager.RaceManager.OnPilotRemoved += RaceManager_OnPilotChanged;
            EventManager.RaceManager.OnRaceEnd += RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceClear += RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceChanged += RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceReset += RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceResumed += RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnLapsRecalculated += RaceManager_OnLapsRecalculated;
            EventManager.LapRecordManager.OnNewPersonalBest += RecordManager_OnNewPersonalBest;
            EventManager.RaceManager.OnRaceResumed += RaceManager_OnRaceChanged;
        }

        public override void Dispose()
        {
            EventManager.OnEventChange -= EventManager_OnEventChange;
            EventManager.RaceManager.OnSplitDetection -= RaceManager_OnSplitDetection;
            EventManager.RaceManager.OnLapDetected -= RaceManager_OnLapDetected;
            EventManager.RaceManager.OnLapDisqualified -= RaceManager_OnLapDisqualified;
            EventManager.RaceManager.OnPilotAdded -= RaceManager_OnPilotChanged;
            EventManager.RaceManager.OnPilotRemoved -= RaceManager_OnPilotChanged;
            EventManager.RaceManager.OnRaceEnd -= RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceClear -= RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceChanged -= RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceReset -= RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnRaceResumed -= RaceManager_OnRaceChanged;
            EventManager.RaceManager.OnLapsRecalculated -= RaceManager_OnLapsRecalculated;
            EventManager.LapRecordManager.OnNewPersonalBest -= RecordManager_OnNewPersonalBest;

            base.Dispose();
        }

        private void RecordManager_OnNewPersonalBest(Pilot p, int recordLapCount, Lap[] laps)
        {
            // Reminder, this needs refreshing even if its NOT the right pilot. As this pilot may have lost the record
            needUpdatePosition = true;
            needsLapRefresh = true;
        }

        private void RaceManager_OnLapsRecalculated(Race race)
        {
            needUpdatePosition = true; 
            needsLapRefresh = true;
        }

        private void RaceManager_OnRaceChanged(Race race)
        {
            needUpdatePosition = true; 
            needsLapRefresh = true; 
            needsSplitClear = true; 
            CrashedOutType = CrashOutType.None;
        }

        private void RaceManager_OnPilotChanged(PilotChannel pc)
        {
            CrashedOutType = CrashOutType.None;
            needUpdatePosition = true;
        }

        private void RaceManager_OnLapDisqualified(Lap lap)
        {
            needUpdatePosition = true;
        }

        private void RaceManager_OnLapDetected(Lap lap)
        {
            RaceManager_OnSplitDetection(lap.Detection);
            if (Pilot == lap.Pilot)
            {
                LapsNode.AddLap(lap);
            }
        }

        private void RaceManager_OnSplitDetection(Detection d)
        {
            needUpdatePosition = true; 
            nextDetection = d;
        }

        private void EventManager_OnEventChange()
        {
            needUpdatePosition = true; 
            needsLapRefresh = true;
            needsSplitClear = true;
        }

        protected virtual Node CreateDisplayNode()
        {
            ColorNode mainNode = new ColorNode(Theme.Current.PilotViewTheme.NoVideoBackground);
            mainNode.KeepAspectRatio = false;
            return mainNode;
        }

        public virtual void Init()
        {
            float pilotAlpha = Theme.Current.PilotViewTheme.PilotTitleAlpha / 255.0f;

            PilotProfile = new PilotProfileNode();
            PilotProfile.SetAnimatedVisibility(false);

            LapsNode = new LapsNode(EventManager);
            LapsNode.LapLines = 4;
            LapsNode.ChannelColor = ChannelColor;
            LapsNode.RelativeBounds = new RectangleF(0, 1 - (LapsNode.LapLines * LapLineHeight), 1, LapLineHeight * LapsNode.LapLines);

            DisplayNode = CreateDisplayNode();

            AddChild(DisplayNode);
            DisplayNode.AddChild(PilotProfile);
            AddChild(LapsNode);

            //WidgetManager = new WidgetManagerNode();
            //DisplayNode.AddChild(WidgetManager);

            if (GeneralSettings.Instance.ShowSplitTimes)
            {
                SplitsNode = new SplitsNode(EventManager);
                SplitsNode.RelativeBounds = new RectangleF(0, LapsNode.RelativeBounds.Y - LapLineHeight, 1, LapLineHeight);
                AddChild(SplitsNode);
            }

            pilotNameNode = new ChannelPilotNameNode(this, ChannelColor, pilotAlpha);
            pilotNameNode.RelativeBounds = new RectangleF(0, 0.03f, 0.4f, 0.12f);
            DisplayNode.AddChild(pilotNameNode);

            channelInfo = new TextNode(Channel.GetBandChannelText(), Color.White);
            channelInfo.RelativeBounds = new RectangleF(0.0f, 0.01f, 0.99f, 0.1f);
            channelInfo.Alpha = pilotAlpha;
            channelInfo.Alignment = RectangleAlignment.TopRight;
            channelInfo.Style.Border = true;
            DisplayNode.AddChild(channelInfo);

            CloseButton = new CloseNode();
            CloseButton.Visible = false;
            AddChild(CloseButton);

            CloseButton.OnClick += (mie) => 
            {
                Close();
            };

            resultBackground = new ColorNode(Theme.Current.PilotViewTheme.PilotOverlayPanel);
            resultBackground.Alpha = pilotAlpha;
            resultBackground.RelativeBounds = new RectangleF(0, 0.3f, 1, 0.4f);
            DisplayNode.AddChild(resultBackground);

            finalPosition = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            finalPosition.RelativeBounds = new RectangleF(0, 0, 1, 0.75f);
            finalPosition.Style.Bold = true;
            finalPosition.Alignment = RectangleAlignment.BottomCenter;

            RectangleF raceSummaryLocations = new RectangleF(0.0f, 0.7f, 1.0f, 0.145f);

            raceSummary1 = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            raceSummary1.RelativeBounds = raceSummaryLocations;
            raceSummary1.Alignment = RectangleAlignment.BottomCenter;

            raceSummaryLocations.Y += 0.15f;
            raceSummaryLocations.Height -= 0.01f;

            raceSummary2 = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            raceSummary2.RelativeBounds = raceSummaryLocations;
            raceSummary2.Alignment = RectangleAlignment.BottomCenter;

            sensitivityNode = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            sensitivityNode.RelativeBounds = new RectangleF(0.005f, 0.92f, 0.99f, 0.07f);
            sensitivityNode.Alignment = RectangleAlignment.BottomRight;

            rssiNode = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            rssiNode.RelativeBounds = sensitivityNode.RelativeBounds;
            rssiNode.Alignment = RectangleAlignment.BottomLeft;

            resultBackground.AddChild(finalPosition);
            resultBackground.AddChild(raceSummary1);
            resultBackground.AddChild(raceSummary2);
            resultBackground.AddChild(rssiNode);
            resultBackground.AddChild(sensitivityNode);
            resultBackground.Visible = false;

            pbBackground = new ColorNode(Theme.Current.PilotViewTheme.PBBackground);
            pbBackground.RelativeBounds = new RectangleF(0.0f, pilotNameNode.RelativeBounds.Bottom, pilotNameNode.RelativeBounds.Width * 0.66f, 0.065f);
            pbBackground.KeepAspectRatio = false;
            pbBackground.Alpha = pilotAlpha;
            DisplayNode.AddChild(pbBackground);

            PBNode = new PBContainerNode(EventManager, Theme.Current.PilotViewTheme.PilotOverlayText.XNA, 1 / pbBackground.Alpha);
            PBNode.RelativeBounds = new RectangleF(0.1f, 0.05f, 0.8f, 0.95f);
            PBNode.PBTimeNode.OnNewPB += () => { OnPBChange?.Invoke(); };
            pbBackground.AddChild(PBNode);

            recentPositionNode = new ChangeAlphaTextNode("", Theme.Current.PilotViewTheme.PositionText.XNA);
            recentPositionNode.RelativeBounds = new RectangleF(0.6f, 0, 0.4f, 0.3f);
            recentPositionNode.Alignment = RectangleAlignment.TopRight;
            recentPositionNode.Style.Bold = true;
            recentPositionNode.Style.Border = true;
            recentPositionNode.RelativeBounds = new RectangleF(0.6f, 0, 0.4f, 0.3f);
            DisplayNode.AddChild(recentPositionNode);

            behindTime = new ChangeAlphaTextNode("", Theme.Current.PilotViewTheme.PositionText.XNA);
            behindTime.RelativeBounds = new RectangleF(0.7f, recentPositionNode.RelativeBounds.Bottom, 0.3f, 0.07f);
            behindTime.Alignment = RectangleAlignment.TopRight;
            DisplayNode.AddChild(behindTime);

            crashedOut = new ColorNode(Theme.Current.PilotViewTheme.CrashedOut);
            crashedOut.KeepAspectRatio = false;
            crashedOut.Visible = false;
            DisplayNode.AddChild(crashedOut);

            AddChild(new ShadowNode());
            SetPilot(null);
        }

        private void Close()
        {
            if (CrashedOut || Finished || EventManager.RaceManager.CanRunRace)
            {
                OnCloseClick?.Invoke();
                CrashedOutType = CrashOutType.Manual;
            }
            else
            {
                OnCrashedOutClick.Invoke();
                CrashedOutType = CrashOutType.Manual;
            }
        }

        public void SetPilot(Pilot pilot)
        {
            recentPositionNode.Text = "";
            Position = EventManager.GetMaxPilotsPerRace();
            LapsNode.ClearLaps();

            if (SplitsNode != null)
                SplitsNode.Clear();

            Pilot = pilot;

            pilotNameNode.SetPilot(Pilot);

            LapsNode.SetPilot(Pilot);
            if (SplitsNode != null)
                SplitsNode.SetPilot(Pilot);

            PBNode.Pilot = Pilot;

            PilotProfile.SetPilot(Pilot);

            if (Pilot == null)
            {
                pbBackground.Visible = false;
                pilotNameNode.Visible = false;
            }
            else
            {
                pilotNameNode.Visible = true;
                UpdatePosition(null);

                // Make PB invisible if not in TT or Race.
                pbBackground.Visible = PBNode.HasPB;
            }
            RequestLayout();
        }

        public void SetProfileVisible(bool visible, bool snap)
        {
            PilotProfile.SetAnimatedVisibility(visible);

            if (snap)
            {
                PilotProfile.Snap();
            }
        }

        public virtual void SetLapsVisible(bool visible)
        {
            LapsNode.Visible = visible;
        }

        public void SetResult(int position, bool dnf, TimeSpan behind, Pilot behindWho)
        {
            finalPosition.Text = position.ToStringPosition();

            if (EventManager.ResultManager.PointsSettings.DNFForUnfinishedRaces && dnf)
            {
                finalPosition.Text = "DNF";
            }

            raceSummary1.Text = "";
            raceSummary2.Text = "";
            sensitivityNode.Text = "Sens: " + Pilot.TimingSensitivityPercent + "%";
            rssiNode.Text = "";

            Race race = EventManager.RaceManager.CurrentRace;
            if (race != null)
            {
                Lap[] laps = race.GetValidLapsInRace(Pilot);

                if (laps.Any())
                {
                    Lap best = laps.OrderBy(l => l.Length).First();

                    if (race.Type == EventTypes.TimeTrial)
                    {
                        laps = laps.BestConsecutive(EventManager.Event.Laps).ToArray();
                    }

                    if (laps.Any() && !dnf)
                    {
                        TimeSpan totalTime = laps.TotalTime();
                        string preText = "";

                        switch (race.Type)
                        {
                            case EventTypes.Race:
                                Lap holeShot = race.GetHoleshot(Pilot);
                                if (holeShot != null)
                                {
                                    totalTime += holeShot.Length;
                                }
                                preText = "Finished";
                                break;
                            default:
                                if (laps.Length == 1)
                                {
                                    preText = laps.Length + " lap";
                                }
                                else
                                {
                                    preText = laps.Length + " laps";
                                }
                                break;
                        }

                        raceSummary1.Text += preText + " in " + totalTime.ToStringRaceTime() + " - ";
                    }

                    raceSummary1.Text += "Fastest lap " + best.Length.ToStringRaceTime();

                    if (EventManager.SpeedRecordManager.HasDistance)
                    {
                        IEnumerable<Split> splits = race.GetSplits(Pilot);
                        IEnumerable<float> speeds = EventManager.SpeedRecordManager.GetSpeeds(splits);
                        if (speeds.Any())
                        {
                            float maxSpeed = speeds.Max();
                            if (maxSpeed > 0)
                            {
                                raceSummary1.Text += " - " + EventManager.SpeedRecordManager.SpeedToString(maxSpeed, GeneralSettings.Instance.Units);
                            }
                        }
                    }

                    if (behind != TimeSpan.Zero && behindWho != null && !dnf)
                    {
                        raceSummary2.Text += "(+" + behind.ToStringRaceTime() + " behind " + behindWho.Name + ")";
                    }

                    IEnumerable<Detection> detections = race.GetLaps(Pilot).Select(l => l.Detection);
                    if (detections.Any())
                    {
                        double averagePower = detections.Select(d => d.Peak).Average();
                        rssiNode.Text += "RSSI: " + ((int)averagePower).ToString();
                    }
                }
            }

            resultBackground.Visible = true;
        }

        public void ClearResult()
        {
            if (resultBackground.Visible)
            {
                resultBackground.Visible = false;
                finalPosition.Text = "";
                raceSummary1.Text = "";
                raceSummary2.Text = "";
            }
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (base.OnMouseInput(mouseInputEvent))
            {
                return true;
            }


            if (mouseInputEvent.ButtonState == ButtonStates.Released)
            {
                OnClick?.Invoke(mouseInputEvent);
            }

            if (mouseInputEvent is MouseInputEnterEvent && Pilot != null)
            {
                CloseButton.Visible = true;
            }

            if (mouseInputEvent is MouseInputLeaveEvent)
            {
                CloseButton.Visible = false;
            }

            if (mouseInputEvent.Button == MouseButtons.Right && mouseInputEvent.ButtonState == ButtonStates.Released)
            {
                if (Pilot != null)
                {
                    MouseMenu mm = new MouseMenu(this);
                    mm.AddItem("Read Name / Channel", () => { SoundManager.Instance.PilotChannel(Pilot, Channel); });
                    mm.AddItem("Hurry Up", () => { SoundManager.Instance.HurryUp(Pilot); });

                    mm.AddItem("Edit Pilot", () =>
                    {
                        PilotEditor editor = new PilotEditor(EventManager, new Pilot[] { Pilot });
                        GetLayer<PopupLayer>().Popup(editor);
                    });

                    if (!EventManager.RaceManager.RaceRunning && !EventManager.RaceManager.RaceFinished)
                    {
                        mm.AddItem("Remove Pilot", () => { EventManager.RaceManager.RemovePilotFromCurrentRace(Pilot); });
                        mm.AddSubmenu("Change Channel", (c) => { EventManager.RaceManager.ChangeChannel(c, Pilot); }, EventManager.RaceManager.FreeChannels.ToArray());
                    }
                    else
                    {
                        mm.AddItem("Crashed Out", () =>
                        {
                            Close();
                        });

                        mm.AddItem("Full screen", () =>
                        {
                            OnFullscreen?.Invoke();
                        });

                        mm.AddItem("Show All", () =>
                        {
                            OnShowAll?.Invoke();
                        });
                    }

                    Race r = EventManager.RaceManager.CurrentRace;
                    if (r != null)
                    {
                        if (r.Ended)
                        {
                            mm.AddItem("Announce Results", () => { SoundManager.Instance.AnnounceResults(r); });
                        }
                        else
                        {
                            mm.AddItem("Announce Race / Pilots", () => { SoundManager.Instance.AnnounceRace(r, true); });
                            mm.AddItem("Hurry Up Everyone", () => { SoundManager.Instance.HurryUpEveryone(); });
                        }
                    }
                    mm.Show(mouseInputEvent);
                }

                return true;
            }

            return false;
        }

        public override void Update(GameTime gameTime)
        {
            if (needUpdatePosition)
            {
                pbBackground.Visible = PBNode.HasPB && Pilot != null;
                UpdatePosition(nextDetection);
                needUpdatePosition = false;
                nextDetection = null;
            }

            if (needsLapRefresh)
            {
                LapsNode.RefreshData();
                needsLapRefresh = false;
            }

            if (needsSplitClear)
            {
                if (SplitsNode != null)
                    SplitsNode.Clear();
                needsSplitClear = false;
            }

            // udpdate crashed out visible
            crashedOut.Visible = CrashedOut && !resultBackground.Visible;

            base.Update(gameTime);
        }


        public void UpdatePosition(Detection detection)
        {
            int oldPosition = Position;

            bool newDetection = false;
            bool holeshot = false;
            if (detection != null)
            {
                newDetection = detection.Pilot == Pilot;
                holeshot = detection.IsHoleshot;
            }

            bool hasFinished = false;
            Race race = EventManager.RaceManager.CurrentRace;

            if (Pilot != null && race != null && (EventManager.RaceManager.RaceType.HasResult()))
            {
                int position;
                TimeSpan behind;
                Pilot behindWho;

                bool showPosition;

                if (EventManager.RaceManager.RaceType == EventTypes.TimeTrial)
                {
                    showPosition = EventManager.LapRecordManager.GetPosition(Pilot, EventManager.Event.Laps, out position, out behindWho, out behind);

                    if (detection != null)
                    {
                        if (!detection.IsLapEnd)
                            showPosition = false;
                    }
                }
                else
                {
                    showPosition = race.GetPosition(Pilot, out position, out behindWho, out behind);
                }

                if (Pilot.HasFinished(EventManager))
                {
                    hasFinished = true;
                    bool dnf = EventManager.ResultManager.DNFed(race, Pilot);
                    SetResult(position, dnf, behind, behindWho);
                }
                else if (newDetection)
                {
                    if (showPosition)
                    {
                        if (!GeneralSettings.Instance.AlwaysShowPosition)
                        {
                            recentPositionNode.SetTextAlpha(position.ToStringPosition());
                            recentPositionNode.Normal = 0;
                        }

                        if (behind != TimeSpan.Zero)
                        {
                            behindTime.SetTextAlpha("+" + behind.ToStringRaceTime());
                        }
                    }
                }
                Position = position;

                if (GeneralSettings.Instance.AlwaysShowPosition)
                {
                    if (race.Started && !hasFinished)
                    {
                        recentPositionNode.Text = position.ToStringPosition();
                    }
                    else
                    {
                        recentPositionNode.Text = "";
                    }
                    recentPositionNode.Normal = 1;
                }
            }
            else
            {
                Position = EventManager.GetMaxPilotsPerRace();
            }

            if (!hasFinished)
            {
                ClearResult();
            }

            if (oldPosition != Position)
            {
                if (holeshot)
                {
                    if (GeneralSettings.Instance.ReOrderAtHoleshot)
                    {
                        RequestReorder?.Invoke();
                    }
                    else
                    {
                        // Treat it as if we didn't change position at all
                        Position = oldPosition;
                    }
                }
                else
                {
                    RequestReorder?.Invoke();
                }
            }
        }

        public override string ToString()
        {
            if (Pilot != null)
            {
                return Pilot.Name + " (P " + Position + ", PB" + PBTime.TotalSeconds + ", F " + Channel.Frequency;
            }

            return base.ToString();
        }

        public override bool OnDrop(MouseInputEvent finalInputEvent, Node node)
        {
            if (EventManager.RaceManager.CanRunRace)
            {
                ChannelPilotNameNode otherPilotNameNode = node as ChannelPilotNameNode;
                if (otherPilotNameNode != null && otherPilotNameNode != pilotNameNode)
                {
                    Pilot other = otherPilotNameNode.Pilot;
                    EventManager.RaceManager.SwapPilots(other, Channel, EventManager.RaceManager.CurrentRace);
                    return true;
                }
            }

            return base.OnDrop(finalInputEvent, node);
        }

        public void SetPlaybackTime(DateTime time)
        {
            playbackTime = time;

            Race current = EventManager.RaceManager.CurrentRace;
            if (current != null && current.Type.HasPoints())
            {
                Lap l = current.GetLastValidLap(Pilot);
                if (l != null && Pilot.HasFinished(EventManager))
                {
                    resultBackground.Visible = l.Detection.Time < playbackTime.Value && l.Number >= current.TargetLaps;
                }
                else
                {
                    resultBackground.Visible = false;
                }
            }
            else
            {
                resultBackground.Visible = false;
            }

            LapsNode.SetPlaybackTime(time);
        }
    }

    public class ChannelPilotNameNode : ColorNode, IPilot
    {
        public Pilot Pilot { get; set; }
        public Channel Channel { get { return ChannelNodeBase.Channel; } }

        private TextNode pilotName;
        public ChannelNodeBase ChannelNodeBase { get; private set; }

        public ChannelPilotNameNode(ChannelNodeBase channelNodeBase, Color channelColor, float pilotAlpha)
            :base(Theme.Current.PilotViewTheme.PilotNameBackground)
        {
            this.ChannelNodeBase = channelNodeBase;
            KeepAspectRatio = false;
            Alpha = pilotAlpha;
            Tint = channelColor;

            pilotName = new TextNode("", Theme.Current.PilotViewTheme.PilotOverlayText.XNA);
            pilotName.RelativeBounds = new RectangleF(0, 0.15f, 0.87f, 0.7f);
            pilotName.Alpha = 1 / Alpha;
            pilotName.Style.Border = true;
            AddChild(pilotName);
        }

        public void SetPilot(Pilot p)
        {
            Pilot = p;

            if (Pilot == null)
            {
                pilotName.Text = "";
            }
            else
            {
                pilotName.Text = p.Name;
            }
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (mouseInputEvent.Button == MouseButtons.Left && mouseInputEvent.ButtonState == ButtonStates.Pressed && ChannelNodeBase.EventManager.RaceManager.CanRunRace)
            {
                GetLayer<DragLayer>()?.RegisterDrag(this, mouseInputEvent);
            }

            return base.OnMouseInput(mouseInputEvent);
        }

    }
}