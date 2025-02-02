﻿using Composition;
using Composition.Input;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Tools;
using UI.Video;

namespace UI.Nodes
{
    public class ChannelsGridNode : GridNode, IUpdateableNode
    {
        public IEnumerable<Pilot> Pilots
        {
            get
            {
                return ChannelNodes.Select(lpn => lpn.Pilot);
            }
        }

        public IEnumerable<Channel> Channels
        {
            get
            {
                return ChannelNodes.Select(lpn => lpn.Channel);
            }
        }

        public IEnumerable<ChannelNodeBase> ChannelNodes
        {
            get
            {
                return Children.OfType<ChannelNodeBase>();
            }
        }

        public IEnumerable<CamNode> CamNodes
        {
            get
            {
                return Children.OfType<CamNode>();
            }
        }

        public event ChannelNodeBase.ChannelNodeDelegate OnChannelNodeClick;
        public event ChannelNodeBase.ChannelNodeDelegate OnChannelNodeCloseClick;

        private Size withLaps;
        private Size withOutLaps;

        public EventManager EventManager { get; private set; }
        public VideoManager VideoManager { get; private set; }

        public TimeSpan CurrentAnimationTime { get; private set; }

        private AutoCrashOut autoCrashOut;
        private VideoTimingManager videoTimingManager;

        private List<ChannelVideoInfo> channelInfos;

        private bool manualOverride;

        private GridStatsNode gridStatsNode;

        private LiveChatNode liveChatNode;

        private object channelCreationLock;

        public bool SingleRow { get; set; }

        public bool Replay { get; private set; }

        public bool ForceReOrder { get; private set; }
        public ReOrderTypes ReOrderType { get; private set; }
        private DateTime reOrderRequest;

        private bool extrasVisible;

        public enum ReOrderTypes
        {
            None, 
            ChannelOrder,
            PositionOrder
        }

        public override IEnumerable<GridTypes> AllowedGridTypes
        {
            get
            {
                if (GeneralSettings.Instance.ChannelGrid1)
                    yield return GridTypes.One;
                if (GeneralSettings.Instance.ChannelGrid2)
                    yield return GridTypes.Two;
                if (GeneralSettings.Instance.ChannelGrid3)
                    yield return GridTypes.Three;
                if (GeneralSettings.Instance.ChannelGrid4)
                    yield return GridTypes.Four;
                if (GeneralSettings.Instance.ChannelGrid6)
                    yield return GridTypes.Six;
                if (GeneralSettings.Instance.ChannelGrid8)
                    yield return GridTypes.Eight;
                if (GeneralSettings.Instance.ChannelGrid10)
                    yield return GridTypes.Ten;
                if (GeneralSettings.Instance.ChannelGrid12)
                    yield return GridTypes.Twelve;
                if (GeneralSettings.Instance.ChannelGrid12)
                    yield return GridTypes.Fifteen;
                if (GeneralSettings.Instance.ChannelGrid16)
                    yield return GridTypes.Sixteen;

                yield return GridTypes.SingleRow;
            }
        }

        public ChannelsGridNode(EventManager eventManager, VideoManager videoManager)
        {
            SingleRow = false;

            channelCreationLock = new object();
            channelInfos = new List<ChannelVideoInfo>();

            videoTimingManager = new VideoTimingManager(eventManager.RaceManager.TimingSystemManager, this);

            EventManager = eventManager;
            VideoManager = videoManager;

            ForceReOrder = false;

            withLaps = new Size(400, 300 + 24);
            withOutLaps = new Size(400, 300);
            SingleSize = withLaps;

            if (GeneralSettings.Instance.AlignChannelsTop)
            {
                Alignment = RectangleAlignment.TopCenter;
            }

            RequestLayout();

            EventManager.RaceManager.OnPilotAdded += AddPilotNR;
            EventManager.RaceManager.OnPilotRemoved += RemovePilot;
            EventManager.RaceManager.OnRaceClear += RaceManager_OnRaceClear;
            EventManager.RaceManager.OnRaceStart += RaceManager_OnRaceStart;
            EventManager.RaceManager.OnRaceEnd += RaceManager_OnRaceEnd;
            EventManager.RaceManager.OnRaceChanged += RaceManager_OnRaceChanged;
            EventManager.OnPilotRefresh += Refresh;

            gridStatsNode = new GridStatsNode(EventManager);
            gridStatsNode.Visible = false;
            AddChild(gridStatsNode);

            if (GeneralSettings.Instance.ShowLiveChatMidRace)
            {
                liveChatNode = new LiveChatNode();
                liveChatNode.Visible = false;
                AddChild(liveChatNode);
            }
        }

