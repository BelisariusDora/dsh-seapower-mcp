// TR2-H: the harness link, data half.
//
// Two directions, two very different mechanisms, and the difference is worth stating
// before the code:
//
//   harness -> game   The DSH web API needs a session cookie that a process outside the
//                     browser cannot obtain, so nothing here talks HTTP. What the harness
//                     does publish on disk is its own session transcript, appended frame
//                     by frame (zstd, one frame per delivery) under
//                     %USERPROFILE%\.dsh\sessions\<workspace>\<session>\session.v4.jsonl.zstd.
//                     An external helper (comms\dsh_tailer.js) decodes those frames and
//                     appends plain JSON lines to comms\run\live.jsonl. This class only
//                     ever reads that already-decoded file, on a background thread, and
//                     keeps a bounded ring of what a player may see: the visible assistant
//                     text, the tool being executed, and turn boundaries. The helper drops
//                     `reasoning` content on purpose - hidden thinking must never reach a
//                     panel that claims to show the agent's reply.
//
//   game -> harness   Also a file: the player's line is appended to comms\outbox.jsonl and
//                     shown immediately as QUEUED. Something outside the game has to carry
//                     it the rest of the way (see docs/HARNESS_COMMS.md); the bridge does
//                     not pretend it was delivered.
//
// Nothing here touches Unity or Noesis. It runs on its own thread and publishes an
// immutable snapshot, so a broken link can never take the frame down.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    /// <summary>One line of the harness link, already reduced to what a panel may draw.</summary>
    public sealed class CommsItem
    {
        public long Seq;
        public string Kind = "";      // user | assistant | exec | execdone | turn
        /// <summary>
        /// The agent's current reasoning when the helper forwarded it. NOT an agent reply: the
        /// page shows it as one live "thinking" row and never as TA&gt; speech.
        /// </summary>
        public string Thinking = "";
        public string Phase = "";     // start | end for turn
        public string Text = "";
        public string Tool = "";
        public bool IsError;
        public double At;             // unix ms

        /// <summary>Which outbox line this row stands for (0 = not a player echo). The panel
        /// echoes a typed line at once, and the transcript carries the same line a moment later;
        /// without this id the page shows it twice - and the second copy arrives AFTER the next
        /// message, which reads as "send shows me the previous line".</summary>
        public long OutgoingId;
        public string Target = "dsh";

        /// <summary>The transcript confirmed this player line. Set on the echo row itself, so the
        /// page still shows one row per typed message.</summary>
        public bool Delivered;
    }

    /// <summary>One player message waiting for the harness to pick it up.</summary>
    public sealed class CommsOutgoing
    {
        public long Id;
        public double At;
        public string Text = "";
        public string State = "queued";   // queued | taken
        public string Target = "dsh";
        public bool Matched;              // the transcript already showed this line
    }

    public static class CommsBridge
    {
        public const string Section = "HarnessComms";

        private const string LiveFile = "live.jsonl";
        private const string StateFile = "tailer.state.json";
        private const string OutboxFile = "outbox.jsonl";

        private static readonly object _lock = new object();
        private static readonly List<CommsItem> _items = new List<CommsItem>();
        private static readonly List<CommsOutgoing> _out = new List<CommsOutgoing>();

        private static string _runDir;
        private static string _livePath;
        private static string _statePath;
        private static string _outboxPath;

        private static long _offset;
        private static long _seq = 1;
        private static long _outSeq = 1;
        private static long _lines;
        private static long _dropped;
        private static string _sessionId = "";
        private static readonly string GameInstance = Guid.NewGuid().ToString("N");
        private static string _displayWorld;
        private static bool _perWorld;
        private static string _worldSession;

        private static string WorldKey()
        {
            string state = SessionRegistry.BoundaryState;
            if (state != SessionRegistry.StateWatching && state != SessionRegistry.StateVerified) return null;
            return GameInstance + ":" + SessionRegistry.WorldGeneration.ToString(CultureInfo.InvariantCulture);
        }

        private static void RefreshWorldLocked()
        {
            if (!_perWorld) return;
            string world = WorldKey();
            if (_displayWorld == world) return;
            _displayWorld = world;
            _items.Clear();
            _out.Clear();
            _sessionId = "";
            _worldSession = null;
            _link = "no-session";
            _tailerNote = "waiting for this mission's DSH session";
        }
        private static string _sessionSlug = "";
        private static string _link = "unknown";     // unknown | attached | no-session | down
        private static string _tailerNote = "";
        private static double _lastLineAt;
        private static double _lastAttachAt;
        private static double _missingSince;
        private static long _fileBornTicks;
        private static string _lastError = "";

        private static Thread _thread;
        private static volatile bool _stop;
        private static int _pollMs = 250;
        private static int _maxItems = 600;
        private static string _agentName = "TACTICAL ADVISOR";
        private static string _agentCall = "TA";
        private static bool _started;

        // ------------------------------------------------------------ operator HOLD
        //
        // HOLD is the operator's stop, and it lives here rather than in the panel for one
        // reason: a window can be hidden, rebuilt or never installed, and a stop that
        // disappears with a window is not a stop. Api's write gate reads this field before
        // it touches a hull, so the refusal never depends on a caller asking nicely.
        private static readonly object _holdLock = new object();
        private static bool _hold;
        private static string _holdSource = "";
        private static long _holdAtMs;
        private const string HoldFileName = "hold.on";

        /// <summary>True while the operator holds every write. Reads are unaffected.</summary>
        public static bool Hold
        {
            get { lock (_holdLock) { return _hold; } }
        }

        /// <summary>The hold as JSON, for /ui/state and /comms/state.</summary>
        public static JObject HoldState()
        {
            lock (_holdLock)
            {
                JObject o = new JObject();
                o["hold"] = _hold;
                o["holdSource"] = _holdSource;
                o["holdSinceUnixMs"] = _holdAtMs > 0 ? (JToken)_holdAtMs : JValue.CreateNull();
                o["holdPersisted"] = HoldFileExists();
                // If the two disagree, say which one wins: the in-memory flag is what the write
                // path checks, the file is what a restart obeys. Silence means an operator
                // releases the hold, sees "released", and meets it again after a reload.
                if (o["holdPersisted"].Value<bool>() != o["hold"].Value<bool>())
                    o["holdPersistWarning"] = "the hold file disagrees with the live state; a restart will use the file";
                return o;
            }
        }

        /// <summary>Set or release the hold. The panel click and the HTTP route both end here.</summary>
        public static JObject SetHold(bool on, string source)
        {
            bool before;
            long at;
            lock (_holdLock)
            {
                before = _hold;
                _hold = on;
                _holdSource = source == null ? "" : source;
                at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .TotalMilliseconds;
                _holdAtMs = at;
            }

            JObject o = new JObject();
            o["hold"] = on;
            o["wasHold"] = before;
            o["changed"] = before != on;
            o["source"] = source;
            o["sinceUnixMs"] = at;
            o["note"] = "hold is bridge state, not agent state: removing or hiding a window "
                + "cannot clear it, and it does not depend on any model being reachable";
            Core.Log("hold -> " + on + " (source " + source + ")");
            // The receipt says what happened to the file, not what we hoped. A release whose
            // delete fails looks identical to a successful one until the next restart brings
            // the hold back, and that is the worst time to find out (review round 2).
            bool persisted = PersistHold(on, source);
            o["persisted"] = persisted;
            if (!persisted)
            {
                o["persistWarning"] = "the hold file at " + (HoldFilePath() ?? "<unknown>") +
                    " could not be written or removed; this hold may not survive a restart";
                o["note"] = o["note"] + "; " + o["persistWarning"];
            }
            return o;
        }

        private static string HoldFilePath()
        {
            try { return Path.Combine(Core.ModDir(), HoldFileName); }
            catch (Exception) { return null; }
        }

        private static bool HoldFileExists()
        {
            string p = HoldFilePath();
            if (p == null) return false;
            try { return File.Exists(p); }
            catch (Exception) { return false; }
        }

        // A hold that disappears when the scene reloads is not a stop. It is a file next to
        // bridge.log, like session_gate.on and reveal_foreign.on: inspectable, and gone when
        // the operator deletes it.
        private static bool PersistHold(bool on, string source)
        {
            string p = HoldFilePath();
            if (p == null) return false;
            try
            {
                if (on) File.WriteAllText(p, (source == null ? "" : source) + Environment.NewLine);
                else if (File.Exists(p)) File.Delete(p);
                // Report the state of the file, not the absence of an exception.
                bool matches = on ? File.Exists(p) : !File.Exists(p);
                if (!matches) Core.Log("hold file: wrote " + (on ? "on" : "off") + " but " + p + " disagrees");
                return matches;
            }
            catch (Exception e)
            {
                Core.Log("hold file: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Read a hold left behind by an earlier session. Called once from Attach().</summary>
        private static void RestoreHold()
        {
            string p = HoldFilePath();
            if (p == null) return;
            bool exists;
            try { exists = File.Exists(p); }
            catch (Exception) { return; }
            if (!exists) return;
            string src = "hold.on (restored)";
            try
            {
                string text = File.ReadAllText(p).Trim();
                if (text.Length > 0) src = text;
            }
            catch (Exception) { }
            lock (_holdLock)
            {
                _hold = true;
                _holdSource = src;
                // No timestamp survives a restart, and 0 would render as 1970. HoldState turns a
                // negative value into null ("since unknown") instead of inventing a date.
                _holdAtMs = -1;
            }
            Core.Log("hold restored from " + HoldFileName + " (source " + src + ")");
        }

        /// <summary>Called once the mod folder is known. Cheap and idempotent.</summary>
        internal static void Attach()
        {
            try
            {
                string modDir = Core.ModDir();
                if (string.IsNullOrEmpty(modDir)) return;
                string dir = Path.Combine(modDir, "comms");
                string run = Path.Combine(dir, "run");
                try { Directory.CreateDirectory(run); }
                catch (Exception) { }
                lock (_lock)
                {
                    _runDir = run;
                    _livePath = Path.Combine(run, LiveFile);
                    _statePath = Path.Combine(run, StateFile);
                    _outboxPath = Path.Combine(dir, OutboxFile);
                    _agentName = Setting("AgentName", _agentName);
                    _agentCall = Setting("AgentCall", _agentCall);
                    _pollMs = SettingInt("PollMs", _pollMs, 100, 4000);
                    _maxItems = SettingInt("MaxItems", _maxItems, 50, 4000);
                }
                StartThread();
                // A hold left behind by an earlier session outranks a clean start: the
                // operator set it, and only the operator gets to clear it.
                RestoreHold();
            }
            catch (Exception ex)
            {
                lock (_lock) { _lastError = "attach failed: " + ex.GetType().Name + ": " + ex.Message; }
            }
        }

        /// <summary>
        /// One background reader. It is the only thing that touches the disk on a timer, and
        /// it publishes items through the lock; the frame never waits on it.
        /// </summary>
        private static void StartThread()
        {
            if (_started) return;
            _started = true;
            _stop = false;
            Thread t = new Thread(delegate ()
            {
                while (!_stop)
                {
                    try { Pump(); }
                    catch (Exception ex)
                    {
                        lock (_lock) { _lastError = ex.GetType().Name + ": " + ex.Message; }
                    }
                    int wait = _pollMs;
                    if (wait < 100) wait = 100;
                    Thread.Sleep(wait);
                }
            });
            t.IsBackground = true;
            t.Name = "dsh-comms-link";
            _thread = t;
            t.Start();
        }

        internal static void Stop()
        {
            _stop = true;
        }

        private static void Pump()
        {
            string live;
            string state;
            lock (_lock)
            {
                live = _livePath;
                state = _statePath;
            }
            if (string.IsNullOrEmpty(live)) return;

            try
            {
                string pointer = Path.Combine(Path.GetDirectoryName(live), "session.pointer");
                JObject pin = JObject.Parse(ReadShared(pointer));
                lock (_lock)
                {
                    _perWorld = (string)pin["source"] == "game-world";
                    RefreshWorldLocked();
                    _worldSession = _perWorld && _displayWorld != null && (string)pin["world"] == _displayWorld
                        ? (string)pin["sessionId"] : null;
                }
            }
            catch (Exception) { }

            // The tailer's own state file names the session it follows. Read before the log
            // so a freshly attached session is labelled in the same pass.
            try
            {
                if (File.Exists(state))
                {
                    string text = ReadShared(state);
                    if (!string.IsNullOrEmpty(text))
                    {
                        JObject o = JObject.Parse(text);
                        string sid = (string)o["sessionId"];
                        string slug = (string)o["workspaceSlug"];
                        lock (_lock)
                        {
                            if (!_perWorld && !string.IsNullOrEmpty(sid)) _sessionId = sid;
                            if (!string.IsNullOrEmpty(slug)) _sessionSlug = slug;
                        }
                    }
                }
            }
            catch (Exception) { }

            if (!File.Exists(live))
            {
                // Debounced on purpose: one stat miss while a helper restarts must not latch
                // the panel into "down" while the link is fine, and recovery must not depend
                // on a new line arriving.
                lock (_lock)
                {
                    if (_missingSince <= 0) _missingSince = NowMs();
                    bool gone = NowMs() - _missingSince >= 2000;
                    if (gone && _link != "down")
                    {
                        _link = "down";
                        _tailerNote = "no " + LiveFile + " yet - start the tailer (see docs\\HARNESS_COMMS.md)";
                    }
                }
                return;
            }

            lock (_lock) { _missingSince = 0; }

            // The helper writes a fresh file when it starts. Offset alone cannot tell a
            // rewritten file from an appended one, and size alone gets it wrong whenever the
            // new file happens to be longer - which is how thirty rows went unread.
            long born = 0;
            try { born = File.GetCreationTimeUtc(live).Ticks; }
            catch (Exception) { }
            if (born > 0 && _fileBornTicks != 0 && born != _fileBornTicks)
            {
                lock (_lock)
                {
                    _offset = 0;
                    _items.Clear();
                    _out.Clear();
                    _dropped = 0;
                    _lines = 0;
                }
            }
            if (born > 0) _fileBornTicks = born;

            byte[] chunk;
            int count = 0;
            long newOffset;
            lock (_lock)
            {
                try
                {
                    using (FileStream fs = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < _offset)
                        {
                            // The helper restarted the file underneath us. Keeping the old
                            // ring would mix a dead transcript into the live page.
                            _offset = 0;
                            _items.Clear();
                            _out.Clear();
                            _dropped = 0;
                            _lines = 0;
                        }
                        long avail = fs.Length - _offset;
                        if (avail <= 0) return;
                        int want = (int)Math.Min(262144L, avail);
                        chunk = new byte[want];
                        fs.Seek(_offset, SeekOrigin.Begin);
                        count = fs.Read(chunk, 0, want);
                        newOffset = _offset + count;
                    }
                }
                catch (Exception ex)
                {
                    _lastError = "read live.jsonl: " + ex.GetType().Name + ": " + ex.Message;
                    return;
                }
                _offset = newOffset;
            }
            if (count <= 0) return;

            // The cursor is a byte offset. Find the complete line boundary before
            // decoding so Chinese text and split UTF-8 sequences cannot rewind it too far.
            int cut = Array.LastIndexOf(chunk, (byte)'\n', count - 1, count);
            int complete = cut + 1;
            if (complete < count)
            {
                lock (_lock) { _offset -= count - complete; }
            }
            if (complete == 0) return;
            string chunkText = Encoding.UTF8.GetString(chunk, 0, complete);
            foreach (string line in chunkText.Split(new char[] { '\n' }))
            {
                if (line.Length > 0) ParseLine(line);
            }
        }

        private static string ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(65536L, fs.Length);
                byte[] buf = new byte[len];
                int n = fs.Read(buf, 0, len);
                return Encoding.UTF8.GetString(buf, 0, n);
            }
        }

        /// <summary>Map one tailer line onto an item, or onto link state. Never throws.</summary>
        private static void ParseLine(string line)
        {
            JObject o;
            try { o = JObject.Parse(line); }
            catch (Exception) { return; }

            string k = (string)o["k"];
            if (string.IsNullOrEmpty(k)) return;
            lock (_lock)
            {
                RefreshWorldLocked();
                if (_perWorld && (string.IsNullOrEmpty(_worldSession) ||
                    (string)o["sessionId"] != _worldSession)) return;
            }

            if (k == "tailer")
            {
                string phase = (string)o["phase"];
                lock (_lock)
                {
                    if (phase == "attached")
                    {
                        // A re-attach means the helper started over: its file may be a fresh
                        // tail window, so anything still on the page came from a previous run
                        // and would read as "the agent just said this".
                        _items.Clear();
                        _out.Clear();
                        _dropped = 0;
                        _lines = 0;
                        _link = "attached";
                        _lastAttachAt = NowMs();
                        _tailerNote = "";
                        string sid = (string)o["sessionId"];
                        if (!string.IsNullOrEmpty(sid)) _sessionId = sid;
                    }
                    else if (phase == "start")
                    {
                        // A start line means the helper opened a new file: nothing on the page
                        // came from it, so drop the old transcript instead of mixing runs.
                        _items.Clear();
                        _out.Clear();
                        _dropped = 0;
                        _lines = 0;
                        _link = "unknown";
                        _tailerNote = "the tailer started - attaching";
                    }
                    else if (phase == "no-session")
                    {
                        _link = "no-session";
                        _tailerNote = "no session transcript yet - the page follows the next turn";
                    }
                    else if (phase == "error")
                    {
                        _lastError = "tailer: " + (string)o["message"];
                    }
                    else if (phase == "switched")
                    {
                        // A new chat started: drop what belonged to the old transcript rather
                        // than mixing two agents' words on one page.
                        _items.Clear();
                        _tailerNote = "session switched";
                    }
                }
                return;
            }

            CommsItem item = new CommsItem();
            item.At = AsDouble(o["time"]);
            item.Seq = AsLong(o["seq"]);
            if (k == "user")
            {
                item.Kind = "user";
                item.Text = AsString(o["text"]);
                // One typed line, one row: match the transcript copy to the oldest unmatched echo
                // with the same text, mark it delivered and do NOT add a second row. Matching is
                // one-to-one and in order, so typing the same sentence twice still shows twice.
                string echoText = (item.Text ?? "").Trim();
                lock (_lock)
                {
                    for (int i = 0; i < _out.Count; i++)
                    {
                        CommsOutgoing o2 = _out[i];
                        if (o2.Target != "dsh") continue;
                        if (o2.Matched || o2.Target != "dsh") continue;
                        if ((o2.Text ?? "").Trim() != echoText) continue;
                        o2.Matched = true;
                        o2.State = "taken";
                        for (int j = 0; j < _items.Count; j++)
                        {
                            if (_items[j].OutgoingId == o2.Id) { _items[j].Delivered = true; break; }
                        }
                        _lines++;
                        _lastLineAt = NowMs();
                        _link = "attached";
                        _tailerNote = "";
                        return;
                    }
                }
            }
            else if (k == "assistant")
            {
                item.Kind = "assistant";
                item.Text = AsString(o["text"]);
                item.Thinking = AsString(o["thinking"]);
                JArray tools = o["tools"] as JArray;
                string names = "";
                if (tools != null)
                {
                    for (int i = 0; i < tools.Count; i++)
                    {
                        if (names.Length > 0) names += ", ";
                        names += (string)tools[i];
                    }
                }
                item.Tool = names;
                if (item.Text.Length == 0 && names.Length == 0) return;
            }
            else if (k == "exec")
            {
                item.Kind = "exec";
                item.Tool = AsString(o["name"]);
                item.Text = AsString(o["args"]);
            }
            else if (k == "execdone")
            {
                item.Kind = "execdone";
                item.IsError = o["isError"] != null && (bool)o["isError"];
            }
            else if (k == "turn")
            {
                item.Kind = "turn";
                item.Phase = AsString(o["phase"]);
            }
            else
            {
                return;   // step/start, step/end and anything unknown are noise on this page
            }

            lock (_lock)
            {
                if (item.Seq <= 0) item.Seq = _seq;
                if (item.Seq > _seq) _seq = item.Seq;
                item.Seq = _seq++;
                _items.Add(item);
                _lines++;
                _lastLineAt = NowMs();
                _link = "attached";
                _tailerNote = "";
                while (_items.Count > _maxItems)
                {
                    _items.RemoveAt(0);
                    _dropped++;
                }
            }
        }

        private static double NowMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static string AsString(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }

        private static double AsDouble(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return (double)t; }
            catch (Exception) { return 0; }
        }

        private static long AsLong(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return (long)t; }
            catch (Exception) { return 0; }
        }

        /// <summary>Called from the frame loop. Deliberately does nothing but keep the
        /// snapshot age honest; all I/O belongs to the reader thread.</summary>
        internal static void Tick()
        {
            // Kept as a hook: a future version may refresh link health here on the main
            // thread. Today anything it would compute is already computed off-thread.
        }

        public static string AgentName
        {
            get { lock (_lock) { return _agentName; } }
        }

        public static string AgentCall
        {
            get { lock (_lock) { return _agentCall; } }
        }

        /// <summary>Snapshot for the window: a copy, so drawing never holds the lock while
        /// the reader is mid-append.</summary>
        internal static List<CommsItem> Snapshot()
        {
            lock (_lock)
            {
                RefreshWorldLocked();
                return new List<CommsItem>(_items);
            }
        }

        internal static List<CommsOutgoing> Outgoing()
        {
            lock (_lock) { return new List<CommsOutgoing>(_out); }
        }

        /// <summary>Player line: queued to the outbox and echoed immediately. It is NOT
        /// reported as delivered - delivery is whatever consumes the outbox.</summary>
        public static JObject Send(JObject req)
        {
            string text = req == null ? null : (string)req["text"];
            if (text == null) text = req == null ? null : (string)req["message"];
            if (text != null) text = text.Trim();
            if (string.IsNullOrEmpty(text))
                throw new ApiException(400, "missing_field", "comms send needs a non-empty 'text'", false);
            int max = 4000;
            bool truncated = false;
            if (text.Length > max) { text = text.Substring(0, max); truncated = true; }

            string target = (string)req["target"] ?? "dsh";
            if (target != "dsh" && target != "codex")
                throw new ApiException(400, "invalid_target", "comms target must be dsh or codex", false);

            CommsOutgoing msg = new CommsOutgoing();
            msg.At = NowMs();
            msg.Text = text;
            msg.Target = target;

            string path;
            string world;
            lock (_lock)
            {
                RefreshWorldLocked();
                world = WorldKey();
                if (_perWorld && target == "dsh" && world == null)
                    throw new ApiException(409, "no_mission", "Wait for the mission to load", true);
                msg.Id = _outSeq++;
                _out.Add(msg);
                while (_out.Count > 200) _out.RemoveAt(0);
                path = _outboxPath;
                if (string.IsNullOrEmpty(path))
                {
                    // Attach() only runs inside the game. Resolve on demand so an offline
                    // caller can still prove the line reached the outbox.
                    string modDir = Core.ModDir();
                    if (!string.IsNullOrEmpty(modDir))
                    {
                        try
                        {
                            // Same file Attach() uses: <mod>\comms\outbox.jsonl. A fallback that
                            // invented <mod>\comms\run\outbox.jsonl quietly handed the helper an
                            // empty directory and the player's line never left the game folder.
                            string commsDir = Path.Combine(modDir, "comms");
                            Directory.CreateDirectory(commsDir);
                            path = Path.Combine(commsDir, "outbox.jsonl");
                            _outboxPath = path;
                        }
                        catch (Exception) { }
                    }
                }
                _items.Add(new CommsItem
                {
                    Seq = _seq++,
                    Kind = "user",
                    Text = text,
                    At = msg.At,
                    OutgoingId = msg.Id,
                    Target = target
                });
            }

            if (target == "codex" && path != null)
                path = Path.Combine(Path.GetDirectoryName(path), "codex.outbox.jsonl");
            string writeError = null;
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    JObject line = new JObject();
                    line["id"] = msg.Id;
                    line["at"] = msg.At;
                    line["text"] = text;
                    line["from"] = "player";
                    if (target == "dsh" && world != null) line["world"] = world;
                    using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception ex)
                {
                    writeError = ex.GetType().Name + ": " + ex.Message;
                }
            }
            else
            {
                writeError = "outbox path unknown (mod folder not resolved)";
            }

            JObject o = new JObject();
            o["id"] = msg.Id;
            o["text"] = text;
            o["truncated"] = truncated;
            o["target"] = target;
            o["queued"] = writeError == null;
            // Three states can never be collapsed into one word: the message exists, it was
            // written to the outbox, and it has NOT been delivered to the harness yet.
            o["state"] = writeError == null ? "queued_for_harness" : "not_written";
            if (writeError != null) o["error"] = writeError;
            o["next"] = Path.GetFileName(path);
            return o;
        }

        public static JObject Clear()
        {
            lock (_lock)
            {
                _items.Clear();
                _out.Clear();
            }
            JObject o = new JObject();
            o["cleared"] = true;
            return o;
        }

        internal static JArray Page(long since, int limit)
        {
            if (limit <= 0) limit = 200;
            if (limit > 1000) limit = 1000;
            JArray arr = new JArray();
            lock (_lock)
            {
                int start = 0;
                if (since > 0)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].Seq > since) { start = i; break; }
                        start = i + 1;
                    }
                }
                for (int i = start; i < _items.Count && arr.Count < limit; i++)
                {
                    CommsItem it = _items[i];
                    JObject o = new JObject();
                    o["seq"] = it.Seq;
                    o["kind"] = it.Kind;
                    if (it.Phase.Length > 0) o["phase"] = it.Phase;
                    if (it.Text.Length > 0) o["text"] = it.Text;
                    if (it.Tool.Length > 0) o["tool"] = it.Tool;
                    if (it.Kind == "execdone") o["isError"] = it.IsError;
                    o["at"] = it.At;
                    arr.Add(o);
                }
            }
            return arr;
        }

        /// <summary>Link, submission and generation are three different questions and the
        /// page must be able to show them apart.</summary>
        public static JObject State()
        {
            JObject o = new JObject();
            lock (_lock)
            {
                RefreshWorldLocked();
                o["worldKey"] = WorldKey();
                o["link"] = _link;
                if (_tailerNote.Length > 0) o["linkNote"] = _tailerNote;
                o["sessionId"] = _sessionId;
                o["workspaceSlug"] = _sessionSlug;
                o["agentName"] = _agentName;
                o["agentCall"] = _agentCall;
                o["items"] = _items.Count;
                o["dropped"] = _dropped;
                o["linesSeen"] = _lines;
                o["pendingOutbox"] = _out.Count;
                o["lastEventAgeMs"] = _lastLineAt <= 0 ? -1 : (long)(NowMs() - _lastLineAt);
                o["attachedAgeMs"] = _lastAttachAt <= 0 ? -1 : (long)(NowMs() - _lastAttachAt);
                if (_lastError.Length > 0) o["lastError"] = _lastError;
                o["liveFile"] = _livePath;
                o["outboxFile"] = _outboxPath;
                JObject hs = HoldState();
                o["hold"] = hs["hold"];
                o["holdSource"] = hs["holdSource"];

                // Generation: what the transcript says the agent is doing right now.
                string gen = "idle";
                string lastTool = "";
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    CommsItem it = _items[i];
                    if (it.Kind == "turn")
                    {
                        gen = it.Phase == "start" ? "working" : "idle";
                        break;
                    }
                    if (it.Kind == "exec") { gen = "executing"; lastTool = it.Tool; break; }
                    if (it.Kind == "execdone") { gen = "working"; break; }
                    if (it.Kind == "assistant") { gen = "working"; break; }
                }
                o["generation"] = gen;
                if (lastTool.Length > 0) o["lastTool"] = lastTool;
            }
            return o;
        }

        /// <summary>ops under /comms. 'send' is the only one that writes anything.</summary>
        public static JObject Dispatch(JObject req)
        {
            string op = req == null ? null : (string)req["op"];
            if (string.IsNullOrEmpty(op)) throw new ApiException(400, "missing_field", "comms needs an 'op'", false);
            if (op == "send") return Send(req);
            if (op == "clear") return Clear();
            if (op == "page")
            {
                long since = 0;
                int limit = 200;
                try { if (req["since"] != null) since = (long)req["since"]; }
                catch (Exception) { }
                try { if (req["limit"] != null) limit = (int)req["limit"]; }
                catch (Exception) { }
                JObject o = new JObject();
                o["items"] = Page(since, limit);
                return o;
            }
            throw new ApiException(404, "not_found", "unknown comms op: " + op
                + " (expected send, page or clear)", false);
        }

        // ---------------------------------------------------------------- setting.ini
        private static string Setting(string key, string dflt)
        {
            try
            {
                string dir = Core.ModDir();
                if (string.IsNullOrEmpty(dir)) return dflt;
                string path = Path.Combine(dir, "setting.ini");
                if (!File.Exists(path)) return dflt;
                string section = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        section = close > 1 ? line.Substring(1, close - 1).Trim() : null;
                        continue;
                    }
                    if (section == null) continue;
                    if (!string.Equals(section, Section, StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = line.Substring(eq + 1).Trim();
                    return v.Length == 0 ? dflt : v;
                }
            }
            catch (Exception) { }
            return dflt;
        }

        private static int SettingInt(string key, int dflt, int min, int max)
        {
            string raw = Setting(key, null);
            if (string.IsNullOrEmpty(raw)) return dflt;
            int v;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return dflt;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
