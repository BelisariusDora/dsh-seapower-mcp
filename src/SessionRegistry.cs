// Sea Power Agent Bridge - T04 / TR2-S: session boundary registry.
// Compiled with C# 5 (framework csc.exe). No string interpolation, no ?., no nameof.
//
// Why this file exists, and why it is split in two:
//
//   SessionBoundary  is a PURE function on plain values. It is the half that can be
//                    proven offline: tests/dynamic_tests.cs feeds it synthetic samples
//                    for the V1-V6 vectors and checks the verdict. Nothing in it touches
//                    Unity, so an offline pass is honest evidence about the logic.
//
//   SessionRegistry  is the stateful half: it keeps a ring of samples taken from the
//                    live game and decides what world generation you are in. Its offline
//                    behaviour proves bookkeeping only - the *signals* it reads can only
//                    be validated at runtime, which is exactly what T04's runtime gate is
//                    for. Passing dynamic tests must never be read as "reload detection
//                    works in game".
//
// The old /probe/session only sampled while it was being polled, so a savegame reload
// between two polls was simply invisible. The registry is ticked from the main thread
// every Update (throttled to 0.5 s) so the event cannot slip between requests.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// One observation of the world. Plain fields on purpose: this is a data carrier,
    /// and the offline fixtures build it directly.
    /// </summary>
    public sealed class SessionSample
    {
        public string TakenAt;

        public bool ClockReadable;
        public double MissionSessionTime;
        public double MissionElapsedTime;
        public double SimulationTick;
        public double TimeCompression;
        public bool Paused;

        public string SceneName;

        public int ObjectCount;
        public int UnitCount;
        public int MaxUid;
        public int MinUid;

        /// <summary>True when the next sample would actually distinguish something.</summary>
        public bool MateriallyEquals(SessionSample o)
        {
            if (o == null) return false;
            if (!SameText(SceneName, o.SceneName)) return false;
            if (ObjectCount != o.ObjectCount) return false;
            if (UnitCount != o.UnitCount) return false;
            if (MaxUid != o.MaxUid) return false;
            if (MinUid != o.MinUid) return false;
            if (ClockReadable != o.ClockReadable) return false;
            // The clock always advances while unpaused, so it cannot be part of "material".
            // A regression is caught by the classifier, not by de-duplication.
            return true;
        }

        private static bool SameText(string a, string b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The pure half. Every method here is a function of its arguments and nothing else,
    /// which is what lets the offline suite test it without a game.
    /// </summary>
    public static class SessionBoundary
    {
        public const string SceneChanged = "scene_changed";
        public const string ClockRegression = "mission_session_time_went_backwards";
        public const string WorldAppeared = "world_appeared_from_empty";
        public const string WorldVanished = "world_vanished_to_empty";
        public const string UidShrank = "max_uid_shrank";
        public const string NoSignal = "";

        /// <summary>
        /// Classifies one transition. Empty string means "no boundary signal".
        /// Deliberately takes primitives: the offline harness calls it by reflection with
        /// boxed values, and a struct parameter would make that call site fragile.
        /// </summary>
        public static string Classify(
            bool prevValid,
            string prevScene, string curScene,
            double prevSession, double curSession,
            int prevObjects, int curObjects,
            int prevMaxUid, int curMaxUid)
        {
            if (!prevValid) return NoSignal;

            // ---- tier 1: definite. Only these are allowed to bump the generation.
            if (!SameText(prevScene, curScene)) return SceneChanged;

            // A savegame reload rewinds the mission clock. Pausing (V3) and changing time
            // compression (V6) never move it backwards, so this cannot fire on those.
            if (prevSession > 0.0 && curSession > 0.0 && curSession < prevSession - 0.5)
                return ClockRegression;

            if (prevObjects == 0 && curObjects > 0) return WorldAppeared;
            if (prevObjects > 0 && curObjects == 0) return WorldVanished;

            // ---- tier 2: suspect only. UniqueID comes from an instance counter, so a new
            // world restarts it low - but losing the highest-numbered ships in an existing
            // world moves the maximum down too. Never proof on its own.
            if (prevMaxUid > 0 && curMaxUid > 0 && curMaxUid < prevMaxUid - 10) return UidShrank;

            return NoSignal;
        }

        public static bool IsDefinite(string code)
        {
            return code == SceneChanged || code == ClockRegression
                || code == WorldAppeared || code == WorldVanished;
        }

        public static bool IsSuspect(string code)
        {
            return code == UidShrank;
        }

        private static bool SameText(string a, string b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The stateful half. Two states matter to callers:
    ///
    ///   WorldGeneration  increments on every definite rebuild signal. Anything the bridge
    ///                    cached against the old world (unit ids, order handles) belongs to
    ///                    the previous generation and must not be trusted afterwards.
    ///
    ///   BoundaryState    "no_world" | "watching" | "rebuild_seen" | "verified".
    ///                    The write gate refuses only during "rebuild_seen" - the window
    ///                    where the world provably changed and the new one is not confirmed
    ///                    yet. It does NOT refuse merely because the classifier has never
    ///                    seen a rebuild; that would block every fresh session and teach
    ///                    people to turn the gate off.
    /// </summary>
    public static class SessionRegistry
    {
        public const int RingMax = 512;
        /// <summary>Consecutive stable samples needed to leave "rebuild_seen".</summary>
        public const int VerifySamples = 3;

        public const string StateNoWorld = "no_world";
        public const string StateWatching = "watching";
        public const string StateRebuildSeen = "rebuild_seen";
        public const string StateVerified = "verified";

        private static readonly List<SessionSample> _ring = new List<SessionSample>();
        private static readonly List<JObject> _marks = new List<JObject>();

        // Offer() runs on the Unity main thread (it is ticked from Update) while Report()
        // runs on an HTTP worker thread. Without this lock a request landing exactly on a
        // sample would enumerate a List that is being appended to, which throws
        // "Collection was modified" out of the dispatcher - a 500 on a read-only route,
        // from a race, with no reproduction short of loading a save at the wrong moment.
        // The sections are short and never nest, so this cannot deadlock with the gate.
        private static readonly object _lock = new object();

        private static SessionSample _last;
        private static int _gen;
        private static string _state = StateNoWorld;
        private static string _lastReason = SessionBoundary.NoSignal;
        private static string _lastSuspect = SessionBoundary.NoSignal;
        private static int _stableCount;
        private static int _offered;
        private static int _dropped;

        public static int WorldGeneration { get { return _gen; } }
        public static string BoundaryState { get { return _state; } }

        /// <summary>True only while a definite rebuild is waiting to be confirmed.</summary>
        public static bool GateWouldRefuse
        {
            get { return _state == StateRebuildSeen; }
        }

        /// <summary>
        /// Opt-in switch, read from disk on every call so it can be toggled without a
        /// rebuild and cannot drift out of sync with what /session reports. Default is
        /// OFF: the classifier's runtime signals are still unverified, and a gate that
        /// blocks writes on an unproven classifier is worse than no gate.
        /// </summary>
        public static bool GateEnabled()
        {
            try { return File.Exists(Path.Combine(Core.ModDir(), "session_gate.on")); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The single write choke point, called from Route(). Refuses only inside the
        /// unconfirmed-rebuild window, and only when the gate is switched on.
        /// </summary>
        public static void RequireStable(string what)
        {
            if (!GateEnabled()) return;
            if (_state != StateRebuildSeen) return;
            throw new ApiException(409, "session_boundary_unverified",
                "the world generation changed (" + _lastReason + ") and the new world is not confirmed yet; "
                + "refusing " + what + ". Poll GET /session until boundaryState is not '" + StateRebuildSeen
                + "', then re-read the target ids - ids from the previous world may be reused.", false);
        }

        /// <summary>Drops every cached observation. Used by tests; harmless in game.</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                ResetInner();
            }
        }

        private static void ResetInner()
        {
            _ring.Clear();
            _marks.Clear();
            _last = null;
            _gen = 0;
            _state = StateNoWorld;
            _lastReason = SessionBoundary.NoSignal;
            _lastSuspect = SessionBoundary.NoSignal;
            _stableCount = 0;
            _offered = 0;
            _dropped = 0;
        }

        /// <summary>
        /// Feeds one observation. Main thread only (the caller reads Unity state).
        /// </summary>
        public static void Offer(SessionSample cur)
        {
            if (cur == null) return;
            // Serialised against Report()/Reset(): see the comment on _lock.
            lock (_lock) { OfferInner(cur); }
        }

        private static void OfferInner(SessionSample cur)
        {
            _offered++;

            // De-duplicate: a 60 fps tick would otherwise fill the ring with identical rows
            // and the V3 (pause) / V6 (time compression) vectors would be unreadable.
            if (_last != null && _last.MateriallyEquals(cur))
            {
                _dropped++;
                return;
            }

            string reason = SessionBoundary.Classify(
                _last != null,
                _last == null ? null : _last.SceneName, cur.SceneName,
                _last == null ? 0.0 : _last.MissionSessionTime, cur.MissionSessionTime,
                _last == null ? 0 : _last.ObjectCount, cur.ObjectCount,
                _last == null ? 0 : _last.MaxUid, cur.MaxUid);

            if (SessionBoundary.IsDefinite(reason))
            {
                _gen++;
                _state = StateRebuildSeen;
                _lastReason = reason;
                _stableCount = 0;
                JObject m = new JObject();
                m["generation"] = _gen;
                m["reason"] = reason;
                m["at"] = cur.TakenAt;
                m["fromScene"] = _last == null ? null : _last.SceneName;
                m["toScene"] = cur.SceneName;
                m["fromObjects"] = _last == null ? 0 : _last.ObjectCount;
                m["toObjects"] = cur.ObjectCount;
                m["fromMaxUid"] = _last == null ? 0 : _last.MaxUid;
                m["toMaxUid"] = cur.MaxUid;
                _marks.Add(m);
                if (_marks.Count > 64) _marks.RemoveAt(0);
            }
            else if (SessionBoundary.IsSuspect(reason))
            {
                // Recorded, never acted on. Losing the top-numbered hulls looks identical.
                _lastSuspect = reason;
            }

            bool hasWorld = cur.ObjectCount > 0;
            if (_state == StateRebuildSeen)
            {
                if (hasWorld)
                {
                    _stableCount++;
                    if (_stableCount >= VerifySamples) _state = StateVerified;
                }
            }
            else if (!hasWorld)
            {
                _state = StateNoWorld;
            }
            else if (_state == StateNoWorld)
            {
                _state = StateWatching;
            }

            _last = cur;
            _ring.Add(cur);
            if (_ring.Count > RingMax) _ring.RemoveAt(0);
        }

        /// <summary>Read-only snapshot for GET /session. Safe off the main thread.</summary>
        public static JObject Report()
        {
            lock (_lock) { return ReportInner(); }
        }

        private static JObject ReportInner()
        {
            JObject o = new JObject();
            o["worldGeneration"] = _gen;
            o["boundaryState"] = _state;
            o["gateEnabled"] = GateEnabled();
            o["gateRefuses"] = GateWouldRefuse;
            o["gateSwitch"] = "create session_gate.on in the mod folder to arm the write gate; while it is absent every write is allowed";
            o["lastRebuildSignal"] = _lastReason.Length == 0 ? null : _lastReason;
            o["lastSuspectSignal"] = _lastSuspect.Length == 0 ? null : _lastSuspect;
            o["stableSamplesSinceRebuild"] = _stableCount;
            o["samplesOffered"] = _offered;
            o["samplesDeduped"] = _dropped;
            o["ringCount"] = _ring.Count;
            o["ringCapacity"] = RingMax;
            o["verifySamples"] = VerifySamples;

            JObject c = null;
            if (_last != null) c = SampleJson(_last);
            o["current"] = c == null ? (JToken)JValue.CreateNull() : (JToken)c;

            JArray marks = new JArray();
            for (int i = 0; i < _marks.Count; i++) marks.Add(_marks[i]);
            o["marks"] = marks;

            JArray hist = new JArray();
            int from = _ring.Count - 12;
            if (from < 0) from = 0;
            for (int i = from; i < _ring.Count; i++) hist.Add(SampleJson(_ring[i]));
            o["history"] = hist;

            o["semantics"] = "worldGeneration bumps only on tier-1 signals (scene change, mission-clock regression, world appearing/vanishing). A shrinking maxUniqueId is reported as lastSuspectSignal and never bumps it. Read this before reusing any cached unit id.";
            o["runtimeUnverified"] = "The classifier logic is covered offline. Whether these signals actually change on a savegame reload (V4) is NOT yet established at runtime - do not arm the gate on the strength of the offline suite.";
            return o;
        }

        private static JObject SampleJson(SessionSample s)
        {
            JObject j = new JObject();
            j["takenAt"] = s.TakenAt;
            j["sceneName"] = s.SceneName;
            j["clockReadable"] = s.ClockReadable;
            j["missionSessionTime"] = s.MissionSessionTime;
            j["missionElapsedTime"] = s.MissionElapsedTime;
            j["simulationTick"] = s.SimulationTick;
            j["timeCompression"] = s.TimeCompression;
            j["paused"] = s.Paused;
            j["objectCount"] = s.ObjectCount;
            j["unitCount"] = s.UnitCount;
            j["maxUid"] = s.MaxUid;
            j["minUid"] = s.MinUid;
            return j;
        }
    }
}
