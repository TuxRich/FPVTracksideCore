﻿using Composition;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using Composition.Input;
using UI.Video;

namespace UI.Nodes
{
    public class SceneManagerNode : Node
    {
        private AnimatedRelativeNode launchCamsNode;
        private AnimatedRelativeNode commentatorsAndChat;

        private VideoManager videoManager;
        private EventManager eventManager;

        private ChannelsGridNode channelsGridNode;
        private TopBarNode topBarNode;

        private NamedRaceNode resultsRaceNode;
        private NamedRaceNode nextRaceNode;

        private WormNode wormNode;

        private LiveChatNode liveChat;

        private AnimatedNode eventStatusNodeContainer;

        public enum Scenes
        {
            Clear,
            PreRace,
            Race,
            PostRace,

            EventStatus,
            Commentators,
        }

        public Scenes Scene { get; private set; }

        public event Action<Scenes> OnSceneChange;
        public event Action OnVideoSettingsChange;

        public TimeSpan AfterRaceStart { get; private set; }

        public bool PreRaceScene { get { return GeneralSettings.Instance.PreRaceScene; } }
        public bool PostRaceScene { get { return GeneralSettings.Instance.PostRaceScene; } }

        public TimeSpan SetupAnimationTime { get { return TimeSpan.FromSeconds(0.2f); } }
        public TimeSpan MidRaceAnimationTime { get { return TimeSpan.FromSeconds(GeneralSettings.Instance.ReOrderAnimationSeconds); } }

        public SceneManagerNode(EventManager eventManager, VideoManager videoManager, ChannelsGridNode channelsGridNode, TopBarNode topBarNode)
        {
            AfterRaceStart = TimeSpan.FromSeconds(2);

            this.eventManager = eventManager;
            this.videoManager = videoManager;
            this.channelsGridNode = channelsGridNode;
            this.topBarNode = topBarNode;

            eventStatusNodeContainer = new AnimatedNode();
            eventStatusNodeContainer.Visible = false;
            eventStatusNodeContainer.RelativeBounds = new RectangleF(0, 0.05f, 1, 0.9f);
            AddChild(eventStatusNodeContainer);

            EventStatusNode eventStatusNode = new EventStatusNode(eventManager);
            eventStatusNodeContainer.AddChild(eventStatusNode);

            launchCamsNode = new AnimatedRelativeNode();
            launchCamsNode.SetAnimatedVisibility(false);
            
            commentatorsAndChat = new AnimatedRelativeNode();
            commentatorsAndChat.SetAnimatedVisibility(false);

            AddChild(launchCamsNode);
            AddChild(commentatorsAndChat);
            AddChild(channelsGridNode);

            resultsRaceNode = new NamedRaceNode("Results", eventManager);
            AddChild(resultsRaceNode);

            nextRaceNode = new NamedRaceNode("Next Race", eventManager);
            AddChild(nextRaceNode);

            liveChat = new LiveChatNode();
            liveChat.Visible = false;

            resultsRaceNode.RelativeBounds = new RectangleF(0, 0, 0.01f, 0.01f);
            nextRaceNode.RelativeBounds = new RectangleF(0, 0, 0.01f, 0.01f);

            wormNode = new WormNode(eventManager);
            wormNode.RelativeBounds = new RectangleF(0.0f, 1f, 1, 0.0f);
            AddChild(wormNode);

            eventManager.RaceManager.OnRaceStart += RaceManager_OnRaceStart;
            eventManager.RaceManager.OnRaceEnd += RaceManager_OnRaceEnd;
            eventManager.RaceManager.OnRaceClear += RaceManager_OnRaceClearReset;
            eventManager.RaceManager.OnRaceReset += RaceManager_OnRaceClearReset;
            eventManager.RaceManager.OnRaceChanged += SetRace;
            eventManager.RaceManager.OnLapsRecalculated += OnLapsRecalculated;
            eventManager.RaceManager.OnRacePreStart += RaceManager_OnRacePreStart;
            eventManager.RoundManager.OnRoundAdded += UpdateNextRaceNode;
            eventManager.OnPilotRefresh += EventManager_OnPilotRefresh;

            SetScene(Scenes.Clear);
        }

        private void EventManager_OnPilotRefresh()
        {
            UpdateNextRaceNode();
            resultsRaceNode.SetRace(eventManager.RaceManager.CurrentRace);
        }

        private void RaceManager_OnRacePreStart(Race race)
        {
            channelsGridNode.SetProfileVisible(false, false);
        }

