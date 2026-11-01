using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Tools;

namespace RaceLib
{
    public enum MultiPCRoles
    {
        RaceControl,
        Review
    }

    // Multi-PC review: other copies of FPVTrackside, on PCs sharing the event directory (e.g. over a
    // network share), review races while one PC runs the event. Each PC writes a small status file
    // into the event directory saying which race it has open and which races it has saved. The
    // others read it to
    //  - lock out the race open on another PC, so two PCs don't edit the same race at once, and
    //  - reload the races another PC has saved, rather than carrying on with a stale copy.
    // Every race save is also checked against the revision on disk, so a stale copy never
    // overwrites a newer one.
    public class MultiPCManager : IDatabaseWriteGuard, IDisposable
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        // A PC whose status hasn't changed for this long has crashed or lost the network, so its lock is ignored.
        public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

        // A status file this old when first seen is from a PC that's long gone. Allows for the clocks of the PCs differing a little.
        private static readonly TimeSpan staleStatusAge = TimeSpan.FromMinutes(2);

        private static readonly TimeSpan messageRepeatTime = TimeSpan.FromSeconds(30);

        public EventManager EventManager { get; private set; }

        public Guid EventId { get; private set; }

        public bool Active { get; private set; }

        public MultiPCRoles Role { get; private set; }

        public bool IsReviewPC { get { return Active && Role == MultiPCRoles.Review; } }

        public string MachineName { get; private set; }

        public event Action<string> OnMessage;

        // Another PC opened, closed or changed the race it has open.
        public event Action OnPeersChanged;

        private DirectoryInfo directory;
        private FileInfo statusFile;
        private MultiPCStatus status;

        private Race openRace;

        private Dictionary<string, Peer> peers;

        // Races whose save was refused, to reload from disk so the change that wasn't saved doesn't linger in memory.
        private HashSet<Guid> toReload;
        private object reloadLock;

        private Dictionary<string, DateTime> recentMessages;

        private Thread thread;
        private AutoResetEvent wake;
        private bool running;

        public MultiPCManager(EventManager eventManager)
        {
            EventManager = eventManager;
            MachineName = Environment.MachineName;

            peers = new Dictionary<string, Peer>();
            toReload = new HashSet<Guid>();
            reloadLock = new object();
            recentMessages = new Dictionary<string, DateTime>();
            wake = new AutoResetEvent(false);
        }

        public void Start(DirectoryInfo eventDirectory, MultiPCRoles role)
        {
            EventId = EventManager.EventId;
            Role = role;

            directory = new DirectoryInfo(Path.Combine(eventDirectory.FullName, "MultiPC"));
            directory.Create();

            statusFile = new FileInfo(Path.Combine(directory.FullName, ToFileName(MachineName) + ".json"));

            status = new MultiPCStatus();
            status.MachineName = MachineName;
            status.Role = role;

            // Carry over the races saved before a restart, so the other PCs still pick them up.
            MultiPCStatus previous = ReadStatus(statusFile);
            if (previous != null)
            {
                status.Heartbeat = previous.Heartbeat;
                if (previous.SavedRaces != null)
                {
                    status.SavedRaces = previous.SavedRaces;
                }
            }

            Active = true;
            DatabaseFactory.WriteGuard = this;

            Logger.RaceLog.LogCall(this, "Multi-PC review started", MachineName, role, directory.FullName);

            // Find out what the other PCs have open before this one opens anything.
            try
            {
                Update();
            }
            catch (Exception e)
            {
                Logger.RaceLog.LogException(this, e);
            }

            running = true;
            thread = new Thread(Run);
            thread.Name = "Multi-PC review";
            thread.IsBackground = true;
            thread.Start();
        }

        public void Dispose()
        {
            if (!Active)
                return;

            if (DatabaseFactory.WriteGuard == this)
            {
                DatabaseFactory.WriteGuard = null;
            }

            running = false;
            wake.Set();

            // Not forever, in case the network share has gone away.
            thread?.Join(TimeSpan.FromSeconds(5));

            Active = false;
        }

        private void Run()
        {
            while (running)
            {
                wake.WaitOne(PollInterval);
                if (!running)
                    break;

                try
                {
                    Update();
                }
                catch (Exception e)
                {
                    Logger.RaceLog.LogException(this, e);
                }

                // Lets a burst of changes go out as one update.
                Thread.Sleep(250);
            }

            // Release the open race. The status file stays, so the races saved here are still picked up.
            openRace = null;
            try
            {
                WriteStatus();
            }
            catch (Exception e)
            {
                Logger.RaceLog.LogException(this, e);
            }
        }

        private void Update()
        {
            WriteStatus();
            ReadPeers();
            ReloadChangedRaces();
        }