        public override void Dispose()
        {
            EventManager.RaceManager.OnPilotAdded -= AddPilotNR;
            EventManager.RaceManager.OnPilotRemoved -= RemovePilot;
            EventManager.RaceManager.OnRaceClear -= RaceManager_OnRaceClear;
            EventManager.RaceManager.OnRaceStart -= RaceManager_OnRaceStart;
            EventManager.RaceManager.OnRaceEnd -= RaceManager_OnRaceEnd;
            EventManager.RaceManager.OnRaceChanged -= RaceManager_OnRaceChanged;
            EventManager.OnPilotRefresh -= Refresh;

            videoTimingManager?.Dispose();
            autoCrashOut?.Dispose();
            base.Dispose();
        }


        private void RaceManager_OnRaceChanged(Race race)
        {
            manualOverride = false;
            ClearPilots();
            Reorder();
        }

        private void RaceManager_OnRaceEnd(Race race)
        {
            SetAssignedVisible(true);
            manualOverride = false;

            Reorder();
        }

        private void RaceManager_OnRaceStart(Race race)
        {
            bool isRace = EventManager.RaceManager.EventType != EventTypes.Freestyle;
            manualOverride = false;
            SetLapsVisiblity(isRace);
        }

        private void RaceManager_OnRaceClear(Race race)
        {
            ClearPilots();
        }
       
        public void Refresh()
        {
            manualOverride = false;
            ClearPilots();
            Reorder();

            Race r = EventManager.RaceManager.CurrentRace;
            if (r != null)
            {
                foreach (var pc in r.PilotChannelsSafe)
                {
                    AddPilotNR(pc);
                }
            }
        }

        protected override GridTypes DecideLayout(int count)
        {
            if (SingleRow)
            {
                gridStatsNode.Visible = false;
                if (liveChatNode != null)
                {
                    liveChatNode.Visible = false;
                }
                return GridTypes.SingleRow;
            }

            // remove one for gridstatsnode
            if (gridStatsNode.Visible)
            {
                count -= 1;
            }

            // Don't show the chat node if that's all there is..
            if (count == 1 && liveChatNode != null && liveChatNode.Visible)
            {
                count = 0;
                liveChatNode.Visible = false;
            }

            GridTypes decided = base.DecideLayout(count);

            if (GridTypeItemCount(decided) > count && !Replay)
            {
                gridStatsNode.Visible = true;
            }
            else
            {
                gridStatsNode.Visible = false;
            }

            return decided;
        }

        public void Reorder()
        {
            Reorder(false);
        }

        public void Reorder(bool forceReorder)
        {
            if (forceReorder)
            {
                ForceReOrder = forceReorder;
            }

            ForceUpdate = true;
            RequestLayout();
        }

        public override void UpdateVisibility(IEnumerable<Node> input)
        {
            if (reOrderRequest < DateTime.Now || ForceReOrder)
            {
                int crashed = input.OfType<ChannelNodeBase>().Count(c => c.CrashedOut && c.Pilot != null);
                int all = input.OfType<ChannelNodeBase>().Count(c => c.Pilot != null);
                bool raceFinished = EventManager.RaceManager.RaceFinished;

                foreach (ChannelNodeBase cbn in input.OfType<ChannelNodeBase>())
                {
                    if (cbn.Pilot != null && !Replay)
                    {
                        bool visible = !cbn.CrashedOut || raceFinished;
                        if (all == crashed)
                        {
                            visible = true;
                        }
                        cbn.SetAnimatedVisibility(visible);
                    }
                }

                foreach (CamNode camNode in CamNodes)
                {
                    camNode.SetAnimatedVisibility(camNode.VideoBounds.ShowInGrid && extrasVisible);
                }
            }

            base.UpdateVisibility(input);
        }

