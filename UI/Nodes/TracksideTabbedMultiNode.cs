using Composition;
using Composition.Input;
using Composition.Nodes;
using ExternalData;
using Microsoft.Xna.Framework;
using RaceLib;
using Sound;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Timing;
using Tools;
using UI.Nodes.Rounds;
using UI.Video;

namespace UI.Nodes
{
    public class TracksideTabbedMultiNode : TabbedMultiNode
    {
        public bool IsOnLive { get { return sceneManagerNode == Showing; } }
        public bool IsOnRounds { get { return rounds == Showing; } }
        public bool IsOnReplay { get { return ReplayNode == Showing; } }
        public bool IsOnChanelList { get { return pilotChanelList == Showing; } }
        public bool IsOnLapCount { get { return LapCountSummaryNode == Showing; } }
        public bool IsOnLapRecords { get { return LapRecordsSummaryNode == Showing; } }
        public bool IsOnPoints { get { return PointsSummaryNode == Showing; } }
        public bool IsOnFinalResults { get { return FinalResultsNode == Showing; } }
        public bool IsOnPatreons { get { return patreonsNode == Showing; } }
        public bool IsOnPhotoBooth { get { return PhotoBooth == Showing; } }
        public bool IsOnRSSI { get { return rssiNode == Showing; } }

        private RoundsNode rounds;
        private SceneManagerNode sceneManagerNode;
        private PilotChanelList pilotChanelList;
        private PatreonsNode patreonsNode;
        public PhotoBoothNode PhotoBooth { get; private set; }

        public ReplayNode ReplayNode { get; private set; }

        public LapRecordsSummaryNode LapRecordsSummaryNode { get; private set; }
        public PointsSummaryNode PointsSummaryNode { get; private set; }
        public LapCountSummaryNode LapCountSummaryNode { get; private set; }
        public FinalResultsNode FinalResultsNode { get; private set; }

        private RSSIAnalyserNode rssiNode;

        private TextButtonNode replayButton;
        private TextButtonNode liveButton;

        protected EventManager eventManager;
        private VideoManager VideoManager;
        private KeyboardShortcuts keyMapper;

        private TextButtonNode rssiButton;
        public TextButtonNode PhotoBoothButton { get; private set; }

        // The race ShowReplay is opening, so the race change doesn't show it as well.
        private Race openingReplay;


        public TracksideTabbedMultiNode(EventManager eventManager, VideoManager videoManager, SoundManager soundManager, RoundsNode rounds, SceneManagerNode sceneManagerContent, TabButtonsNode tabButtons, KeyboardShortcuts keyMapper)
            : base(TimeSpan.FromSeconds(0.6f), tabButtons)
        {
            this.eventManager = eventManager;
            this.keyMapper = keyMapper;
            VideoManager = videoManager;

            this.rounds = rounds;
            sceneManagerNode = sceneManagerContent;
            rssiNode = new RSSIAnalyserNode(eventManager);
            patreonsNode = new PatreonsNode();
            PointsSummaryNode = new PointsSummaryNode(eventManager);
            LapCountSummaryNode = new LapCountSummaryNode(eventManager);
            LapRecordsSummaryNode = new LapRecordsSummaryNode(eventManager);
            FinalResultsNode = new FinalResultsNode(eventManager);
            pilotChanelList = new PilotChanelList(eventManager);
            PhotoBooth = new PhotoBoothNode(videoManager, eventManager, soundManager);

            ReplayNode = new ReplayNode(eventManager, keyMapper);

            eventManager.RaceManager.OnRaceChanged += UpdateReplayButton;
            eventManager.RaceManager.OnRaceEnd += UpdateReplayButton;
            eventManager.RaceManager.TimingSystemManager.OnInitialise += UpdateRSSIVisible;
            videoManager.OnFinishedFinalizing += VideoManager_OnFinishedFinalizing;
            eventManager.OnJumpToReplay += ShowReplay;
            eventManager.OnJumpToReplaySecondWindow += ShowReplaySecondWindow;
        }

