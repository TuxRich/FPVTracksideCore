using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Timing.RaceTimer
{
    // A racetimer gate, through its timer API (version 1, racetimer's docs/timer-api.md): a WebSocket at /api, one JSON
    // object a frame. Requests are matched to their replies by id, with the gate's detections, levels and status in
    // between. Every time from the gate is on its own clock, which is mapped onto this PC's.
    public class RaceTimerTimingSystem : ITimingSystemWithRSSI
    {
        private const int ApiVersion = 1;
        private const int MinMhz = 5345;
        private const int MaxMhz = 5945;
        private const int MaxNameLength = 5;
        private const int ClockSyncRounds = 10;
        private const int LevelsHz = 10;

        // The gate refuses a start_ms more than a minute from its clock.
        private const double StartWithinMs = 55000;

        // A sync this far from the last one: the gate's clock restarted, so it rebooted.
        private const double GateRestartMs = 1000;

        // The receivers restart their scan on a new channel plan, and detect nothing for about 0.6 s.
        private const double RescanMs = 600;

        private static readonly TimeSpan ConnectTimeOut = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReplyTimeOut = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FirstStatusTimeOut = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan RescanTimeOut = TimeSpan.FromSeconds(3);

        // The gate sends its status every second.
        private static readonly TimeSpan SilenceTimeOut = TimeSpan.FromSeconds(3);

        public TimingSystemType Type { get { return TimingSystemType.RaceTimer; } }

        private RaceTimerSettings settings;
        public TimingSystemSettings Settings { get { return settings; } set { settings = value as RaceTimerSettings; } }

        public string Name { get { return "RG"; } }

        public int MaxPilots { get { return maxChannels; } }

        public event DetectionEventDelegate OnDetectionEvent;
        public event MarshallEventDelegate OnMarshallEvent;

        private readonly object linkLock = new object();
        private Link link;
        private int linkCount;
        private volatile bool connected;
        private long nextId;

        // TimingSystemManager can still be connecting a timer it has replaced (new timing settings) and disposed.
        private volatile bool disposed;

        // From the gate's hello
        private string gateName;
        private int maxChannels;

        private volatile GateStatus status;
        private volatile GateLevels levels;
        private volatile GateConfig config;
        private string configText;

        // The clock map from the latest sync.
        private volatile ClockMap clock;

        // The channels FPVTrackside last asked for.
        private volatile ChannelPlan plan;

        // The race the gate is timing for us, under raceLock. raceOpen: the gate started a race for us and no stop has
        // got there since. raceLink: the connection the race started on, or carried on over.
        private readonly object raceLock = new object();
        private bool detecting;
        private bool raceOpen;
        private long raceNo;
        private int raceLink;
        private double raceBeganMs;
        private ClockMap raceClock;
        private long seenRace;
        private long seenSeq;
        private bool warnedStopped;

        public RaceTimerTimingSystem()
        {
            settings = new RaceTimerSettings();
            maxChannels = 8;
            seenRace = -1;
            seenSeq = -1;
        }

        public void Dispose()
        {
            disposed = true;
            Disconnect();
        }

        public bool Connected
        {
            get
            {
                Link l = link;
                if (!connected || l == null || l.Closed)
                    return false;

                if (l.SilentMs > SilenceTimeOut.TotalMilliseconds)
                {
                    Logger.TimingLog.Log(this, "Nothing from the gate for " + (l.SilentMs / 1000).ToString("0.0") + " s: reconnecting");
                    Disconnect();
                    return false;
                }

                return true;
            }
        }

        public bool Connect()
        {
            Disconnect();

            if (disposed || settings == null || string.IsNullOrWhiteSpace(settings.HostName))
                return false;

            Uri uri = null;
            Link newLink = new Link(Interlocked.Increment(ref linkCount));
            try
            {
                uri = settings.GetUri("ws", "/api");

                using (CancellationTokenSource cts = new CancellationTokenSource(ConnectTimeOut))
                {
                    newLink.Socket.ConnectAsync(uri, cts.Token).GetAwaiter().GetResult();
                }

                lock (linkLock)
                {
                    link = newLink;
                }

                lock (raceLock)
                {
                    newLink.CatchingUp = detecting;
                }

                newLink.Reader = new Thread(() => Read(newLink));
                newLink.Reader.Name = "racetimer gate";
                newLink.Reader.IsBackground = true;
                newLink.Reader.Start();

                // The gate speaks first.
                if (!newLink.Hello.Task.Wait(ReplyTimeOut) || newLink.Hello.Task.Result.ValueKind != JsonValueKind.Object)
                    return FailConnect(newLink, uri + " sent no hello");

                JsonElement hello = newLink.Hello.Task.Result;
                if (GetLong(hello, "api") != ApiVersion)
                    return FailConnect(newLink, uri + " isn't a racetimer gate speaking timer API " + ApiVersion + ": " + hello.GetRawText());

                gateName = GetString(hello, "name") ?? uri.Host;
                maxChannels = (int)GetLong(hello, "max_channels", 8);

                ClockMap fresh = SyncClock(newLink);
                if (fresh == null)
                    return FailConnect(newLink, "Clock sync with " + gateName + " failed");

                AdoptClock(fresh);

                // The channels last asked for: asked for while disconnected, they'd otherwise wait for the next race,
                // and a gate that rebooted is back on the plan saved on its page.
                ChannelPlan last = plan;
                if (last == null || !SendPlan(newLink, last.Mhz, last.Names))
                {
                    ReadConfig(newLink);
                }

                Request(newLink, "levels", w => w.WriteNumber("hz", LevelsHz), out _);

                // Which race the gate is running, for OnConnected.
                newLink.FirstStatus.Task.Wait(FirstStatusTimeOut);

                // A connection nothing would ever use, or close.
                if (disposed)
                    return FailConnect(newLink, "Disposed while connecting to " + gateName);

                connected = true;

                Logger.TimingLog.Log(this, "Connected to " + gateName + " (" + GetString(hello, "controller") + " " + GetString(hello, "version") + ", " + maxChannels + " channels) at " + uri
                    + ". Clock: gate = ours " + fresh.OffsetMs.ToString("+0.0;-0.0") + " ms, round trip " + fresh.RoundTripMs.ToString("0.0") + " ms");
                return true;
            }
            catch (Exception ex)
            {
                return FailConnect(newLink, "Can't connect to " + (uri != null ? uri.ToString() : settings.HostName) + ": " + ex.Message);
            }
        }

        private bool FailConnect(Link failed, string why)
        {
            Logger.TimingLog.Log(this, why);
            failed.Close();
            Disconnect();
            return false;
        }

        public bool Disconnect()
        {
            Link old;
            lock (linkLock)
            {
                old = link;
                link = null;
                connected = false;
            }

            old?.Close();
            return true;
        }

        public void OnConnected(bool raceRunning)
        {
            Link l = link;
            if (l == null)
                return;

            long race;
            long from;
            bool open;
            bool timing;
            lock (raceLock)
            {
                race = raceNo;
                from = seenRace == raceNo ? seenSeq + 1 : 0;
                open = raceOpen;
                timing = detecting;
            }

            bool gateRunsIt = GateRuns(l, race);

            // No race of ours to catch up on: new detections come straight through.
            if (!raceRunning || !timing || !gateRunsIt)
            {
                l.CatchingUp = false;
            }

            if (!raceRunning)
            {
                // FPVTrackside isn't racing: stop a race of ours the gate is still running, because its stop never got
                // there. A race the gate is running for anyone else is left alone: a multi-PC review PC, or another
                // client, can connect to the same gate.
                if (open && (!gateRunsIt || Request(l, "stop", null, out _)))
                {
                    lock (raceLock)
                    {
                        raceOpen = false;
                    }

                    if (gateRunsIt)
                    {
                        Logger.TimingLog.Log(this, "Stopped race " + race + ", left running while disconnected");
                    }
                }
                return;
            }

            if (!timing)
                return;

            if (gateRunsIt)
            {
                // The detections from while it was disconnected, then the ones held back, then new ones, in order.
                if (Request(l, "detections", w => w.WriteNumber("from", from), out JsonElement reply, r => l.CatchingUp = false))
                {
                    Logger.TimingLog.Log(this, "Race " + race + " still running: detections from #" + GetLong(reply, "from") + " of " + GetLong(reply, "detections") + " again");
                }
                l.CatchingUp = false;
            }
            else if (race != 0)
            {
                Logger.TimingLog.Log(this, "Race " + race + " is no longer running on the gate");
            }
        }

        public bool SetListeningFrequencies(IEnumerable<ListeningFrequency> newFrequencies)
        {
            if (!Plan(newFrequencies, out int[] mhz, out string[] names))
                return false;

            plan = new ChannelPlan(mhz, names);

            Link l = link;
            if (!Connected || l == null)
                return false;

            Logger.TimingLog.Log(this, "SetListeningFrequencies", string.Join(", ", names.Zip(mhz, (n, m) => n + " " + m)));
            return SendPlan(l, mhz, names);
        }

        private bool SendPlan(Link l, int[] mhz, string[] names)
        {
            bool sent = Request(l, "frequencies", w =>
            {
                w.WriteStartArray("mhz");
                foreach (int m in mhz)
                {
                    w.WriteNumberValue(m);
                }
                w.WriteEndArray();

                w.WriteStartArray("names");
                foreach (string n in names)
                {
                    w.WriteStringValue(n);
                }
                w.WriteEndArray();
            }, out JsonElement reply);

            if (!sent)
                return false;

            if (GetBool(reply, "changed"))
            {
                double changedMs = LocalMs;
                if (!Waiter.WaitFor(() => ReceiversOK(l, changedMs + RescanMs), RescanTimeOut, 20))
                {
                    Logger.TimingLog.Log(this, "The receivers aren't back on the new channels: " + BoardsText());
                }
            }

            ReadConfig(l);
            return true;
        }

        public bool StartDetection(ref DateTime time, StartMetaData startMetaData)
        {
            Link l = link;
            if (!Connected || l == null)
                return false;

            // Timing a race since before the connection dropped: FPVTrackside is starting its race again.
            bool reconnected;
            long race;
            lock (raceLock)
            {
                reconnected = detecting && raceLink != l.Number;
                race = raceNo;
            }

            // The gate kept the race running and kept its detections (OnConnected asked for the ones missed), but a
            // new race would lose any pass under way.
            if (reconnected && GateRuns(l, race) && CarryOn(l))
                return true;

            // Again before each race: the two clocks drift apart.
            ClockMap fresh = SyncClock(l);
            if (fresh == null)
                return false;

            AdoptClock(fresh);

            // Resuming an earlier race, its start is too far back for the gate, which then starts it now. The
            // detections' times don't depend on it.
            double startMs = fresh.GateMs(time);
            bool atTime = Math.Abs(startMs - fresh.GateNowMs) < StartWithinMs;

            Action<Utf8JsonWriter> fields = null;
            if (atTime)
            {
                fields = w => w.WriteNumber("start_ms", Math.Round(startMs, 3));
            }

            if (!Request(l, "start", fields, out JsonElement reply, r => BeginRace(l, fresh, r)))
                return false;

            if (atTime && TryGetDouble(reply, "start_ms", out double gateStartMs))
            {
                time = fresh.LocalTime(gateStartMs);
            }

            bool ready = GetBool(reply, "ready");
            Logger.TimingLog.Log(this, "Race " + GetLong(reply, "race") + " started" + (atTime ? " at " + time.ToLogFormat() : " now") + (ready ? "" : ", but a receiver isn't streaming: " + BoardsText()));

            // Without both receivers no pass is VALID, so the race would have no laps. A race already under way keeps
            // whatever the gate can do.
            if (!ready && !reconnected && settings.CountLaps != RaceTimerLaps.EitherReceiver)
                return false;

            return true;
        }

        private bool CarryOn(Link l)
        {
            lock (raceLock)
            {
                if (!detecting || raceLink == l.Number)
                    return false;

                raceLink = l.Number;
                l.CatchingUp = false;
                Logger.TimingLog.Log(this, "Race " + raceNo + " carries on after reconnecting");
                return true;
            }
        }

        // On the reader thread, before it reads on, so detections that come after the reply are timed for this race.
        private void BeginRace(Link l, ClockMap map, JsonElement reply)
        {
            if (GetString(reply, "type") != "ok")
                return;

            lock (raceLock)
            {
                raceNo = GetLong(reply, "race");
                raceClock = map;
                raceLink = l.Number;
                raceBeganMs = LocalMs;
                seenRace = raceNo;
                seenSeq = -1;
                warnedStopped = false;
                raceOpen = true;
                detecting = true;
            }
            l.CatchingUp = false;
        }

        public bool EndDetection(EndDetectionType type)
        {
            bool open;
            lock (raceLock)
            {
                open = raceOpen;
            }

            // Only a race of ours: other clients may be using the gate.
            if (!open)
            {
                StopTiming();
                return true;
            }

            // Disconnected, raceOpen stays set, and OnConnected stops the race.
            Link l = link;
            if (!Connected || l == null)
            {
                StopTiming();
                return false;
            }

            // Detections that come before the stop's reply were passes before the end, and RaceManager still takes them.
            bool stopped = Request(l, "stop", null, out JsonElement reply, r => StopTiming());
            StopTiming();

            if (!stopped)
                return false;

            lock (raceLock)
            {
                raceOpen = false;
            }

            Logger.TimingLog.Log(this, "Race " + GetLong(reply, "race") + " stopped: " + GetLong(reply, "detections") + " detections");
            return true;
        }

        private void StopTiming()
        {
            lock (raceLock)
            {
                detecting = false;
            }
        }

        public IEnumerable<StatusItem> Status
        {
            get
            {
                if (!Connected)
                {
                    yield return new StatusItem() { StatusOK = false, Value = "Discon" };
                    yield break;
                }

                GateStatus s = status;
                if (s == null)
                    yield break;

                bool stopped;
                lock (raceLock)
                {
                    stopped = detecting && s.ReceivedMs > raceBeganMs && !(s.RaceRunning && s.RaceNo == raceNo);
                }

                if (stopped)
                {
                    yield return new StatusItem() { StatusOK = false, Value = "Stopped" };
                }

                // Shown every frame, so it mustn't throw.
                foreach (GateBoard board in s.Boards)
                {
                    string boardName = board.Name == "bottom" ? "bot" : board.Name;
                    if (board.OK && board.Scanning)
                    {
                        yield return new StatusItem() { StatusOK = true, Value = boardName.Substring(0, Math.Min(1, boardName.Length)).ToUpper() + board.HopsPerSecond.ToString("0") };
                    }
                    else
                    {
                        yield return new StatusItem() { StatusOK = false, Value = "No " + boardName };
                    }
                }

                // racegate-51A0: 51A0, to tell gates apart
                string name = gateName ?? "";
                int dash = name.LastIndexOf('-');
                if (dash >= 0 && dash < name.Length - 1)
                {
                    yield return new StatusItem() { StatusOK = true, Value = name.Substring(dash + 1) };
                }
            }
        }

        public IEnumerable<RSSI> GetRSSI()
        {
            GateLevels lv = levels;
            if (lv == null || LocalMs - lv.ReceivedMs > 1000)
                yield break;

            // RSSINode draws a level as (level - ScaleMin) / ScaleMax, so from 0. EnterAt ends up about two thirds up.
            GateConfig c = config;
            float scaleMax = c != null && c.Enter > 0 ? c.Enter + 60 : 185;

            int count = new int[] { lv.Mhz.Length, lv.Top.Length, lv.Bottom.Length, lv.Crossing.Length }.Min();
            for (int i = 0; i < count; i++)
            {
                yield return new RSSI()
                {
                    TimingSystem = this,
                    Frequency = lv.Mhz[i],
                    CurrentRSSI = Math.Max(lv.Top[i], lv.Bottom[i]),
                    Detected = lv.Crossing[i],
                    ScaleMin = 0,
                    ScaleMax = scaleMax
                };
            }
        }

        private void Read(Link l)
        {
            byte[] buffer = new byte[8192];
            MemoryStream message = new MemoryStream();
            try
            {
                while (!l.Closed)
                {
                    WebSocketReceiveResult result = l.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.TimingLog.Log(this, gateName + " closed the connection");
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > Link.MaxMessageBytes)
                    {
                        Logger.TimingLog.Log(this, "A message from " + gateName + " was too long");
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            OnMessage(l, new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length));
                        }
                        message.SetLength(0);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!l.Closed)
                {
                    Logger.TimingLog.Log(this, "Lost the connection to " + gateName + ": " + ex.Message);
                }
            }
            finally
            {
                l.Close();
                lock (linkLock)
                {
                    if (link == l)
                    {
                        connected = false;
                    }
                }
            }
        }

        private void OnMessage(Link l, ReadOnlyMemory<byte> text)
        {
            l.Heard();
            try
            {
                using (JsonDocument document = JsonDocument.Parse(text))
                {
                    JsonElement message = document.RootElement;
                    if (message.ValueKind != JsonValueKind.Object)
                        return;

                    if (message.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long requestId)
                        && l.TakeRequest(requestId, out PendingRequest request))
                    {
                        request.Complete(message.Clone());
                        return;
                    }

                    // Types from later API versions are ignored.
                    switch (GetString(message, "type"))
                    {
                        case "hello":
                            l.Hello.TrySetResult(message.Clone());
                            break;

                        case "status":
                            OnStatus(l, message);
                            break;

                        case "levels":
                            OnLevels(message);
                            break;

                        case "detection":
                            OnDetection(l, message);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.TimingLog.LogException(this, ex);
            }
        }

        private void OnDetection(Link l, JsonElement detection)
        {
            long race = GetLong(detection, "race", -1);
            long seq = GetLong(detection, "seq", -1);

            // It comes again with the ones missed, after them.
            if (l.CatchingUp)
            {
                Logger.TimingLog.Log(this, "Detection race " + race + " #" + seq + " held back until the ones missed while disconnected have come");
                return;
            }

            int mhz = (int)GetLong(detection, "mhz");
            int peak = (int)GetLong(detection, "peak");
            string verdict = GetString(detection, "verdict") ?? "?";
            bool valid = GetBool(detection, "valid");

            if (!TryGetDouble(detection, "time_ms", out double timeMs))
                return;

            bool raise;
            DateTime time;
            lock (raceLock)
            {
                // Had already: a replay after reconnecting can overlap what came live.
                if (race == seenRace && seq <= seenSeq)
                    return;

                seenRace = race;
                seenSeq = seq;

                if (detecting && race != raceNo)
                {
                    Logger.TimingLog.Log(this, "Race " + race + " replaced race " + raceNo + " on the gate: another client started it");
                    raceNo = race;
                }

                ClockMap map = raceClock ?? clock;
                if (map == null)
                    return;

                time = map.LocalTime(timeMs);
                raise = detecting && Counts(verdict, valid);
            }

            string delta = TryGetDouble(detection, "delta_db", out double deltaDb) ? " " + deltaDb.ToString("+0.00;-0.00") + " dB" : "";
            Logger.TimingLog.Log(this, "Detection race " + race + " #" + seq + " " + GetString(detection, "name") + " " + mhz + " " + verdict
                + " top " + GetLong(detection, "top") + " bottom " + GetLong(detection, "bottom") + delta + " " + time.ToLogFormat() + (raise ? "" : " (not counted)"));

            if (raise)
            {
                OnDetectionEvent?.Invoke(this, mhz, time, peak);
            }
        }

        private bool Counts(string verdict, bool valid)
        {
            switch (settings.CountLaps)
            {
                case RaceTimerLaps.BothReceivers:
                    return valid || verdict == "OVER" || verdict == "UNDER";

                case RaceTimerLaps.EitherReceiver:
                    return true;

                default:
                    return valid;
            }
        }

        private void OnStatus(Link l, JsonElement message)
        {
            GateStatus s = new GateStatus();
            s.Link = l.Number;
            s.ReceivedMs = LocalMs;

            if (message.TryGetProperty("race", out JsonElement race))
            {
                s.RaceNo = GetLong(race, "no");
                s.RaceRunning = GetBool(race, "running");
            }

            List<GateBoard> boards = new List<GateBoard>();
            if (message.TryGetProperty("boards", out JsonElement boardsElement) && boardsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement b in boardsElement.EnumerateArray())
                {
                    string name = GetString(b, "name");
                    boards.Add(new GateBoard()
                    {
                        Name = string.IsNullOrEmpty(name) ? "board " + boards.Count : name,
                        OK = GetBool(b, "ok"),
                        Scanning = GetBool(b, "scanning"),
                        HopsPerSecond = GetDouble(b, "hops_per_s")
                    });
                }
            }
            s.Boards = boards.ToArray();

            GateStatus previous = status;
            status = s;
            l.FirstStatus.TrySetResult(true);

            foreach (GateBoard board in s.Boards)
            {
                GateBoard before = previous?.Boards.FirstOrDefault(b => b.Name == board.Name);
                bool working = board.OK && board.Scanning;
                if (before == null ? !working : working != (before.OK && before.Scanning))
                {
                    Logger.TimingLog.Log(this, "The " + board.Name + " receiver " + (working ? "is streaming" : "isn't streaming (ok " + board.OK + ", scanning " + board.Scanning + ")"));
                }
            }

            lock (raceLock)
            {
                if (detecting && raceLink == l.Number && s.ReceivedMs > raceBeganMs)
                {
                    if (s.RaceRunning && s.RaceNo != raceNo)
                    {
                        Logger.TimingLog.Log(this, "Race " + s.RaceNo + " replaced race " + raceNo + " on the gate: another client started it");
                        raceNo = s.RaceNo;
                    }
                    else if (!s.RaceRunning && !warnedStopped)
                    {
                        warnedStopped = true;
                        Logger.TimingLog.Log(this, "The gate stopped race " + raceNo + " (another client?), and keeps no more detections");
                    }
                }
            }
        }

        private void OnLevels(JsonElement message)
        {
            levels = new GateLevels()
            {
                ReceivedMs = LocalMs,
                Mhz = GetInts(message, "mhz"),
                Top = GetInts(message, "top"),
                Bottom = GetInts(message, "bottom"),
                Crossing = GetBools(message, "crossing")
            };
        }

        private void ReadConfig(Link l)
        {
            if (!Request(l, "config", null, out JsonElement reply))
                return;

            GateConfig c = new GateConfig()
            {
                Enter = (int)GetLong(reply, "enter"),
                Exit = (int)GetLong(reply, "exit")
            };
            config = c;

            int[] mhz = GetInts(reply, "mhz");
            string[] names = GetStrings(reply, "names");
            string text = "channels " + string.Join(" ", mhz.Select((m, i) => (i < names.Length ? names[i] + "=" : "") + m))
                + ", enter " + c.Enter + ", exit " + c.Exit + ", gain " + GetLong(reply, "gain")
                + ", delta " + GetDouble(reply, "delta_db") + " dB, offset " + GetDouble(reply, "offset_db") + " dB"
                + ", pair " + GetLong(reply, "pair_ms") + " ms, wait " + GetLong(reply, "wait_ms") + " ms, min lap " + GetLong(reply, "min_lap_ms") + " ms";

            if (text != configText)
            {
                configText = text;
                Logger.TimingLog.Log(this, gateName + ": " + text);
            }
        }

        // The status from this connection says the gate is running this race.
        private bool GateRuns(Link l, long race)
        {
            GateStatus s = status;
            return race != 0 && s != null && s.Link == l.Number && s.RaceRunning && s.RaceNo == race;
        }

        private bool ReceiversOK(Link l, double sinceMs)
        {
            GateStatus s = status;
            return s != null && s.Link == l.Number && s.ReceivedMs >= sinceMs && s.Boards.Length > 0 && s.Boards.All(b => b.OK && b.Scanning);
        }

        private string BoardsText()
        {
            GateStatus s = status;
            if (s == null)
                return "no status";

            return string.Join(", ", s.Boards.Select(b => b.Name + (b.OK && b.Scanning ? " ok" : " not streaming")));
        }

        // The channel plan, as the gate takes it: 1 to its maximum different frequencies, whole MHz in the range its
        // receivers tune. A channel the gate can't time fails the race start if a pilot's on it, and is left out if not.
        private bool Plan(IEnumerable<ListeningFrequency> frequencies, out int[] mhz, out string[] names)
        {
            mhz = null;
            names = null;

            List<ListeningFrequency> chosen = new List<ListeningFrequency>();
            IEnumerable<ListeningFrequency> given = (frequencies ?? Enumerable.Empty<ListeningFrequency>()).Where(f => f != null);
            foreach (IGrouping<int, ListeningFrequency> same in given.GroupBy(f => f.Frequency).OrderBy(g => g.Key))
            {
                ListeningFrequency f = same.FirstOrDefault(HasPilot) ?? same.First();
                if (f.Frequency < MinMhz || f.Frequency > MaxMhz)
                {
                    if (HasPilot(f))
                    {
                        Logger.TimingLog.Log(this, f.Pilot + " is on " + f.Frequency + " MHz, outside the " + MinMhz + " to " + MaxMhz + " MHz the gate tunes");
                        return false;
                    }

                    Logger.TimingLog.Log(this, "Leaving out " + f.Frequency + " MHz, outside the " + MinMhz + " to " + MaxMhz + " MHz the gate tunes");
                    continue;
                }
                chosen.Add(f);
            }

            if (chosen.Count > maxChannels)
            {
                ListeningFrequency[] withPilots = chosen.Where(HasPilot).ToArray();
                if (withPilots.Length > maxChannels)
                {
                    Logger.TimingLog.Log(this, withPilots.Length + " pilots, but the gate times " + maxChannels + " channels at most");
                    return false;
                }

                chosen = withPilots.Concat(chosen.Where(f => !HasPilot(f)).Take(maxChannels - withPilots.Length)).OrderBy(f => f.Frequency).ToList();
            }

            if (chosen.Count == 0)
            {
                Logger.TimingLog.Log(this, "No channels to time");
                return false;
            }

            mhz = chosen.Select(f => f.Frequency).ToArray();
            names = chosen.Select(ChannelName).ToArray();
            return true;
        }

        private static bool HasPilot(ListeningFrequency f)
        {
            return f.PilotId != Guid.Empty || !string.IsNullOrEmpty(f.Pilot);
        }

        // As FPVTrackside shows the channel, e.g. "R1": the band's letter and the channel. The gate takes 1 to 5
        // letters or digits, and shows them on its page.
        private static string ChannelName(ListeningFrequency f)
        {
            string band = f.Band ?? "";

            string letter = "";
            if (band == "HDZero" || band == "SharkByte")
            {
                letter = "Z";
            }
            else if (band.Length > 0 && band != "None")
            {
                letter = band.Substring(0, 1);
            }

            string name = letter.Length > 0 ? letter + f.Channel : "";
            if (name.Length == 0 || name.Length > MaxNameLength || !name.All(char.IsAsciiLetterOrDigit))
            {
                name = f.Frequency.ToString();
            }
            return name;
        }

        // Best of a few round trips: the one that took the least time was held up the least, so its middle is the
        // best guess for when the gate read its clock.
        private ClockMap SyncClock(Link l)
        {
            double bestRoundTrip = double.MaxValue;
            double bestOffset = 0;

            for (int i = 0; i < ClockSyncRounds; i++)
            {
                double sent = LocalMs;
                if (!Request(l, "time", null, out JsonElement reply))
                    return null;
                double back = LocalMs;

                if (TryGetDouble(reply, "time_ms", out double gateMs) && back - sent < bestRoundTrip)
                {
                    bestRoundTrip = back - sent;
                    bestOffset = gateMs - (sent + back) / 2;
                }
            }

            if (bestRoundTrip == double.MaxValue)
                return null;

            return new ClockMap(bestOffset, bestRoundTrip);
        }

        private void AdoptClock(ClockMap fresh)
        {
            ClockMap previous = clock;
            bool restarted = previous != null && Math.Abs(fresh.OffsetMs - previous.OffsetMs) > GateRestartMs;
            clock = fresh;

            lock (raceLock)
            {
                if (restarted && raceNo != 0)
                {
                    Logger.TimingLog.Log(this, "The gate's clock restarted, so it rebooted, and race " + raceNo + " went with it");
                    raceNo = 0;
                    raceOpen = false;
                    seenRace = -1;
                    seenSeq = -1;
                }

                // Mid-race, the race keeps the map its detections so far were timed with, so its lap times stay exact.
                if (!detecting || restarted || raceClock == null)
                {
                    raceClock = fresh;
                }
            }
        }

        private bool Request(Link l, string cmd, Action<Utf8JsonWriter> fields, out JsonElement reply, Action<JsonElement> onReply = null)
        {
            reply = default;
            if (l == null || l.Closed)
                return false;

            long id = Interlocked.Increment(ref nextId);
            byte[] message = RequestMessage(cmd, id, fields);

            // From a handler of one of the reader's detections: the reply can't be read until the handler returns.
            if (Thread.CurrentThread == l.Reader)
            {
                Logger.TimingLog.Log(this, cmd + " sent from the reader thread, so not waiting for its reply");
                l.Send(message);
                return false;
            }

            PendingRequest request = new PendingRequest(onReply);
            l.AddRequest(id, request);

            if (!l.Send(message))
            {
                l.TakeRequest(id, out _);
                return false;
            }

            if (!request.Reply.Wait(ReplyTimeOut))
            {
                if (l.TakeRequest(id, out _))
                {
                    Logger.TimingLog.Log(this, cmd + ": no reply in " + ReplyTimeOut.TotalSeconds + " s");
                    return false;
                }

                // The reply came as it timed out.
                request.Reply.Wait();
            }

            reply = request.Reply.Result;
            if (reply.ValueKind != JsonValueKind.Object)
                return false;

            if (GetString(reply, "type") == "error")
            {
                Logger.TimingLog.Log(this, cmd + " refused: " + GetString(reply, "error"));
                return false;
            }

            return true;
        }

        private static byte[] RequestMessage(string cmd, long id, Action<Utf8JsonWriter> fields)
        {
            ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
            using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("cmd", cmd);
                writer.WriteNumber("id", id);
                fields?.Invoke(writer);
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        // A monotonic clock, in ms.
        private static double LocalMs { get { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; } }

        private static bool TryGetDouble(JsonElement e, string name, out double value)
        {
            value = 0;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
        }

        private static double GetDouble(JsonElement e, string name)
        {
            TryGetDouble(e, name, out double value);
            return value;
        }

        private static long GetLong(JsonElement e, string name, long fallback = 0)
        {
            if (TryGetDouble(e, name, out double value))
                return (long)Math.Round(value);
            return fallback;
        }

        private static bool GetBool(JsonElement e, string name)
        {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.True;
        }

        private static string GetString(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
            return null;
        }

        private static IEnumerable<JsonElement> GetArray(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Array)
                return p.EnumerateArray();
            return Enumerable.Empty<JsonElement>();
        }

        private static int[] GetInts(JsonElement e, string name)
        {
            return GetArray(e, name).Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? (int)Math.Round(d) : 0).ToArray();
        }

        private static bool[] GetBools(JsonElement e, string name)
        {
            return GetArray(e, name).Select(v => v.ValueKind == JsonValueKind.True).ToArray();
        }

        private static string[] GetStrings(JsonElement e, string name)
        {
            return GetArray(e, name).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : "").ToArray();
        }

        // gate = local + offset, in ms. The epoch is the DateTime of local ms 0, taken at the sync.
        private class ClockMap
        {
            public double OffsetMs { get; private set; }
            public double RoundTripMs { get; private set; }
            public DateTime Epoch { get; private set; }

            public double GateNowMs { get { return LocalMs + OffsetMs; } }

            public ClockMap(double offsetMs, double roundTripMs)
            {
                OffsetMs = offsetMs;
                RoundTripMs = roundTripMs;
                Epoch = DateTime.Now - TimeSpan.FromTicks((long)Math.Round(LocalMs * TimeSpan.TicksPerMillisecond));
            }

            public double GateMs(DateTime time)
            {
                return (time - Epoch).TotalMilliseconds + OffsetMs;
            }

            public DateTime LocalTime(double gateMs)
            {
                return Epoch + TimeSpan.FromTicks((long)Math.Round((gateMs - OffsetMs) * TimeSpan.TicksPerMillisecond));
            }
        }

        private class ChannelPlan
        {
            public int[] Mhz { get; private set; }
            public string[] Names { get; private set; }

            public ChannelPlan(int[] mhz, string[] names)
            {
                Mhz = mhz;
                Names = names;
            }
        }

        private class GateStatus
        {
            public int Link { get; set; }
            public double ReceivedMs { get; set; }
            public long RaceNo { get; set; }
            public bool RaceRunning { get; set; }
            public GateBoard[] Boards { get; set; }
        }

        private class GateBoard
        {
            public string Name { get; set; }
            public bool OK { get; set; }
            public bool Scanning { get; set; }
            public double HopsPerSecond { get; set; }
        }

        private class GateLevels
        {
            public double ReceivedMs { get; set; }
            public int[] Mhz { get; set; }
            public int[] Top { get; set; }
            public int[] Bottom { get; set; }
            public bool[] Crossing { get; set; }
        }

        private class GateConfig
        {
            public int Enter { get; set; }
            public int Exit { get; set; }
        }

        private class PendingRequest
        {
            private TaskCompletionSource<JsonElement> reply;
            private Action<JsonElement> onReply;

            public Task<JsonElement> Reply { get { return reply.Task; } }

            public PendingRequest(Action<JsonElement> onReply)
            {
                this.onReply = onReply;
                reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void Complete(JsonElement message)
            {
                try
                {
                    onReply?.Invoke(message);
                }
                finally
                {
                    reply.TrySetResult(message);
                }
            }

            public void Fail()
            {
                reply.TrySetResult(default);
            }
        }

        // One connection to the gate. Reconnecting makes a new one, so an old reader finishing late can't touch it.
        private class Link
        {
            public const int MaxMessageBytes = 65536;

            private static readonly TimeSpan SendTimeOut = TimeSpan.FromSeconds(2);

            public int Number { get; private set; }
            public ClientWebSocket Socket { get; private set; }
            public Thread Reader { get; set; }
            public TaskCompletionSource<JsonElement> Hello { get; private set; }
            public TaskCompletionSource<bool> FirstStatus { get; private set; }

            public bool Closed { get { return closed; } }

            // Reconnected mid-race: new detections wait until the ones missed have been asked for, so that they all
            // come in order.
            public volatile bool CatchingUp;

            public double SilentMs { get { return (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastHeard)) * 1000.0 / Stopwatch.Frequency; } }

            private volatile bool closed;
            private long lastHeard;
            private readonly object sendLock = new object();
            private readonly Dictionary<long, PendingRequest> requests = new Dictionary<long, PendingRequest>();

            public Link(int number)
            {
                Number = number;
                Socket = new ClientWebSocket();

                // The gate's status every second shows the connection's alive.
                Socket.Options.KeepAliveInterval = TimeSpan.Zero;

                Hello = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                FirstStatus = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Heard();
            }

            public void Heard()
            {
                Interlocked.Exchange(ref lastHeard, Stopwatch.GetTimestamp());
            }

            public void AddRequest(long id, PendingRequest request)
            {
                lock (requests)
                {
                    if (closed)
                    {
                        request.Fail();
                        return;
                    }
                    requests[id] = request;
                }
            }

            public bool TakeRequest(long id, out PendingRequest request)
            {
                lock (requests)
                {
                    return requests.Remove(id, out request);
                }
            }

            public bool Send(byte[] message)
            {
                lock (sendLock)
                {
                    if (closed || Socket.State != WebSocketState.Open)
                        return false;

                    try
                    {
                        using (CancellationTokenSource cts = new CancellationTokenSource(SendTimeOut))
                        {
                            Socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.TimingLog.Log(this, "Send failed: " + ex.Message);
                        return false;
                    }
                }
            }

            public void Close()
            {
                closed = true;

                try
                {
                    Socket.Abort();
                    Socket.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.TimingLog.LogException(this, ex);
                }

                PendingRequest[] waiting;
                lock (requests)
                {
                    waiting = requests.Values.ToArray();
                    requests.Clear();
                }

                foreach (PendingRequest request in waiting)
                {
                    request.Fail();
                }

                Hello.TrySetResult(default);
                FirstStatus.TrySetResult(false);
            }
        }
    }
}