        public void SetReorderType(ReOrderTypes reOrderType)
        {
            ReOrderType = reOrderType;
            ForceReOrder = true;
            Reorder();
        }

        public override IEnumerable<Node> OrderedChildren(IEnumerable<Node> input)
        {
            ReOrderTypes reOrderType = ReOrderType;

            if (!EventManager.RaceManager.RaceType.HasResult())
            {
                reOrderType = ReOrderTypes.ChannelOrder;
            }

            if (GeneralSettings.Instance.ReOrderDelaySeconds != 0f && !ForceReOrder)
            {
                if (reOrderRequest == DateTime.MaxValue)
                {
                    reOrderRequest = DateTime.Now.AddSeconds(GeneralSettings.Instance.ReOrderDelaySeconds);
                    reOrderType = ReOrderTypes.None;
                }
                else if (reOrderRequest < DateTime.Now)
                {
                    reOrderRequest = DateTime.MaxValue;
                }
                else
                {
                    reOrderType = ReOrderTypes.None;
                }
            }

            IEnumerable<Node> output;
            switch (reOrderType)
            {
                case ReOrderTypes.None:
                default:
                    output = input;
                    break;
                case ReOrderTypes.ChannelOrder:
                    // Order by channel
                    output = input.OfType<ChannelNodeBase>().OrderBy(p => p.Channel.Frequency);
                    reOrderRequest = DateTime.MaxValue;
                    ForceReOrder = false;
                    break;
                case ReOrderTypes.PositionOrder:
                    // order by position
                    output = input.OfType<ChannelNodeBase>().OrderBy(p => p.Position).ThenBy(p => p.PBTime).ThenBy(p => p.Channel.Frequency);
                    reOrderRequest = DateTime.MaxValue;
                    ForceReOrder = false;
                    break;
            }


            // Add in the grid stats node
            if (gridStatsNode.Visible)
            {
                output = output.Union(new Node[] { gridStatsNode });
            }

            // And the live chat 
            if (liveChatNode != null && liveChatNode.Visible)
            {
                output = output.Union(new Node[] { liveChatNode });
            }

            // And the cam nodes
            if (CamNodes.Any(c => c.Visible))
            {
                output = output.Union(CamNodes.Where(c => c.Visible).OrderBy(c => c.FrameNode.Source.VideoConfig.DeviceName));
            }

            return output;
        }

        public void AddVideo(ChannelVideoInfo ci)
        {
            channelInfos.Add(ci);

            // Just create them now if we can. Helps get the video systems up and running.
            ChannelNodeBase cn = GetCreateChannelNode(ci.Channel);
            if (cn != null)
            {
                cn.Visible = false;
                cn.Snap();
            }
        }

        public void ClearVideo()
        {
            autoCrashOut?.Dispose();

            channelInfos.Clear();

            foreach (ChannelNodeBase n in ChannelNodes.ToArray())
            {
                n.Dispose();
            }

            foreach (CamNode n in CamNodes.ToArray())
            {
                n.Dispose();
            }
        }


        public void AddPilotNR(PilotChannel pilotChannel)
        {
            AddPilot(pilotChannel);
        }