        public override void Dispose()
        {
            eventManager.RaceManager.OnRaceChanged -= UpdateReplayButton;
            eventManager.RaceManager.OnRaceEnd -= UpdateReplayButton;
            eventManager.RaceManager.TimingSystemManager.OnInitialise -= UpdateRSSIVisible;
            eventManager.OnJumpToReplay -= ShowReplay;
            eventManager.OnJumpToReplaySecondWindow -= ShowReplaySecondWindow;

            base.Dispose();
        }

        private void VideoManager_OnFinishedFinalizing()
        {
            Race race = eventManager.RaceManager.CurrentRace;
            if (race != null)
            {
                UpdateReplayButton(race);   
            }
        }

        public virtual void Init(PlatformTools platformTools)
        {
            AddTab("Rounds", rounds, ShowRounds);
            liveButton = AddTab("Live", sceneManagerNode, ShowLive);
            replayButton = AddTab("Replay", ReplayNode, (mie) => { ShowReplay(); });

            AddTab("Lap Records", LapRecordsSummaryNode, ShowTopLaps);
            AddTab("Lap Count", LapCountSummaryNode, ShowLaps);
            AddTab("Points", PointsSummaryNode, ShowPoints);
            AddTab("Final Results", FinalResultsNode, ShowFinalResults);
            TextButtonNode channelListButton = AddTab("Channel List", pilotChanelList, ShowPilotChannelList);
            rssiButton = AddTab("RSSI Analyser", rssiNode, ShowAnalyser);
            PhotoBoothButton = AddTab("Photo Booth", PhotoBooth, ShowPhotoBooth);
            TextButtonNode patreonsButton = AddTab("Patreons", patreonsNode, ShowPatreons);

            replayButton.Enabled = false;
            PhotoBoothButton.Visible = platformTools.HasFeature(PlatformFeature.Video);

            UpdateRSSIVisible();

            // Multi-PC review: a review PC just reviews races. Races open as replays rather than live.
            if (IsReviewPC)
            {
                liveButton.Visible = false;
                channelListButton.Visible = false;
                PhotoBoothButton.Visible = false;
                patreonsButton.Visible = false;

                ShowRounds();
            }
            else
            {
                ShowPatreons();
            }
        }

        private bool IsReviewPC { get { return eventManager.MultiPC.IsReviewPC; } }

        // Multi-PC review: shows the open race's replay where the PC running the event would show it live.
        private void ShowReview()
        {
            Race race = eventManager.RaceManager.CurrentRace;

            // ShowReplay is opening it, and shows it itself.
            if (race != null && race == openingReplay)
                return;

            // A review PC only opens races that have been run.
            if (race != null && race.Ended)
            {
                ShowReplay(race);
            }
            else
            {
                ShowRounds();
            }
        }


        // Multi-PC review: the analyser starts and stops detection, which on a shared timer (RotorHazard, a racetimer gate)
        // would start or stop the race the PC running the event is timing.
        private void UpdateRSSIVisible()
        {
            rssiButton.Visible = eventManager.RaceManager.TimingSystemManager.HasSpectrumAnalyser && !IsReviewPC;
        }

        private void UpdateReplayButton(Race race)
        {
            if (race != null && race.Ended)
            {
                replayButton.Enabled = VideoManager.HasReplay(race) && !VideoManager.Finalising;
            }
            else
            {
                replayButton.Enabled = false;
            }

            if (race == null)
            {
                if (Showing == ReplayNode)
                {
                    ShowLive();
                }
            }
        }

