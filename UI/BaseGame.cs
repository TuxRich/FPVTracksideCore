using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RaceLib;
using Sound;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Timing;
using Timing.ImmersionRC;
using Tools;
using UI;
using UI.Nodes;
using UI.Nodes.Rounds;
using UI.Video;
using Webb;

namespace UI
{
    public class BaseGame : LayerStackGameBackgroundThread
    {
        protected virtual string DefaultTheme => "FPVTrackside";

        protected EventLayer eventLayer;

        protected LoadingLayer loadingLayer;

        private Mutex mutex;
        private bool mutexAcquired;

        private bool hasEverShownEventSelector;

        // Multi-PC review: the event was opened to review races, so restarts reopen it the same way.
        private bool openedForReview;

        public Texture2D Banner { get; protected set; }

        public WorkQueue Background { get; private set; }

        private AlreadyRunningLayer alreadyRunning;

        public DirectoryInfo Log { get; private set; }
        public DirectoryInfo Data { get; private set; }
        public DirectoryInfo Themes { get; private set; }
        public DirectoryInfo Video { get; private set; }
        public DirectoryInfo Pilots { get; private set; }
        public DirectoryInfo Patreons { get; private set; }
        public DirectoryInfo HTTPFiles { get; private set; }

        public Tools.Profile Profile { get; private set; }

        public BaseGame(PlatformTools platformTools)
            :base(platformTools)
        {
            IOTools.WorkingDirectory = platformTools.WorkingDirectory;

            InactiveSleepTime = new TimeSpan(0);

            Background = new WorkQueue("Game Background");

            hasEverShownEventSelector = false;
            mutex = new Mutex(false, "FPVTrackside - uewepuep");

            Log = CreateDirectory(platformTools.WorkingDirectory, "log");
            Data = CreateDirectory(platformTools.WorkingDirectory, "data");
            Themes = CreateDirectory(platformTools.WorkingDirectory, "themes");
            Video = CreateDirectory(platformTools.WorkingDirectory, "video");
            Pilots = CreateDirectory(platformTools.WorkingDirectory, "pilots");
            Patreons = CreateDirectory(platformTools.WorkingDirectory, "patreons");
            HTTPFiles = CreateDirectory(platformTools.WorkingDirectory, "httpfiles");

            Content.RootDirectory = "Content";
            IsFixedTimeStep = false;
            Window.Title = Assembly.GetEntryAssembly().GetName().Name + " - " + AppVersion.Text;

            Logger.Init(Log);


            FileInfo[] oldConfigs = Data.GetFiles("*.xml").Where(r => !r.Name.EndsWith("GeneralSettings.xml")).ToArray();
            if (oldConfigs.Any())
            {
                if (Data.GetDirectories().Any(d => d.Name == "default"))
                {
                    foreach (FileInfo xml in oldConfigs) 
                    { 
                        string newName = Path.Combine(Data.FullName, "default",xml.Name);

                        if (!File.Exists(newName))
                        {
                            xml.MoveTo(newName);
                        }
                        else
                        {
                            xml.Delete();
                        }
                    }
                }
            }
        }

        private DirectoryInfo CreateDirectory(DirectoryInfo working, string directory)
        {
            string fullPath = Path.Combine(working.FullName, directory);
            DirectoryInfo di = new DirectoryInfo(fullPath);
            if (!di.Exists)
            {
                di.Create();
            }
            return di;
        }


        protected override void Dispose(bool disposing)
        {
            Logger.UI.Log(this, this.ToString(), "Dispose");

            Background.Dispose();

            try
            {
                base.Dispose(disposing);
            }
            catch (Exception ex)
            {
                // MonoGame bug: Effect.Dispose can throw NullReferenceException during GraphicsDevice cleanup
                Logger.UI.LogException(this, ex);
            }

            Tools.Logger.CleanUp();

            if (mutexAcquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                mutex.Dispose();
            }
        }

        protected virtual void InitializeProfileSettings(Profile profile)
        {
            ApplicationProfileSettings.Initialize(profile);
        }

        // Why the profile loaded isn't the one chosen, shown once the UI is up.
        private string profileMessage;