        public ChannelNodeBase AddPilot(PilotChannel pilotChannel)
        {
            ChannelNodeBase channelNode = GetCreateChannelNode(pilotChannel.Channel);
            if (channelNode == null)
                return null;

            channelNode.SetPilot(pilotChannel.Pilot);
            channelNode.AnimationTime = CurrentAnimationTime;
            channelNode.SetAnimatedVisibility(true);
            channelNode.CrashedOutType = ChannelNodeBase.CrashOutType.None;

            bool isRace = EventManager.RaceManager.EventType != EventTypes.Freestyle;
            channelNode.SetLapsVisible(isRace);

            // Make them all update their position...
            ChannelNodeBase[] nodes = ChannelNodes.ToArray();
            foreach (ChannelNodeBase n in nodes)
            {
                n.UpdatePosition(null);
            }

            ForceUpdate = true;
            ForceReOrder = true;
            RequestLayout();
            return channelNode;
        }

        public void MakeExtrasVisible(bool visible)
        {
            extrasVisible = visible;
            foreach (CamNode camNode in CamNodes)
            {
                camNode.SetAnimatedVisibility(visible);
            }

            gridStatsNode.SetAnimatedVisibility(visible);

            if (liveChatNode != null)
            {
                liveChatNode.SetAnimatedVisibility(visible);
            }
        }

        public void SetBiggerChannelInfo(bool tall)
        {
            ChannelNodeBase[] nodes = ChannelNodes.ToArray();
            foreach (ChannelNodeBase n in nodes)
            {
                n.BiggerChannelInfo = tall;
            }
        }

        private ChannelNodeBase GetCreateChannelNode(Channel c)
        {
            lock (channelCreationLock)
            {
                ChannelNodeBase channelNodeBase = ChannelNodes.FirstOrDefault(cia => cia.Channel == c);
                if (channelNodeBase != null)
                {
                    return channelNodeBase;
                }

                ChannelVideoInfo ci = channelInfos.FirstOrDefault(cia => cia.Channel == c);
                Color color = EventManager.GetChannelColor(c);

                if (ci != null)
                {
                    ChannelVideoNode channelNode = new ChannelVideoNode(EventManager, c, ci.FrameSource, color);
                    channelNode.Init();
                    channelNode.FrameNode.RelativeSourceBounds = ci.ScaledRelativeSourceBounds;
                    channelNode.FrameNode.SetAspectRatio(withLaps);
                    autoCrashOut?.AddChannelNode(channelNode);

                    channelNodeBase = channelNode;
                }
                else
                {
                    channelNodeBase = new ChannelNodeBase(EventManager, c, color);
                    channelNodeBase.Init();
                }

                channelNodeBase.RelativeBounds = new RectangleF(0.45f, 0.45f, 0.1f, 0.1f);
                channelNodeBase.OnClick += (mie) =>
                {
                    OnChannelNodeClick?.Invoke(channelNodeBase);
                };

                channelNodeBase.OnCloseClick += () =>
                {
                    OnChannelNodeCloseClick?.Invoke(channelNodeBase);
                    Reorder();
                };

                channelNodeBase.OnCrashedOutClick += () =>
                {
                    Reorder();
                };

                channelNodeBase.OnFullscreen += () =>
                {
                    FullScreen(channelNodeBase);
                };

                channelNodeBase.OnShowAll += () =>
                {
                    IncreaseChannelVisiblity();
                };

                channelNodeBase.OnPBChange += Reorder;
                channelNodeBase.RequestReorder += Reorder;

                AddChild(channelNodeBase);

                channelNodeBase.RelativeBounds = new RectangleF(0.45f, 0.45f, 0.1f, 0.1f);
                channelNodeBase.Layout(Bounds);
                channelNodeBase.Snap();

                return channelNodeBase;
            }
        }

        public void RemovePilot(PilotChannel pc)
        {
            RemovePilot(pc.Pilot);
        }

        public void RemovePilot(Pilot p)
        {
            ChannelNodeBase channelNode = ChannelNodes.FirstOrDefault(lpn => lpn.Pilot == p);
            if (channelNode != null)
            {
                channelNode.SetPilot(null);
                channelNode.SetAnimatedVisibility(false);
                                
                ForceUpdate = true;
                ForceReOrder = true;
                RequestLayout();
            }
        }