        public void ShowAnalyser(MouseInputEvent mie)
        {
            Show(rssiNode);

            if (!eventManager.RaceManager.RaceRunning)
            {
                var groups = eventManager.Channels.GetChannelGroups();
                var frequencies = groups.Select(c => c.FirstOrDefault());

                Timing.ListeningFrequency[] lastFrequencies = eventManager.RaceManager.TimingSystemManager.LastListeningFrequencies;
                if (lastFrequencies.Length == 0)
                {
                    lastFrequencies = frequencies.Select(r => new Timing.ListeningFrequency(r.Band.ToString(), r.Number, r.Frequency, 1, Color.Red)).ToArray();
                }

                eventManager.RaceManager.TimingSystemManager.SetListeningFrequencies(lastFrequencies);

                DateTime now = DateTime.Now;
                eventManager.RaceManager.TimingSystemManager.StartDetection(ref now);
            }
        }

        public void ShowPatreons(MouseInputEvent mie)
        {
            ShowPatreons();
        }

        public void ShowPatreons()
        {
            Show(patreonsNode);
            patreonsNode.Refresh();
        }

        public void ShowTopLaps(MouseInputEvent mie)
        {
            //if (mie != null && mie.Button == MouseButtons.Middle)
            //{
            //    BaseGame baseGame = CompositorLayer.Game as BaseGame;
            //    baseGame.QuickLaunchWindow<LapRecordsSummaryNode>(eventManager, keyMapper);
            //    return;
            //}

            if (LapRecordsSummaryNode.Visible)
            {
                LapRecordsSummaryNode.Refresh();
            }
            Show(LapRecordsSummaryNode);
            LapRecordsSummaryNode.Scale(0.9f);
        }

        public void ShowPoints(MouseInputEvent mie)
        {
            //if (mie != null && mie.Button == MouseButtons.Middle)
            //{
            //    BaseGame baseGame = CompositorLayer.Game as BaseGame;
            //    baseGame.QuickLaunchWindow<PointsSummaryNode>(eventManager, keyMapper);
            //    return;
            //}

            PointsSummaryNode.OrderByLast();
            PointsSummaryNode.Refresh();
            Show(PointsSummaryNode);
        }

        public void ShowLaps(MouseInputEvent mie)
        {
            //if (mie != null && mie.Button == MouseButtons.Middle)
            //{
            //    BaseGame baseGame = CompositorLayer.Game as BaseGame;
            //    baseGame.QuickLaunchWindow<LapCountSummaryNode>(eventManager, keyMapper);
            //    return;
            //}

            LapCountSummaryNode.OrderByLast();
            LapCountSummaryNode.Refresh();
            Show(LapCountSummaryNode);
        }

        public void ShowFinalResults(MouseInputEvent mie)
        {
            FinalResultsNode.Refresh();
            Show(FinalResultsNode);
        }

        public void ShowRounds(MouseInputEvent mie)
        {
            // A review PC doesn't add rounds.
            if (!IsReviewPC)
            {
                eventManager.RoundManager.CheckThereIsOneRound();
            }
            //if (mie != null && mie.Button == MouseButtons.Middle)
            //{
            //    BaseGame baseGame = CompositorLayer.Game as BaseGame;
            //    baseGame.QuickLaunchWindow<RoundsNode>(eventManager, keyMapper);
            //    return;
            //}

            ShowRounds();
        }

        public void ShowRounds()
        {
            Race race = eventManager.RaceManager.CurrentRace;
            if (race != null)
            {
                rounds.ScrollToRace(race);
            }

            Show(rounds);
        }
        public void ShowPhotoBooth(MouseInputEvent mie)
        {
            // Hidden on a review PC, but taking a pilot's photo from the pilot list would still show it.
            if (IsReviewPC)
                return;

            Show(PhotoBooth);
            PhotoBooth.Load();
        }

        public void ShowPilotChannelList(MouseInputEvent mie)
        {
            //if (mie != null && mie.Button == MouseButtons.Middle)
            //{
            //    BaseGame baseGame = CompositorLayer.Game as BaseGame;
            //    baseGame.QuickLaunchWindow<PilotChanelList>(eventManager, keyMapper);
            //    return;
            //}

            Show(pilotChanelList);
        }