        // A profile's event folder may be a network drive that isn't there, e.g. a review profile pointing at the race
        // PC's share. LoadContent would then fail opening the database on it, and the app would close with an error.
        private void CheckEventStorage()
        {
            Func<Profile, DirectoryInfo> storage = p => (p.Name == Profile.Name ? ApplicationProfileSettings.Instance : ApplicationProfileSettings.Peek(p)).EventStorageDirectory;
            Func<DirectoryInfo, string> check = d => IOTools.IsAccessible(d, TimeSpan.FromSeconds(10), out string problem) ? null : problem;

            Profile accessible = ChooseAccessibleProfile(Profile, Profile.GetProfiles(PlatformTools.WorkingDirectory), storage, check, out profileMessage);
            if (!ReferenceEquals(accessible, Profile))
            {
                Profile = accessible;
                InitializeProfileSettings(Profile);
            }
            else if (profileMessage != null)
            {
                // Only for this run: the setting itself isn't changed.
                ApplicationProfileSettings.Instance.EventStorageLocationRelative = "events";
            }
        }

        // The profile to load: the one chosen if its event folder can be used, or else the default profile or any
        // other whose folder can be, with a message saying so. With none, the one chosen, for the caller to point at
        // the local events folder. check gives why a folder can't be used, or null.
        public static Profile ChooseAccessibleProfile(Profile chosen, IEnumerable<Profile> profiles, Func<Profile, DirectoryInfo> storage, Func<DirectoryInfo, string> check, out string message)
        {
            message = null;

            DirectoryInfo chosenFolder = storage(chosen);
            string problem = check(chosenFolder);
            if (problem == null)
                return chosen;

            Logger.UI.Log(typeof(BaseGame), "Profile " + chosen.Name + "'s event folder " + chosenFolder.FullName + " can't be used: " + problem);
            string reason = "The event folder of the profile '" + chosen.Name + "' can't be used:\n" + chosenFolder.FullName + "\n" + problem;

            IEnumerable<Profile> others = profiles.Where(p => p.Name != chosen.Name)
                                                  .OrderBy(p => p.Name == GeneralSettings.DefaultProfileName ? 0 : 1)
                                                  .ThenBy(p => p.Name);
            foreach (Profile other in others)
            {
                if (check(storage(other)) == null)
                {
                    message = reason + "\n\nThe profile '" + other.Name + "' has been loaded instead.";
                    return other;
                }
            }

            message = reason + "\n\nNo other profile's can be either, so for now its events are in the local events folder.";
            return chosen;
        }

        protected override void Initialize()
        {
            GeneralSettings.Initialise();

            Profile = new Profile(PlatformTools.WorkingDirectory, GeneralSettings.Instance.Profile);
            InitializeProfileSettings(Profile);
            CheckEventStorage();

            // ShownDecimalPlaces is [NeedsRestart], so a single startup-time copy
            // into SpeechParameters is enough for every TTS call site.
            SpeechParameters.DecimalPlaces = ApplicationProfileSettings.Instance.ShownDecimalPlaces;

            if (!ApplicationProfileSettings.Instance.UseDirectX9)
            {
                GraphicsDeviceManager.GraphicsProfile = GraphicsProfile.HiDef;
            }
            GraphicsDeviceManager.SynchronizeWithVerticalRetrace = ApplicationProfileSettings.Instance.VSync;
            GraphicsDeviceManager.PreferMultiSampling = ApplicationProfileSettings.Instance.AntiAliasing;
            GraphicsDeviceManager.ApplyChanges();

            Window.Position = new Point(Math.Max(Window.Position.X, 0), Math.Max(Window.Position.Y, 0));

            base.Initialize();
        }