        public void SetLapsVisiblity(bool visible)
        {
            if (visible)
            {
                SingleSize = withLaps;
            }
            else
            {
                SingleSize = withOutLaps;
            }

            foreach (ChannelNodeBase channelNode in ChannelNodes)
            {
                channelNode.SetLapsVisible(visible);
            }

            RequestLayout();
        }

        public void SetProfileVisible(bool visible, bool snap)
        {
            foreach (ChannelNodeBase channelNode in ChannelNodes)
            {
                channelNode.SetProfileVisible(visible, snap);
            }
        }

        public ChannelNodeBase GetChannelNode(Pilot p)
        {
            return ChannelNodes.FirstOrDefault(cn => cn.Pilot == p);
        }

        public void ClearPilots()
        {
            foreach (ChannelNodeBase cn in ChannelNodes)
            {
                cn.LapsNode.ClearLaps();
                cn.SetPilot(null);
                cn.SetAnimatedVisibility(false);
            }

            ForceUpdate = true;
            ForceReOrder = true;
            RequestLayout();
        }

        public void SetPilotVisible(Pilot p, bool visible)
        {
            if (EventManager.RaceManager.RaceRunning)
                manualOverride = true;

            ChannelNodeBase cn = GetChannelNode(p);
            if (cn != null)
            {
                cn.SetAnimatedVisibility(visible);
            }
        }

        public void TogglePilotVisible(Pilot p)
        {
            if (EventManager.RaceManager.RaceRunning)
                manualOverride = true;

            ChannelNodeBase cn = GetChannelNode(p);
            if (cn != null)
            {
                cn.SetAnimatedVisibility(!cn.Visible);
            }
        }

        public void SetAssignedVisible(bool visible)
        {
            foreach (ChannelNodeBase cn in ChannelNodes)
            {
                if (cn.Pilot != null)
                {
                    cn.SetAnimatedVisibility(visible);
                }
            }
        }

        public void SetUnassignedVisible(bool visible)
        {
            foreach (ChannelNodeBase cn in ChannelNodes)
            {
                if (cn.Pilot == null)
                {
                    cn.SetAnimatedVisibility(visible);
                }
            }
        }

        public void FullScreen(ChannelNodeBase fullScreen)
        {
            if (EventManager.RaceManager.RaceRunning || Replay)
            {
                IEnumerable<ChannelNodeBase> pilotNodes = ChannelNodes.Where(cn => cn.Pilot != null && cn != fullScreen);
                if (pilotNodes.Any())
                {
                    foreach (ChannelNodeBase cn in pilotNodes)
                    {
                        cn.SetAnimatedVisibility(false);
                        cn.CrashedOutType = ChannelNodeBase.CrashOutType.Manual;
                    }
                }

                fullScreen.SetAnimatedVisibility(true);
                fullScreen.CrashedOutType = ChannelNodeBase.CrashOutType.None;

                RequestLayout();
            }
        }

        public void IncreaseChannelVisiblity()
        {
            ForceReOrder = true;

            if (EventManager.RaceManager.RaceRunning)
                manualOverride = true;

            IEnumerable<ChannelNodeBase> pilotNodes = ChannelNodes.Where(cn => cn.Pilot != null);
            if (pilotNodes.Any())
            {
                foreach (ChannelNodeBase cn in pilotNodes)
                {
                    cn.SetAnimatedVisibility(true);
                    cn.CrashedOutType = ChannelNodeBase.CrashOutType.None;
                }

                RequestLayout();
            }
            else
            {
                AllVisible(true);
            }
        }

        public void DecreaseChannelVisiblity()
        {
            ForceReOrder = true;

            if (manualOverride)
            {
                manualOverride = false;
                return;
            }

            IEnumerable<ChannelNodeBase> emptyNodes = ChannelNodes.Where(cn => cn.Visible && cn.Pilot == null);
            if (emptyNodes.Any())
            {
                foreach (ChannelNodeBase cn in emptyNodes)
                {
                    cn.SetAnimatedVisibility(false);
                }

                RequestLayout();
            }
            else
            {
                AllVisible(false);
            }
        }

