// Sea Power Agent Bridge - entry point + main-thread dispatcher.
// Compiled with C# 5 (framework csc.exe). No string interpolation, no ?., no nameof.
//
// REBUILT: a patch script destroyed this file (PowerShell does not treat \" as an escape,
// so the replacement string was cut short and the file was rewritten with the script's own
// error line). It is reconstructed against the surface the rest of the mod calls, and the
// compiler plus the static/dynamic gates are the proof that the surface is right. Lesson
// recorded in docs/HARNESS_COMMS.md: C# edits go through a Python script with explicit
// occurrence counts, never through an inline PowerShell string.
//
// The two rules this file exists to enforce, in order:
//
//   1. Unity and Noesis APIs may only be touched on the main thread. The first live
//      session proved it the hard way: a background thread read Application.unityVersion
//      and the process exited with "Graphics device is null." Because that failure is
//      unrecoverable, EVERY route is marshalled through OnMainThread rather than trusted
//      to remember.
//
//   2. A main-thread call that times out must say whether a retry is safe. An item
//      cancelled while still pending provably never touched the game; an item that had
//      already started may have fired a missile. Collapsing those into one "timeout" is
//      how a caller ends up double-shooting.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using AnchorChain;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    [ACPlugin("dsh.spmcp.bridge", "Sea Power Agent Bridge", "0.1.0", new string[0], new string[0])]
    public class BridgePlugin : IAnchorChainMod
    {
        public void TriggerEntryPoint()
        {
            Core.Log("TriggerEntryPoint()");
            try
            {
                GameObject go = new GameObject("SPMCP_Bridge");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<BridgeBehaviour>();
            }
            catch (Exception ex)
            {
                Core.Log("FATAL TriggerEntryPoint: " + ex);
            }
        }
    }

    public class BridgeBehaviour : MonoBehaviour
    {
        // Kept false until the socket is actually bound. A previous game process can
        // still hold the port for several seconds after a restart, and a single failed
        // Bind() would otherwise leave the bridge dead for the whole session.
        private bool _listening;
        private float _lastTry = -10f;
        private int _tries;
        private float _lastSwitchPoll = -10f;
        private float _lastProbePoll = -10f;

        void Awake()
        {
            // Claimed here, before anything else can run. Start() binds the HTTP port, and
            // the first request (or the dsh-type-index warm-up thread) arrives long before
            // the first Update(). Whoever called OnMainThread first used to decide the
            // identity of "the main thread" permanently: an HTTP worker winning that race
            // made every off_main_thread guard pass on a pool thread, and any other
            // background thread winning it made every OnMain route answer 500. Awake only
            // ever runs on the real main thread, so this is the one honest place to say so.
            Core.MainThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                Core.CoreIdentity = typeof(Core).Assembly.Location + "#" + typeof(Core).Assembly.GetName().Version
                    + "@" + DateTime.Now.ToString("HH:mm:ss.fff");
            }
            catch (Exception) { }
            Core.Log("BridgeBehaviour.Awake (main thread " + Core.MainThreadId + ")");
            // Main thread: the one place these Unity properties may be read for /ping.
            try
            {
                Core.UnityVersion = UnityEngine.Application.unityVersion;
                Core.SceneName = UnityEngine.Application.loadedLevelName;
            }
            catch (Exception) { }
        }

        void Start()
        {
            Shots.Attach(this);
            ApplyRuntimeSwitches();
            // The type index walks 475 assemblies, which took the cold start from instant
            // to ~20 s when it ran on the first request. Building it on a background thread
            // at load is the round-5 fix; keep it here so a miss never pays that walk.
            try
            {
                Thread warm = new Thread(delegate ()
                {
                    try { GameReflect.BuildIndex(); }
                    catch (Exception e) { Core.Log("type index warm-up failed: " + e.GetType().Name); }
                });
                warm.IsBackground = true;
                warm.Name = "dsh-type-index";
                warm.Start();
            }
            catch (Exception e)
            {
                Core.Log("type index thread failed to start: " + e.GetType().Name);
            }
            // TR2-H: the comms page's reader thread. Started here rather than in Awake
            // because it resolves the mod folder, which Start guarantees. It runs before the
            // port opens because Attach also restores a persisted HOLD: the bridge must not
            // accept one request while the operator's hold is still being read back.
            try { CommsBridge.Attach(); }
            catch (Exception e) { Core.Log("comms attach failed: " + e.GetType().Name); }
            TryStart();
        }

        /// <summary>
        /// Reads the diagnostic switch from disk so it can be toggled without a rebuild.
        /// Presence of "diagnostics.on" in the mod folder enables /__raw and /__d1..3.
        /// </summary>
        private void ApplyRuntimeSwitches()
        {
            try
            {
                string flag = Path.Combine(Core.ModDir(), "diagnostics.on");
                bool on = File.Exists(flag);
                if (on != MiniHttp.DiagnosticsEnabled)
                {
                    MiniHttp.DiagnosticsEnabled = on;
                    Core.Log("diagnostics " + (on ? "ENABLED" : "disabled"));
                }
            }
            catch (Exception e)
            {
                Core.Log("ApplyRuntimeSwitches failed: " + e.Message);
            }
        }

        private void TryStart()
        {
            if (_listening) return;
            float now = Time.realtimeSinceStartup;
            if (_tries > 0 && now - _lastTry < 3f) return;
            _lastTry = now;
            _tries++;
            try
            {
                MiniHttp.SetHandler(Api.Dispatch);
                MiniHttp.Start(Core.Port);
                _listening = true;
                Core.Log("bridge listening on http://127.0.0.1:" + Core.Port + " (attempt " + _tries + ")");
            }
            catch (Exception e)
            {
                Core.Log("MiniHttp.Start failed (attempt " + _tries + "): " + e.Message);
            }
        }

        void Update()
        {
            Core.Pump(6);
            TryStart();

            // Screenshot queue: starts the end-of-frame grab once per requested capture.
            // Main thread here, which is the only place StartCoroutine may be called.
            try { Watch.Tick(); } catch (Exception e) { Core.Log("watch tick: " + e.Message); }
            try { Shots.Tick(); }
                        catch (Exception) { }

            // T04 / TR2-S: the session registry samples the world continuously instead of
            // only when /probe/session is polled - a savegame reload between two polls was
            // otherwise invisible, and that is the vector T04 exists to answer.
            try { Api.SessionTick(); }
            catch (Exception) { }

            // ACT-004: mirror what the player typed into the panel's TextBox.
            try { Api.UiTick(); }
            catch (Exception) { }

            // ACT-010: the inspector window's hotkey, polled rather than routed (see
            // Api.InspectorKeyTick for why the UI key path was not usable).
            try { Api.InspectorKeyTick(); }
            catch (Exception) { }

            // Re-read the diagnostic switch roughly once a second. Without this the
            // "toggle diagnostics.on without a restart" promise in the README is false:
            // Awake/Start fire once per session, so the file was only ever read at boot.
            float now = Time.realtimeSinceStartup;
            if (now - _lastProbePoll >= 0.5f)
            {
                _lastProbePoll = now;
                // Cheap main-thread refresh so /ping can answer from memory only.
                try
                {
                    Core.SceneName = UnityEngine.Application.loadedLevelName;
                    Core.InGame = global::SeaPower.GameMain.Instance != null;
                }
                catch (Exception) { }
            }
            if (now - _lastSwitchPoll >= 1.0f)
            {
                _lastSwitchPoll = now;
                ApplyRuntimeSwitches();
            }
        }

        void OnDestroy()
        {
            Core.Log("BridgeBehaviour.OnDestroy");
            try { CommsBridge.Stop(); }
            catch (Exception) { }
            Core.Shutdown();
        }
    }

    /// <summary>
    /// Thrown when the main thread did not answer in time. Safe tells the caller whether a
    /// retry is safe: true only when the item was cancelled while still pending, so it
    /// provably never touched the game.
    /// </summary>
    public sealed class MainThreadTimeoutException : Exception
    {
        public readonly bool Safe;

        public MainThreadTimeoutException(string message, bool safe)
            : base(message)
        {
            Safe = safe;
        }
    }

    /// <summary>
    /// The mod's small runtime: identity of the main thread, the mod folder, the log, and
    /// the queue every Unity touch is funnelled through.
    /// </summary>
    public static class Core
    {
        public static readonly int Port = 19081;

        public const int MaxQueue = 128;

        private static readonly object _logLock = new object();
        private static string _logPath;
        private static int _logFailures;
        /// <summary>
        /// The thread Unity calls Update on, or -1 before anyone claimed it. A FIELD on
        /// purpose: tests/dynamic_tests.cs claims this identity with FieldInfo.SetValue to
        /// make Dispatch run inline, and the original bridge exposed it the same way.
        /// </summary>
        public static int MainThreadId = -1;

        private static readonly BlockingCollection<Work> _queue =
            new BlockingCollection<Work>(new ConcurrentQueue<Work>(), MaxQueue);

        // Diagnostics only: enough to tell "the pump never runs" apart from "the pump runs in
        // another assembly copy" without guessing. Read by GET /diag, which Dispatch serves
        // inline (no main-thread hop) for exactly that reason.
        internal static int PumpTicks;
        internal static int PumpTook;
        internal static string LastPumpAt = "";
        internal static string CoreIdentity = "";

        internal static int QueueDepth
        {
            get
            {
                try { return _queue.Count; }
                catch (Exception) { return -1; }
            }
        }

        private static long _seq;

        // /ping must never touch Unity. It is a background-thread handler (one of the two
        // routes Dispatch serves without the main-thread hop), and reading Application.unityVersion /
        // loadedLevelName / GameMain.Instance from there is exactly what killed the process
        // intermittently on startup with "Graphics device is null." - the same failure the
        // header comment of this file records. These three fields are refreshed on the main
        // thread instead, so the probe stays a pure memory read.
        internal static string UnityVersion = "unknown";
        internal static string SceneName = "";
        internal static bool InGame;

        /// <summary>
        /// Claims the main thread on first use. Called from the read paths rather than from a
        /// property, so the field can stay writable for the offline suite.
        /// </summary>
        private static void EnsureClaim()
        {
            // Exactly the -1 sentinel, not any negative value: the offline suite parks a
            // value like -12345 here to say "this caller is NOT the main thread", and
            // claiming over it would silently turn the queueing path into an inline call.
            if (MainThreadId == -1) MainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>The one check that keeps a nested dispatch from deadlocking on itself.</summary>
        public static bool IsMain
        {
            get
            {
                EnsureClaim();
                return Thread.CurrentThread.ManagedThreadId == MainThreadId;
            }
        }

        public static string ModDir()
        {
            try
            {
                // The AnchorChain loader puts every mod in
                // <Game>_Data/StreamingAssets/<mod folder>/, and the assembly lives in that
                // same folder, so the assembly's own location IS the mod folder.
                string asm = typeof(Core).Assembly.Location;
                if (!string.IsNullOrEmpty(asm)) return Path.GetDirectoryName(asm);
            }
            catch (Exception) { }
            try
            {
                return Path.Combine(Application.dataPath, "StreamingAssets", "dsh-spmcp");
            }
            catch (Exception) { }
            // Never null: every caller does Path.Combine(ModDir(), ...) and a null here used
            // to turn a read-only route into a 500 with no hint of the real cause.
            try { return System.Environment.CurrentDirectory; }
            catch (Exception) { }
            return ".";
        }

        public static void Log(string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\n";
                lock (_logLock)
                {
                    if (_logPath == null)
                    {
                        string dir = ModDir();
                        if (dir == null) return;
                        _logPath = Path.Combine(dir, "bridge.log");
                    }
                    // Third failure means the folder is not writable; retrying every frame
                    // would be an exception storm, so the counter stops it.
                    if (_logFailures >= 3) return;
                    try
                    {
                        File.AppendAllText(_logPath, line, Encoding.UTF8);
                        _logFailures = 0;
                    }
                    catch (Exception)
                    {
                        _logFailures++;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Runs fn on the main thread and returns its result.
        ///
        /// On the main thread itself this is a direct call: queueing there would wait for a
        /// pump that can only run once this frame ends, which is a deadlock, not a wait.
        /// </summary>
        public static object OnMainThread(Func<object> fn, int timeoutMs)
        {
            if (fn == null) return null;
            if (IsMain) return fn();

            int wait = timeoutMs <= 0 ? 20000 : timeoutMs;
            Work w = new Work();
            w.Fn = fn;
            w.Id = Interlocked.Increment(ref _seq);
            try
            {
                _queue.Add(w);
            }
            catch (InvalidOperationException)
            {
                throw new Exception("main-thread queue is full (" + MaxQueue + " items)");
            }

            bool done = w.Done.Wait(wait);
            if (!done)
            {
                // The exchange decides which side of the line the item was on when the
                // caller gave up. Pending -> Cancelled promises it never ran. Anything
                // else (already running) may already have touched the game.
                if (w.State.CompareExchange((int)WorkState.Cancelled, (int)WorkState.Pending) == (int)WorkState.Pending)
                    throw new MainThreadTimeoutException(
                        "main thread did not pick up request #" + w.Id + " within " + wait
                        + " ms; it was cancelled while pending, so it never ran", true);

                w.Done.Wait(50);
                // It ran on the main thread; if the answer landed inside the grace window,
                // return it instead of calling the outcome unknown. Reporting a timeout for
                // work that demonstrably completed would invite a retry that executes a
                // second time, which is the exact hazard this class exists to prevent.
                if (w.State.Current() == (int)WorkState.Done)
                {
                    if (w.Error != null) throw w.Error;
                    return w.Result;
                }
                throw new MainThreadTimeoutException(
                    "main thread started request #" + w.Id + " but did not finish within "
                    + wait + " ms; whether it took effect is unknown", false);
            }

            if (w.Error != null) throw w.Error;
            return w.Result;
        }

        /// <summary>
        /// Drains up to budget queued items. Called from Update once per frame, on the main
        /// thread.
        /// </summary>
        public static void Pump(int budget)
        {
            if (budget <= 0) budget = 1;
            PumpTicks++;
            for (int i = 0; i < budget; i++)
            {
                Work w;
                if (!_queue.TryTake(out w)) return;
                PumpTook++;
                try { LastPumpAt = DateTime.Now.ToString("HH:mm:ss.fff"); }
                catch (Exception) { }
                if (w == null) continue;

                // B17: an item whose state is no longer Pending is finished here, whatever
                // the reason. The bug this guards against is a pump that leaves a dead
                // head entry in place and spins on it forever.
                if (w.State.CompareExchange((int)WorkState.Running, (int)WorkState.Pending) != (int)WorkState.Pending)
                {
                    w.State.TrySet((int)WorkState.Expired);
                    try { w.Done.Set(); }
                    catch (Exception) { }
                    continue;
                }

                try
                {
                    w.Result = w.Fn();
                }
                catch (Exception ex)
                {
                    w.Error = ex;
                }
                finally
                {
                    w.State.TrySet((int)WorkState.Done);
                    try { w.Done.Set(); }
                    catch (Exception) { }
                }
            }
        }

        public static void Shutdown()
        {
            try { MiniHttp.Stop(); }
            catch (Exception) { }
            // Waking queued callers matters more than waiting for them: each one is blocked
            // on a thread the game is about to take away.
            try
            {
                Work w;
                while (_queue.TryTake(out w))
                {
                    if (w == null) continue;
                    w.State.TrySet((int)WorkState.Expired);
                    try { w.Done.Set(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    internal enum WorkState
    {
        Pending = 0,
        Running = 1,
        Done = 2,
        Cancelled = 3,
        Expired = 4
    }

    /// <summary>One marshalled call. Holds the caller's answer until it is collected.</summary>
    internal sealed class Work
    {
        public long Id;
        public Func<object> Fn;
        public object Result;
        public Exception Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public readonly AtomicEnum State = new AtomicEnum();
    }

    /// <summary>
    /// A one-word state cell with compare-exchange. Hand-rolled so every transition reads
    /// the same way and a lost race can be told apart from a won one.
    /// </summary>
    internal sealed class AtomicEnum
    {
        private const int ExpiredFlag = 0x100;
        private int _value;

        /// <summary>
        /// Moves Pending to <paramref name="next"/>. The return value is the state this call
        /// observed, which is what makes the timeout verdict trustworthy: only a caller that
        /// saw Pending and won the exchange may promise "it never ran".
        /// </summary>
        public int CompareExchange(int next, int expected)
        {
            int prev = Interlocked.CompareExchange(ref _value, next, expected);
            return prev;
        }

        /// <summary>
        /// The current state, flag bits removed. TrySet stores `next | ExpiredFlag`, so a
        /// finished item reads 258 rather than 2 - comparing the raw value to the enum
        /// silently never matches.
        /// </summary>
        public int Current()
        {
            return CompareExchange(0, 0) & 0xFF;
        }

        /// <summary>Terminal set that never relabels an earlier verdict.</summary>
        public bool TrySet(int next)
        {
            while (true)
            {
                int cur = Volatile.Read(ref _value);
                if ((cur & ExpiredFlag) != 0) return false;
                if (Interlocked.CompareExchange(ref _value, next | ExpiredFlag, cur) == cur) return true;
            }
        }
    }
}