        private void UpdateNextRaceNode()
        {
            Race nextRace = eventManager.RaceManager.GetNextRace(true);
            if (nextRace == null)
            {
                nextRaceNode.SetRace(null);
                if (eventManager.RaceManager.CurrentRace != null)
                {
                    nextRaceNode.ShowNextRoundOptions(eventManager.RaceManager.CurrentRace.Round);
                }
            }
            else
            {
                nextRaceNode.SetRace(nextRace);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            eventManager.RaceManager.OnRaceStart -= RaceManager_OnRaceStart;
            eventManager.RaceManager.OnRaceEnd -= RaceManager_OnRaceEnd;
            eventManager.RaceManager.OnRaceClear -= RaceManager_OnRaceClearReset;
            eventManager.RaceManager.OnRaceReset -= RaceManager_OnRaceClearReset;
            eventManager.RaceManager.OnRaceChanged -= SetRace;
            eventManager.RaceManager.OnLapsRecalculated -= OnLapsRecalculated;
            eventManager.RoundManager.OnRoundAdded -= UpdateNextRaceNode;
            eventManager.RaceManager.OnRacePreStart -= RaceManager_OnRacePreStart;
            eventManager.OnPilotRefresh -= EventManager_OnPilotRefresh;
        }

        public override bool OnDrop(MouseInputEvent finalInputEvent, Node node)
        {
            if (!base.OnDrop(finalInputEvent, node))
            {
                IPilot pilotNode = node as IPilot;
                if (pilotNode != null)
                {
                    eventManager.RaceManager.AddPilot(pilotNode.Pilot);
                    return true;
                }
                return false;
            }
            return true;
        }

        private void RaceManager_OnRaceStart(Race race)
        {
            eventManager.TimedActionManager.Enqueue(DateTime.Now + AfterRaceStart,
                () =>
                {
                    if (race.Running)
                    {
                        SetScene(Scenes.Race);
                    }
                });
        }

        private void RaceManager_OnRaceClearReset(Race race)
        {
            SetScene(Scenes.PreRace);
        }

        private void RaceManager_OnRaceEnd(Race race)
        {
            SetScene(Scenes.PostRace);
        }

        private void OnLapsRecalculated(Race race)
        {
            if (Scene == Scenes.PostRace && resultsRaceNode.Race == race)
            {
                // Update the results.
                resultsRaceNode.SetRace(race);
            }
        }

        public void SetRace(Race race)
        {
            if (race == null)
            {
                SetScene(Scenes.PreRace);
                return;
            }

            if (race.Ended)
            {
                SetScene(Scenes.PostRace);
            }
            else if (race.Running)
            {
                SetScene(Scenes.Race);
            }
            else
            {
                SetScene(Scenes.PreRace);
            }
        }

        public void SetScene(Scenes s, bool force = false)
        {
            if (Scene == s && !force)
                return;

            Scene = s;

            SetChannelGridReordering(s);

            // If those scenes are disabled, ignore that scene and do race.
            if (s == Scenes.PreRace && !PreRaceScene || s == Scenes.PostRace && !PostRaceScene)
            {
                SceneLayout(Scenes.Race);
            }
            else // Do the requested layout
            {
                SceneLayout(Scene);
            }

            // Need to make this visible again :)
            channelsGridNode.Visible = true;

            RequestLayout();

            OnSceneChange?.Invoke(s);
        }

        public void SetAnimationTime(TimeSpan time)
        {
            launchCamsNode.AnimationTime = time;
            commentatorsAndChat.AnimationTime = time;
            resultsRaceNode.AnimationTime = time;
            nextRaceNode.AnimationTime = time;
            channelsGridNode.SetAnimationTime(time);
            liveChat.AnimationTime = time;
            topBarNode.SetAnimationTime(time);
        }

        private void SetChannelGridReordering(Scenes scene)
        {
            bool hasPilots = eventManager.RaceManager.HasPilots;
            switch (scene)
            {
                case Scenes.PreRace:
                    if (GeneralSettings.Instance.PilotOrderPreRace == GeneralSettings.OrderTypes.PositionAndPB)
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.PositionOrder);
                    }
                    else
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.ChannelOrder);
                    }
                    break;
                case Scenes.Race:
                    if (GeneralSettings.Instance.PilotOrderMidRace == GeneralSettings.OrderTypes.PositionAndPB)
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.PositionOrder);
                    }
                    else
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.ChannelOrder);
                    }
                    break;
                case Scenes.PostRace:
                    if (GeneralSettings.Instance.PilotOrderPostRace == GeneralSettings.OrderTypes.PositionAndPB)
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.PositionOrder);
                    }
                    else
                    {
                        channelsGridNode.SetReorderType(ChannelsGridNode.ReOrderTypes.ChannelOrder);
                    }
                    break;
            }
        }

        private void SceneLayout(Scenes scene)
        {
            float channelGridHeight = 0.3f;
            float nonChannelGridHeight = 1 - channelGridHeight;
            switch (scene)
            {
                case Scenes.PreRace:
                    SetAnimationTime(SetupAnimationTime);
                    float launchWidth = 0.7f;

                    liveChat.SetAnimatedVisibility(GeneralSettings.Instance.ShowLiveChatPreRace);
                    liveChat.Refresh();

                    IEnumerable<Node> launchCams = launchCamsNode.VisibleChildren;
                    IEnumerable<Node> commentatorCams = commentatorsAndChat.VisibleChildren;

                    if (!launchCams.Any() && !commentatorCams.Any())
                    {
                        channelGridHeight = 1;
                        nonChannelGridHeight = 0;

                        channelsGridNode.SingleRow = false;
                    }
                    else
                    {
                        channelsGridNode.SingleRow = true;

                        if (!launchCams.Any())
                        {
                            launchWidth = 0;
                        }

                        if (!commentatorCams.Any())
                        {
                            launchWidth = 1;
                        }
                    }

                    channelsGridNode.SetBiggerChannelInfo(true);
                    channelsGridNode.SetProfileVisible(true, true);

                    launchCamsNode.SetAnimatedVisibility(true);
                    launchCamsNode.RelativeBounds = new RectangleF(0, 0, launchWidth, nonChannelGridHeight);

                    Node.AlignHorizontally(0, launchCams.ToArray());

                    commentatorsAndChat.SetAnimatedVisibility(true);
                    commentatorsAndChat.RelativeBounds = new RectangleF(launchWidth, 0, 1 - launchWidth, nonChannelGridHeight);

                    if (launchCams.Any())
                    {
                        Node.AlignVertically(0, commentatorsAndChat.VisibleChildren.ToArray());
                    }
                    else
                    {
                        Node.AlignHorizontally(0, commentatorsAndChat.VisibleChildren.ToArray());
                    }

                    channelsGridNode.RelativeBounds = new RectangleF(0, nonChannelGridHeight, 1, channelGridHeight);

                    resultsRaceNode.SetAnimatedVisibility(false);
                    nextRaceNode.SetAnimatedVisibility(false);

                    channelsGridNode.MakeExtrasVisible(false);
                    eventStatusNodeContainer.SetAnimatedVisibility(false);
                    break;

                case Scenes.Race:
                    channelsGridNode.SetProfileVisible(false, true);

                    SetAnimationTime(MidRaceAnimationTime);
                    liveChat.SetAnimatedVisibility(false);

                    channelsGridNode.SingleRow = false;
                    channelsGridNode.RelativeBounds = new RectangleF(0, 0.0f, 1, 1);

                    commentatorsAndChat.SetAnimatedVisibility(false);
                    launchCamsNode.SetAnimatedVisibility(false);

                    resultsRaceNode.SetAnimatedVisibility(false);
                    nextRaceNode.SetAnimatedVisibility(false);
                    channelsGridNode.SetBiggerChannelInfo(!eventManager.RaceManager.RaceRunning);
                    channelsGridNode.MakeExtrasVisible(true);
                    eventStatusNodeContainer.SetAnimatedVisibility(false);
                    break;

                case Scenes.Clear:
                    SetAnimationTime(SetupAnimationTime);

                    channelsGridNode.AllVisible(false);
                    commentatorsAndChat.SetAnimatedVisibility(false);
                    launchCamsNode.SetAnimatedVisibility(false);

                    resultsRaceNode.SetAnimatedVisibility(false);
                    nextRaceNode.SetAnimatedVisibility(false);

                    liveChat.SetAnimatedVisibility(false);

                    channelsGridNode.SetBiggerChannelInfo(false);
                    channelsGridNode.MakeExtrasVisible(false);
                    channelsGridNode.SetProfileVisible(false, true);
                    eventStatusNodeContainer.SetAnimatedVisibility(false);

                    break;

                case Scenes.PostRace:
                    SetAnimationTime(SetupAnimationTime);

                    channelsGridNode.SetProfileVisible(false, true);

                    liveChat.SetAnimatedVisibility(GeneralSettings.Instance.ShowLiveChatPostRace);
                    liveChat.Refresh();

                    launchCamsNode.SetAnimatedVisibility(false);

                    commentatorsAndChat.SetAnimatedVisibility(true);
                    commentatorsAndChat.RelativeBounds = new RectangleF(0.33f, channelGridHeight, 0.33f, nonChannelGridHeight);
                    Node.AlignVertically(0, commentatorsAndChat.Children);

                    channelsGridNode.SingleRow = true;
                    channelsGridNode.RelativeBounds = new RectangleF(0, 0.0f, 1, channelGridHeight);

                    resultsRaceNode.RelativeBounds = new RectangleF(0, channelGridHeight, 0.33f, nonChannelGridHeight);
                    resultsRaceNode.Scale(0.8f);
                    nextRaceNode.RelativeBounds = new RectangleF(0.66f, channelGridHeight, 0.33f, nonChannelGridHeight);
                    nextRaceNode.Scale(0.8f);

                    nextRaceNode.SetAnimatedVisibility(true);

                    if (eventManager.RaceManager.RaceFinished)
                    {
                        resultsRaceNode.SetAnimatedVisibility(true);
                        resultsRaceNode.SetRace(eventManager.RaceManager.CurrentRace);
                    }
                    else
                    {
                        resultsRaceNode.SetAnimatedVisibility(false);
                    }

                    UpdateNextRaceNode();


                    channelsGridNode.SetBiggerChannelInfo(true);
                    channelsGridNode.MakeExtrasVisible(false);
                    eventStatusNodeContainer.SetAnimatedVisibility(false);
                    break;
                case Scenes.Commentators:
                    liveChat.SetAnimatedVisibility(false);

                    launchCamsNode.SetAnimatedVisibility(false);

                    commentatorsAndChat.RelativeBounds = new RectangleF(0.0f, 0.0f, 1, 1);
                    commentatorsAndChat.SetAnimatedVisibility(true);
                    Node.AlignHorizontally(0, commentatorsAndChat.VisibleChildren.ToArray());

                    channelsGridNode.RelativeBounds = new RectangleF(0, 0.0f, 0, 0);

                    nextRaceNode.SetAnimatedVisibility(false);
                    resultsRaceNode.SetAnimatedVisibility(false);
                    eventStatusNodeContainer.SetAnimatedVisibility(false);
                    break;

                case Scenes.EventStatus:
                    liveChat.SetAnimatedVisibility(false);
                    launchCamsNode.SetAnimatedVisibility(false);
                    commentatorsAndChat.SetAnimatedVisibility(false);
                    channelsGridNode.RelativeBounds = new RectangleF(0, 0.0f, 0, 0);

                    nextRaceNode.SetAnimatedVisibility(false);
                    resultsRaceNode.SetAnimatedVisibility(false);
                    eventStatusNodeContainer.SetAnimatedVisibility(true);

                    break;
            }
        }

        public void Hide()
        {
            launchCamsNode.SetAnimatedVisibility(false);
            commentatorsAndChat.SetAnimatedVisibility(false);
            resultsRaceNode.SetAnimatedVisibility(false);
            nextRaceNode.SetAnimatedVisibility(false);

            channelsGridNode.Visible = false;
        }

        public void Show()
        {
            SetScene(Scene);
        }

        public void SetupCams()
        {
            if (liveChat != null)
            {
                liveChat.Remove();
            }

            launchCamsNode.ClearDisposeChildren();
            commentatorsAndChat.ClearDisposeChildren();

            foreach (FrameSource source in videoManager.GetFrameSources())
            {
                foreach (VideoBounds videoBounds in source.VideoConfig.VideoBounds)
                {
                    CamNode node = null;
                    switch (videoBounds.SourceType)
                    {
                        case SourceTypes.Commentators:
                            node = new CamNode(source, videoBounds);
                            node.OnFullScreenRequest += Node_OnFullScreenRequest;
                            commentatorsAndChat.AddChild(node);
                            break;

                        case SourceTypes.Launch:
                            node = new CamNode(source, videoBounds);
                            node.FrameNode.KeepAspectRatio = false;
                            node.FrameNode.CropToFit = true;
                            node.FrameNode.Alignment = RectangleAlignment.Center;
                            launchCamsNode.AddChild(node);
                            break;
                    }
                    if (node != null)
                    {
                        node.OnVideoBoundsChange += Node_OnVideoBoundsChange;
                    }
                }
            }

            if (liveChat != null)
            {
                commentatorsAndChat.AddChild(liveChat);
            }

        }

        private void Node_OnFullScreenRequest()
        {
            SetScene(Scenes.Commentators);
        }

        private void Node_OnVideoBoundsChange(VideoBounds obj)
        {
            videoManager.WriteCurrentDeviceConfig();
            OnVideoSettingsChange?.Invoke();
        }

        public void ToggleWorm()
        {
            if (wormNode.RelativeBounds.Height > 0)
            {
                wormNode.RelativeBounds = new RectangleF(0.0f, 1f, 1, 0.0f);
                channelsGridNode.RelativeBounds = new RectangleF(0, 0, 1, 1);
            }
            else
            { 
                // 0.2 and 6 are the numbers it was designed for...
                float height = 0.2f * (eventManager.RaceManager.PilotCount / 6.0f);

                wormNode.RelativeBounds = new RectangleF(0.0f, 1 - height, 1, height);
                channelsGridNode.RelativeBounds = new RectangleF(0, 0, 1, 1 - height);
            }
            RequestLayout();
        }
    }
}