        public void AutomaticSetCrashed(ChannelNodeBase cn, bool crashed)
        {
            if (!manualOverride)
            {
                if (cn != null)
                {
                    if (!cn.Finished && cn.CrashedOutType != ChannelNodeBase.CrashOutType.Manual)
                    {
                        cn.CrashedOutType = crashed ? ChannelNodeBase.CrashOutType.Auto : ChannelNodeBase.CrashOutType.None;
                    }
                }
                Reorder();
            }
        }

        public void FillChannelNodes()
        {
            ClearVideo();
            autoCrashOut = new AutoCrashOut(EventManager, this);
       
            Channel[] channels = EventManager.Channels;

            try
            {
                foreach (ChannelVideoInfo channelVideoInfo in VideoManager.CreateChannelVideoInfos())
                {
                    if (channels.Contains(channelVideoInfo.Channel))
                    {
                        AddVideo(channelVideoInfo);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.UI.LogException(this, e);
            }

            foreach (VideoConfig vs in VideoManager.VideoConfigs)
            {
                foreach (VideoBounds videoBounds in vs.VideoBounds.Where(vb => vb.SourceType != SourceTypes.FPVFeed && vb.ShowInGrid))
                {
                    try
                    {
                        FrameSource source = VideoManager.GetFrameSource(vs);
                        if (source != null)
                        {
                            CamNode camNode = new CamNode(source, videoBounds);
                            AddChild(camNode);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.UI.LogException(this, e);
                    }
                }
            }
        }

        public void SetAnimationTime(TimeSpan timeSpan)
        {
            CurrentAnimationTime = timeSpan;

            foreach (AnimatedNode cn in Children.OfType<AnimatedNode>())
            {
                cn.AnimationTime = timeSpan;
            }

            foreach (AnimatedRelativeNode cn in Children.OfType<AnimatedRelativeNode>())
            {
                cn.AnimationTime = timeSpan;
            }

            if (gridStatsNode != null)
            {
                gridStatsNode.AnimationTime = CurrentAnimationTime;
            }

            if (liveChatNode != null)
            {
                liveChatNode.AnimationTime = CurrentAnimationTime;
            }
        }

        public void Update(GameTime gameTime)
        {
            if (reOrderRequest != DateTime.MinValue && DateTime.Now > reOrderRequest)
            {
                RequestLayout();
                ForceUpdate = true;
            }
        }

        public override bool OnDrop(MouseInputEvent finalInputEvent, Node node)
        {
            if (!base.OnDrop(finalInputEvent, node))
            {
                IPilot pl = node as IPilot;
                if (pl != null)
                {
                    if (EventManager.RaceManager.AddPilot(pl.Pilot))
                    {
                        return true;
                    }
                }
                return false;
            }

            return true;
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            Node[] t = Children;

            // Draw the crashed out channel nodes and the non-channel nodes.
            for (int i = t.Length - 1; i >= 0; i--)
            {
                if (t[i].Drawable)
                {
                    if (t[i] is ChannelNodeBase)
                    {
                        ChannelNodeBase c = (ChannelNodeBase)t[i];
                        if (c.CrashedOut)
                        {
                            c.Draw(id, parentAlpha * Alpha);
                        }
                    }
                    else
                    {
                        t[i].Draw(id, parentAlpha * Alpha);
                    }
                }
            }

            // Draw the active channel nodes
            for (int i = t.Length - 1; i >= 0; i--)
            {
                if (t[i].Drawable)
                {
                    if (t[i] is ChannelNodeBase)
                    {
                        ChannelNodeBase c = (ChannelNodeBase)t[i];
                        if (!c.CrashedOut)
                        {
                            c.Draw(id, parentAlpha * Alpha);
                        }
                    }
                }
            }

            NeedsDraw = false;
        }

        public void SetPlaybackTime(DateTime time)
        {
            Replay = true;
            foreach (ChannelNodeBase nodeBase in ChannelNodes)
            {
                nodeBase.SetPlaybackTime(time);
            }
        }
    }
}