        public void SetOpenRace(Race race)
        {
            if (openRace == race)
                return;

            openRace = race;

            // Let the others know straight away, so they can't open it too.
            if (Active)
            {
                wake.Set();
            }
        }

        // The PC that has the race open, if it's not this one.
        public string GetOpenElsewhere(Race race)
        {
            if (race == null)
                return null;

            return GetOpenElsewhere(race.ID);
        }

        public string GetOpenElsewhere(Guid raceId)
        {
            if (!Active || raceId == Guid.Empty)
                return null;

            lock (peers)
            {
                Peer peer = peers.Values.FirstOrDefault(p => p.Alive && p.Status.OpenRace == raceId);
                return peer?.Status.MachineName;
            }
        }

        public bool CanOpen(Race race)
        {
            if (!Active)
                return true;

            string openOn = GetOpenElsewhere(race);
            if (openOn != null)
            {
                Message(race.RaceName + " is open on " + openOn + ". It can be opened here once it's closed there.");
                return false;
            }

            // Opening a race locks it, so a review PC sticks to races that have been run and never holds up the next one.
            if (Role == MultiPCRoles.Review && !race.Ended)
            {
                Message(race.RaceName + " hasn't been run yet. This PC opened the event to review races, so it can only open races that have been run.");
                return false;
            }

            // Open it as it was last saved, even if that was moments ago on another PC.
            try
            {
                int savedRevision;
                string savedBy;
                bool changed;
                using (IDatabase db = DatabaseFactory.Open(EventId))
                {
                    changed = race.ID != Guid.Empty && db.GetSavedRaceRevision(race.ID, out savedRevision, out savedBy) && (savedRevision != race.Revision || savedBy != race.SavedBy);
                }

                if (changed)
                {
                    ReloadRace(race.ID, false);
                }
            }
            catch (Exception e)
            {
                Logger.RaceLog.LogException(this, e);
            }
            return true;
        }

        // A review PC reviews races that have been run. It doesn't add races or change who's in them.
        public bool CanChangeRaces()
        {
            if (!IsReviewPC)
                return true;

            Message("This PC opened the event to review races, so it can't add pilots to races.");
            return false;
        }

        // Final Results pins are saved with the event, which belongs to the PC running it.
        public bool CanChangeFinalResults()
        {
            if (!IsReviewPC)
                return true;

            Message("This PC opened the event to review races, so pilots can only be pinned in Final Results on the PC running the event.");
            return false;
        }

        public IEnumerable<MultiPCStatus> GetPeers()
        {
            lock (peers)
            {
                return peers.Values.Where(p => p.Alive).Select(p => p.Status).ToArray();
            }
        }

        public bool Guards(Type type)
        {
            return type == typeof(Race) || type == typeof(Result) || type == typeof(Event);
        }

        public bool Write(IDatabase database, BaseObject obj, Func<bool> write)
        {
            Race race = obj as Race;
            if (race != null)
            {
                return WriteRace(database, race, write);
            }

            // Results go with their race, so only the PC with the race open saves them.
            Result result = obj as Result;
            if (result != null && GetOpenElsewhere(result.Race) != null)
            {
                return false;
            }

            // The event belongs to the PC running it. A review PC's copy would put back anything
            // changed there since it loaded, e.g. pilots added to the event.
            if (obj is Event && Role == MultiPCRoles.Review)
            {
                Logger.RaceLog.LogCall(this, "Event not saved on a review PC");
                return false;
            }

            return write();
        }

        private bool WriteRace(IDatabase database, Race race, Func<bool> write)
        {
            string openOn = GetOpenElsewhere(race);
            if (openOn != null)
            {
                Message(race.RaceName + " is open on " + openOn + ", so this change to it wasn't saved.");
                Reload(race.ID);
                return false;
            }

            bool conflict = false;
            bool written = false;
            int savedRevision = 0;
            string savedBy = null;

            // Saves of the same race on this PC (e.g. a lap from the timing system while editing) must check and bump the revision one at a time.
            lock (race)
            {
                if (race.ID != Guid.Empty && database.GetSavedRaceRevision(race.ID, out savedRevision, out savedBy) && savedRevision != race.Revision)
                {
                    conflict = true;
                }
                else
                {
                    int revision = race.Revision;
                    string previousSavedBy = race.SavedBy;

                    race.Revision = revision + 1;
                    race.SavedBy = MachineName;

                    written = write();
                    if (!written)
                    {
                        race.Revision = revision;
                        race.SavedBy = previousSavedBy;
                    }
                }
            }

            if (conflict)
            {
                // Another PC has saved the race since this one loaded it. Writing would throw their changes away.
                Logger.RaceLog.LogCall(this, "Save refused, revision on disk", race, race.Revision, savedRevision, savedBy);
                if (savedBy != MachineName)
                {
                    Message(race.RaceName + " was changed on " + savedBy + ", so this change to it wasn't saved. It's been reloaded with their changes.");
                }
                Reload(race.ID);
                return false;
            }

            if (written)
            {
                lock (status)
                {
                    status.SavedRaces[race.ID] = race.Revision;
                }
            }
            return written;
        }