        public void ShowCommentators()
        {
            if (IsReviewPC)
            {
                ShowReview();
                return;
            }

            Show(sceneManagerNode);
            sceneManagerNode.ShowCommentators();
        }

        public void ShowLive(SceneManagerNode.Scenes scene)
        {
            if (IsReviewPC)
            {
                ShowReview();
                return;
            }

            sceneManagerNode.SetScene(scene);
            Show(sceneManagerNode);
        }

        private void ShowLive(MouseInputEvent mie)
        {
            if (mie != null && mie.Button == MouseButtons.Right)
            {
                MouseMenu sceneMenu = new MouseMenu(this);
                foreach (SceneManagerNode.Scenes scene in Enum.GetValues(typeof(SceneManagerNode.Scenes)))
                {
                    sceneMenu.AddItem(scene.ToString().CamelCaseToHuman(), () =>
                    {
                        ShowLive(scene);
                    });
                }

                sceneMenu.Show(liveButton.Bounds.X, liveButton.Bounds.Bottom);
            }
            else
            {
                ShowLive();
            }
        }

        public void ShowLive()
        {
            // e.g. when a race is opened.
            if (IsReviewPC)
            {
                ShowReview();
                return;
            }

            if (!eventManager.RaceManager.HasPilots)
            {
                sceneManagerNode.SetScene(SceneManagerNode.Scenes.PreRace, true);
            }
            else if (eventManager.RaceManager.RaceRunning)
            {
                sceneManagerNode.SetScene(SceneManagerNode.Scenes.Race, true);
            }
            else if (eventManager.RaceManager.RaceFinished)
            {
                sceneManagerNode.SetScene(SceneManagerNode.Scenes.RaceResults, true);
            }
            else
            {
                sceneManagerNode.SetScene(SceneManagerNode.Scenes.PreRace, true);
            }

            if (eventManager.RaceManager.CurrentRace == null)
            {
                Race race = eventManager.RaceManager.LastFinishedRace();
                if (race != null) 
                { 
                    eventManager.RaceManager.SetRace(race);
                }
            }

            Show(sceneManagerNode);
        }

        public void ShowReplay(Race race, Lap lap = null)
        {
            if (!eventManager.RaceManager.RaceRunning)
            {
                if (eventManager.RaceManager.CurrentRace != race)
                {
                    // Not if it's locked out (open on another PC). Laps added in the replay go to the current race.
                    openingReplay = race;
                    bool opened = eventManager.RaceManager.SetRace(race);
                    openingReplay = null;

                    if (!opened)
                        return;
                }

                Show(ReplayNode);
                ReplayNode.ReplayRace(race, lap);
            }
        }

        public void ShowReplay()
        {
            Race current = eventManager.RaceManager.CurrentRace;
            ShowReplay(current);
        }

        private void ShowReplaySecondWindow(Race race, Lap lap = null)
        {
            BaseGame baseGame = CompositorLayer.Game as BaseGame;
            if (baseGame == null)
                return;

            var replayNode = new ReplayNode(eventManager, keyMapper);
            replayNode.ReplayRace(race, lap);
            baseGame.ShowNewWindow(replayNode);
        }

        public override void Show(Node node)
        {
            if (Showing == rssiNode)
            {
                TimingSystemManager tsm = eventManager.RaceManager.TimingSystemManager;

                // if we're detecting and not in a race
                if (!eventManager.RaceManager.RaceRunning && tsm.IsDetecting)
                {
                    tsm.EndDetection(EndDetectionType.Abort);
                }
            }

            if (Showing == PhotoBooth)
            {
                PhotoBooth.Clean();
                eventManager.ProfilePictures.FindProfilePictures(eventManager.Event.Pilots.ToArray());
                sceneManagerNode.ChannelsGridNode.ReloadPilotProfileImages();
            }

            base.Show(node);
            ReplayNode.CleanUp();
            GC.Collect();
        }
    }
}