        protected override void LoadContent()
        {
            Logger.UI.Log(this, this.Window.Title, "LoadContent");

            base.LoadContent();

            float inversScale = ApplicationProfileSettings.Instance.InverseResolutionScalePercent / 100.0f;
            if (inversScale > 0)
            {
                LayerStack.Scale = 1 / inversScale;
            }

            ClearColor = Theme.Current.Background.XNA;

            Banner = TextureHelper.GetEmbeddedTexture(GraphicsDevice, System.Reflection.Assembly.GetAssembly(typeof(BaseGame)), @"UI.img.banner.png");

            BackgroundLayer backgroundLayer = new BackgroundLayer(GraphicsDevice, Theme.Current.Background);
            backgroundLayer.BackgroundNode.Alignment = RectangleAlignment.TopLeft;
            LayerStack.Add(backgroundLayer);

            loadingLayer = new LoadingLayer(GraphicsDevice);
            loadingLayer.BlockOnLoading = true;

            PopupLayer popupLayer = new PopupLayer(GraphicsDevice, Theme.Current.Editor.Background.XNA, Theme.Current.Editor.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
            Popuper.PopupProvider = popupLayer;

            MenuLayer menuLayer = new MenuLayer(GraphicsDevice, Theme.Current.MenuBackground.XNA, Theme.Current.Hover.XNA, Theme.Current.MenuText.XNA, Theme.Current.MenuTextInactive.XNA, Theme.Current.ScrollBar.XNA);
            DragLayer dragLayer = new DragLayer(GraphicsDevice);

            LayerStack.Add(popupLayer);
            LayerStack.Add(menuLayer);
            LayerStack.Add(dragLayer);
            LayerStack.Add(loadingLayer);

#if DEBUG
            LayerStack.Add(new TestLayer(GraphicsDevice, popupLayer));
#endif
            bool waitingOnMutex;
            try
            {
                mutexAcquired = mutex.WaitOne(TimeSpan.Zero);
                waitingOnMutex = !mutexAcquired;
            }
            catch
            {
                waitingOnMutex = true;
            }

            if (waitingOnMutex)
            {
                alreadyRunning = new AlreadyRunningLayer(GraphicsDevice, mutex);
                LayerStack.Add(alreadyRunning);
            }

            int frameRate = Math.Min(1000, Math.Max(1, ApplicationProfileSettings.Instance.FrameRateLimit));
            TargetElapsedTime = TimeSpan.FromSeconds(1f / frameRate);
            IsFixedTimeStep = true;

            loadingLayer.WorkQueue.Enqueue("Database Upgrade", DatabaseUpgrade);

            loadingLayer.WorkQueue.Enqueue("Load Translations", LoadTranslations);
            loadingLayer.WorkQueue.Enqueue("Startup", Startup);
        }

        private void DatabaseUpgrade()
        {
            using (IDatabase db = DatabaseFactory.Open(Guid.Empty))
            {
                Logger.AllLog.LogCall(this, db.Version);
            }
        }

        public virtual void Startup()
        {
            if (ApplicationProfileSettings.Instance.ShowWelcomeScreen && !hasEverShownEventSelector)
            {
                ShowWelcomeSetup();
            }
            else
            {
                ShowEventSelector();
            }

            if (profileMessage != null)
            {
                Popuper.PopupMessage(profileMessage);
                profileMessage = null;
            }
        }

        public void LoadTranslations()
        {
            Translator[] translators = TranslatorFactory.Load().ToArray();

            // Translation sweep: FPVTRACKSIDE_TRANSLATION_SWEEP=1 records every key the app asks for that the file is missing
            Translator english = translators.FirstOrDefault(t => t.Language == "English");
            if (english != null && Environment.GetEnvironmentVariable("FPVTRACKSIDE_TRANSLATION_SWEEP") == "1")
            {
                Translator.RecordMissing(english.ItemNames, Path.Combine(IOTools.WorkingDirectory.FullName, "TranslationsMissing.tsv"));
            }

            Translator t = translators.FirstOrDefault(t => t.Language == ApplicationProfileSettings.Instance.Language);
            if (t != null)
            {
                // Don't load the english translator, it's already english.
                if (t.Language == "English")
                {
                    Translator.ClearPrimary();
                }
                else
                {
                    t.MakePrimary();
                }
            }
        }


        public virtual void ShowEventSelector()
        {
            EventSelectorLayer eventSelector = LayerStack.GetLayer<EventSelectorLayer>();
            if (eventSelector != null)
            {
                eventSelector.Dispose();
            }

            Logger.UI.LogCall(this);
            hasEverShownEventSelector = true;
            eventSelector = new EventSelectorLayer(GraphicsDevice, CreateEventSelectorEditor());

            LayerStack.AddAbove<BackgroundLayer>(eventSelector);
            eventSelector.OnOK += EventSelected;
        }

        protected virtual EventSelectorEditor CreateEventSelectorEditor()
        {
            return new EventSelectorEditor(Banner, Profile);
        }

        public virtual void ShowWelcomeSetup()
        {
            Logger.UI.LogCall(this);

            Profile profile = new Profile(PlatformTools.WorkingDirectory, GeneralSettings.Instance.Profile);

            CompositorLayer welcomeLayer = new CompositorLayer(GraphicsDevice);
            WelcomeSetupNode welcomeSetupNode = new WelcomeSetupNode(Banner, profile);
            welcomeSetupNode.OnOK += () =>
            {
                if (welcomeLayer != null)
                {
                    LayerStack.Remove(welcomeLayer);
                    welcomeLayer.Dispose();
                    welcomeLayer = null;
                }
                ShowEventSelector();
            };
            welcomeSetupNode.Restart += Restart;
            welcomeLayer.Root.AddChild(welcomeSetupNode);

            LayerStack.AddAbove<BackgroundLayer>(welcomeLayer);
        }

        public virtual void EventSelected(BaseObjectEditorNode<SimpleEvent> ed)
        {
            EventSelectorEditor editor = ed as EventSelectorEditor;

            Logger.UI.LogCall(this, editor.Selected);

            SimpleEvent selected = editor.Selected;

            if (selected == null)
                selected = editor.Objects.FirstOrDefault();

            if (selected == null)
                return;

            StartEvent(selected.ID, editor.Profile, editor.OpenForReview);
        }

        public void Restart(Event evvent)
        {
            Restart();

            if (evvent != null)
            {
                PlatformTools.Invoke(() =>
                {
                    if (loadingLayer != null)
                    {
                        loadingLayer.WorkQueue.Enqueue("Setting Event", () =>
                        {
                            EventSelectorLayer eventSelectorLayer = LayerStack.GetLayer<EventSelectorLayer>();
                            if (eventSelectorLayer != null)
                            {
                                eventSelectorLayer.Dispose();
                                StartEvent(evvent.ID, eventSelectorLayer.Editor.Profile, openedForReview);
                            }
                        });
                    }
                });
            }
        }

        protected virtual EventManager CreateEventMananger(Profile profile)
        {
            return new VideoEventManager(profile); ;
        }


        private void StartEvent(Guid eventId, Profile profile, bool review)
        {
            openedForReview = review;

            loadingLayer.BlockOnLoading = true;

            BackgroundLayer backgroundLayer = LayerStack.GetLayer<BackgroundLayer>();
            EventManager eventManager = CreateEventMananger(profile);

            WorkSet startEventWorkSet = new WorkSet();
            startEventWorkSet.OnError += ErrorLoadingEvent;

            eventManager.LoadEvent(startEventWorkSet, loadingLayer.WorkQueue, eventId);

            // Load races BEFORE sync. Sync systems need to assume this..
            eventManager.LoadRaces(startEventWorkSet, loadingLayer.WorkQueue);

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Starting Multi-PC Review", () =>
            {
                if (eventManager.Event.MultiPCReview)
                {
                    DirectoryInfo eventDirectory = new DirectoryInfo(Path.Combine(ApplicationProfileSettings.Instance.EventStorageDirectory.FullName, eventId.ToString()));
                    eventManager.MultiPC.Start(eventDirectory, review ? MultiPCRoles.Review : MultiPCRoles.RaceControl);
                }
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "On Start Event", () =>
            {
                OnStartEvent(eventManager, eventManager.Event);
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Loading Settings", () =>
            {
                // Re-init the following settings so settings windows can reload event to reload settings.
                InitializeProfileSettings(Profile);
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Loading Theme", () =>
            {
                Theme.Initialise(GraphicsDevice, PlatformTools.WorkingDirectory, DefaultTheme);

                if (backgroundLayer != null)
                {
                    backgroundLayer.SetBackground(Theme.Current.Background);
                }
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Initializing UI", () =>
            {
                CreateEventLayer(eventManager);
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Initializing Video Inputs", () =>
            {
                try
                {
                    eventLayer?.LoadVideo();
                }
                catch (Exception ex)
                {
                    Logger.VideoLog.LogException(this, ex);
                    Popuper.PopupMessage("Error loading Video: " + ex.Message);
                }
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Cleaning up old video files", () =>
            {
                // A review PC shares the events directory with the PC running the event, and mustn't delete its recordings.
                if (eventManager.MultiPC.IsReviewPC)
                    return;

                try
                {
                    eventLayer?.VideoCheckFileCount();
                }
                catch (Exception ex)
                {
                    Logger.VideoLog.LogException(this, ex);
                    Popuper.PopupMessage("Error Cleaning old videos: " + ex.Message);
                }
            });

            loadingLayer.WorkQueue.Enqueue(startEventWorkSet, "Setting Scene", () =>
            {
                if (eventLayer != null)
                {
                    eventLayer.FinalSetup();

                    while (eventLayer.Root.Alpha < 1)
                    {
                        Thread.Sleep(10);
                        eventLayer.Root.Alpha += 0.05f;
                    }

                    eventLayer.Root.Alpha = 1;
                }
            });
        }

        protected virtual void CreateEventLayer(EventManager eventManager)
        {
            eventLayer = new EventLayer(this, GraphicsDevice, eventManager, PlatformTools);

            eventLayer.Root.Alpha = 0;
            LayerStack.AddAbove<BackgroundLayer>(eventLayer);

            TestLayer testLayer = LayerStack.GetLayer<TestLayer>();
            if (testLayer != null)
            {
                testLayer.EventLayer = eventLayer;
            }
        }

        protected virtual void OnStartEvent(EventManager eventManager, Event selected)
        {

        }

        public void ErrorLoadingEvent(WorkItem arg1, Exception arg2)
        {
            if (eventLayer != null)
            {
                eventLayer.Dispose();
                eventLayer = null;
            }

            Logger.UI.LogException(this, arg2);

            Popuper.PopupMessage("Error loading event. Please check log files.", () =>
            {
                if (LayerStack.Game is UI.BaseGame)
                {
                    ((UI.BaseGame)LayerStack.Game).Restart(null);
                }
            });
        }

        protected override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (LayerStack != null && LayerStack.InputEventFactory != null)
            {
                TimeSpan sinceMouseMove = DateTime.Now - LayerStack.InputEventFactory.LastMouseUpdateTime;
                IsMouseVisible = sinceMouseMove.TotalSeconds < 5;
            }
        }

        public void ShowNewWindow(Node node)
        {
            PlatformTools.ShowNewWindow(node);
        }

        public void QuickLaunchWindow<T>(EventManager eventManager, KeyboardShortcuts keyMapper)
        {
            if (typeof(T) == typeof(LapRecordsSummaryNode))
            {
                var n = new LapRecordsSummaryNode(eventManager);
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(PointsSummaryNode))
            {
                var n = new PointsSummaryNode(eventManager);
                n.Refresh();
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(LapCountSummaryNode))
            {
                var n = new LapCountSummaryNode(eventManager);
                n.Refresh();
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(PilotChanelList))
            {
                var n = new PilotChanelList(eventManager);
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(RoundsNode))
            {
                var n = new RoundsNode(eventManager);
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(ReplayNode))
            {
                var n = new ReplayNode(eventManager, keyMapper);
                n.ReplayRace(eventManager.RaceManager.CurrentRace);
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(LogNode))
            {
                var n = new LogNode(Logger.AllLog);
                ShowNewWindow(n);
            }

            if (typeof(T) == typeof(EventStatusNodeTopBar))
            {
                var n = new EventStatusNodeTopBar(eventManager);
                ShowNewWindow(n);
            }
        }
    }
}