        private void Reload(Guid raceId)
        {
            lock (toReload)
            {
                toReload.Add(raceId);
            }
            wake.Set();
        }

        private void WriteStatus()
        {
            string json;
            lock (status)
            {
                status.Heartbeat++;

                Race race = openRace;
                status.OpenRace = race != null ? race.ID : Guid.Empty;

                json = JsonConvert.SerializeObject(status, Formatting.Indented);
            }

            // Write then rename, so the other PCs never read half a file. Windows refuses the rename while anything has
            // the file open, which another PC reading it or a virus scanner does for a moment, so it's tried again.
            string temp = Path.ChangeExtension(statusFile.FullName, ".tmp");
            File.WriteAllText(temp, json);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, statusFile.FullName, true);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void ReadPeers()
        {
            DateTime now = DateTime.Now;
            bool changed = false;

            foreach (FileInfo file in directory.GetFiles("*.json"))
            {
                if (string.Equals(file.Name, statusFile.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                MultiPCStatus peerStatus = ReadStatus(file);
                if (peerStatus == null || string.IsNullOrEmpty(peerStatus.MachineName))
                    continue;

                lock (peers)
                {
                    Peer peer;
                    if (!peers.TryGetValue(peerStatus.MachineName, out peer))
                    {
                        peer = new Peer();
                        peer.Status = peerStatus;

                        bool stale = DateTime.UtcNow - file.LastWriteTimeUtc > staleStatusAge;
                        peer.LastChanged = stale ? DateTime.MinValue : now;

                        peers.Add(peerStatus.MachineName, peer);
                    }
                    else
                    {
                        if (peer.Status.Heartbeat != peerStatus.Heartbeat)
                        {
                            peer.LastChanged = now;
                        }

                        if (peer.Status.OpenRace != peerStatus.OpenRace || peer.Status.Role != peerStatus.Role)
                        {
                            changed = true;
                        }

                        peer.Status = peerStatus;
                    }

                    // Closed PCs keep their status file, they just stop updating it.
                    bool alive = peer.Alive;
                    if (alive != peer.WasAlive)
                    {
                        peer.WasAlive = alive;
                        changed = true;

                        Logger.RaceLog.LogCall(this, alive ? "PC connected" : "PC disconnected", peerStatus.MachineName, peerStatus.Role);
                    }
                }
            }

            if (Role == MultiPCRoles.RaceControl)
            {
                Peer[] raceControls;
                lock (peers)
                {
                    raceControls = peers.Values.Where(p => p.Alive && p.Status.Role == MultiPCRoles.RaceControl && !p.WarnedRaceControl).ToArray();
                }

                foreach (Peer peer in raceControls)
                {
                    peer.WarnedRaceControl = Message(peer.Status.MachineName + " also has this event open to run it. Only one PC should run the event, open it with Review on the others.");
                }
            }

            if (changed)
            {
                OnPeersChanged?.Invoke();
            }
        }

        private void ReloadChangedRaces()
        {
            Guid openRaceId = openRace != null ? openRace.ID : Guid.Empty;

            // Races saved on other PCs since we last looked.
            List<SavedRace> saves = new List<SavedRace>();
            lock (peers)
            {
                foreach (Peer peer in peers.Values)
                {
                    if (peer.Status.SavedRaces == null)
                        continue;

                    foreach (KeyValuePair<Guid, int> saved in peer.Status.SavedRaces)
                    {
                        int handled;
                        if (peer.Handled.TryGetValue(saved.Key, out handled) && handled == saved.Value)
                            continue;

                        // Leave a race until the other PC has finished with it, unless this PC has it open too.
                        if (peer.Alive && peer.Status.OpenRace == saved.Key && saved.Key != openRaceId)
                            continue;

                        saves.Add(new SavedRace() { Peer = peer, RaceId = saved.Key, Revision = saved.Value });
                    }
                }
            }

            foreach (SavedRace saved in saves)
            {
                try
                {
                    Race race = EventManager.RaceManager.GetRaceByRaceId(saved.RaceId);

                    // Unless it already has that save, or a later one.
                    string savedBy = saved.Peer.Status.MachineName;
                    if (race == null || race.Revision < saved.Revision || (race.Revision == saved.Revision && race.SavedBy != savedBy))
                    {
                        ReloadRace(saved.RaceId, false);
                    }

                    lock (peers)
                    {
                        saved.Peer.Handled[saved.RaceId] = saved.Revision;
                    }
                }
                catch (Exception e)
                {
                    // Try again next time.
                    Logger.RaceLog.LogException(this, e);
                }
            }

            Guid[] refused;
            lock (toReload)
            {
                refused = toReload.Where(id => GetOpenElsewhere(id) == null).ToArray();
                toReload.ExceptWith(refused);
            }

            foreach (Guid raceId in refused)
            {
                try
                {
                    ReloadRace(raceId, true);
                }
                catch (Exception e)
                {
                    // Try again next time.
                    Logger.RaceLog.LogException(this, e);
                    lock (toReload)
                    {
                        toReload.Add(raceId);
                    }
                }
            }
        }

        private void ReloadRace(Guid raceId, bool force)
        {
            // From the background thread, and when opening a race.
            lock (reloadLock)
            {
                Race saved;
                using (IDatabase db = DatabaseFactory.Open(EventId))
                {
                    saved = db.GetObject<Race>(raceId);
                }

                if (saved == null)
                    return;

                Race race = EventManager.RaceManager.GetRaceByRaceId(raceId);
                if (race != null && !force && race.Revision == saved.Revision && race.SavedBy == saved.SavedBy)
                    return;

                Pilot[] pilots = race != null ? race.Pilots : new Pilot[0];

                race = EventManager.RaceManager.ApplyReloadedRace(saved);

                // The PC running the event has the last word on results, and generates rounds from them.
                bool saveResults = Role == MultiPCRoles.RaceControl && GetOpenElsewhere(race) == null;
                EventManager.ResultManager.RecalculateResults(race, saveResults);

                foreach (Pilot pilot in pilots.Union(race.Pilots).Where(p => p != null).Distinct())
                {
                    EventManager.LapRecordManager.ClearPilot(pilot);
                    EventManager.LapRecordManager.UpdatePilot(pilot);
                    EventManager.SpeedRecordManager.UpdatePilot(pilot);
                }

                Logger.RaceLog.LogCall(this, "Reloaded race", race, race.Revision, race.SavedBy);

                if (race == openRace && race.SavedBy != MachineName)
                {
                    Message(race.RaceName + " was changed on " + race.SavedBy + " and has been reloaded.");
                }
            }
        }

        // Returns whether it's been shown, now or recently. Not before anything's listening, e.g. while the event loads.
        private bool Message(string message)
        {
            Action<string> onMessage = OnMessage;
            if (onMessage == null)
            {
                Logger.RaceLog.Log(this, message);
                return false;
            }

            // The same thing can be tried over and over, e.g. by the auto runner.
            lock (recentMessages)
            {
                DateTime last;
                if (recentMessages.TryGetValue(message, out last) && DateTime.Now - last < messageRepeatTime)
                    return true;

                recentMessages[message] = DateTime.Now;
            }

            Logger.RaceLog.Log(this, message);
            onMessage(message);
            return true;
        }

        private static MultiPCStatus ReadStatus(FileInfo file)
        {
            try
            {
                // Share delete too, so the owner can replace the file while it's being read.
                using (FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                {
                    return JsonConvert.DeserializeObject<MultiPCStatus>(reader.ReadToEnd());
                }
            }
            catch (Exception)
            {
                // Not there, or being replaced. Try again next time.
                return null;
            }
        }

        private static string ToFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private class Peer
        {
            public MultiPCStatus Status { get; set; }

            // When its status last changed, by this PC's clock.
            public DateTime LastChanged { get; set; }

            public bool WasAlive { get; set; }

            public bool WarnedRaceControl { get; set; }

            public bool Alive { get { return DateTime.Now - LastChanged < PeerTimeout; } }

            // The saved revision of each race already acted on.
            public Dictionary<Guid, int> Handled { get; private set; }

            public Peer()
            {
                Handled = new Dictionary<Guid, int>();
            }
        }

        private struct SavedRace
        {
            public Peer Peer;
            public Guid RaceId;
            public int Revision;
        }
    }

    public class MultiPCStatus
    {
        public string MachineName { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public MultiPCRoles Role { get; set; }

        // Goes up with every write, so the others can tell this PC is still there.
        public long Heartbeat { get; set; }

        public Guid OpenRace { get; set; }

        // The latest revision this PC saved of each race.
        public Dictionary<Guid, int> SavedRaces { get; set; }

        public MultiPCStatus()
        {
            SavedRaces = new Dictionary<Guid, int>();
        }
    }
}
