using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// Layer 1 evidence producer. Two duties only: produce completion evidence reliably, and
    /// hand it to the file channel that layer 2 already reads. It does NOT run work orders, decide
    /// outcomes, or talk to the player - that state machine lives in the upper layer's code.
    ///
    /// Sampling: the unit and the field reader are resolved ONCE at registration; every tick only
    /// reads the (object, field) pairs some rule subscribed to, one read per pair, shared by every
    /// rule on that pair. No rules means no work. Scene unit count does not change this cost.
    ///
    /// Evidence: mode "state" fires when the predicate holds now (a destroyed target is destroyed
    /// at once); mode "edge" fires when the value differs from the reading taken at registration.
    /// A read failure is its own kind. unreadable/missing/unknown/expired are never evidence.
    /// once: freeze the event, deliver it, then retire. A failed append retries the SAME event_id.
    /// </summary>
    internal static class Watch
    {
        public const int MaxRules = 64;
        private const double SampleIntervalSeconds = 0.5;

        private static readonly object Gate = new object();
        private static readonly List<Rule> Rules = new List<Rule>();
        private static int _seq;
        private static long _eventSeq;
        private static double _lastSample = -1.0;
        private static string _genStamp = "unknown";

        private sealed class Reader
        {
            public FieldInfo Field;
            public PropertyInfo Property;
            public bool Exists { get { return Field != null || Property != null; } }
            public object Read(object holder)
            {
                try
                {
                    if (holder == null) return null;
                    if (Field != null) return Field.GetValue(holder);
                    if (Property != null) return Property.GetValue(holder, null);
                }
                catch (Exception) { }
                return null;
            }
        }

        private sealed class Rule
        {
            public string WatchId;
            public string UnitKey;
            public object Unit;
            public string WorldGeneration;
            public Reader Reader;
            public string Field;
            public string Mode;
            public string Op;
            public bool HasWant;
            public double Want;
            public string WantRaw;
            public bool Once;
            public double CreatedAt;
            public double ExpiresAt;
            public int Hits;
            public bool PrevHit;
            public bool HadBaseline;
            public string BaselineRaw;
            public bool Frozen;
            public string FrozenJson;
            public string Removed;
            public string LastValueRaw;
        }

        public static bool Matches(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path;
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            p = p.TrimEnd('/');
            return p.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/watch/", StringComparison.OrdinalIgnoreCase);
        }

        public static HttpResult Dispatch(string method, string path, string body)
        {
            string m = (method ?? "GET").ToUpperInvariant();
            string p = path ?? "";
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            p = p.TrimEnd('/');
            JObject req = null;
            if (!string.IsNullOrEmpty(body))
            {
                try { req = JObject.Parse(body); }
                catch (Exception ex)
                {
                    return Envelope(false, null, "bad_json", "body is not a JSON object: " + ex.Message, false, 400);
                }
            }
            try
            {
                JObject data;
                lock (Gate)
                {
                    if (p.Equals("/watch", StringComparison.OrdinalIgnoreCase))
                    {
                        if (m == "POST") data = Create(req ?? new JObject());
                        else if (m == "GET") data = List();
                        else return Envelope(false, null, "method_not_allowed", m + " /watch", false, 405);
                    }
                    else
                    {
                        string id = p.Substring("/watch/".Length).Trim('/');
                        if (m == "GET") data = One(id);
                        else if (m == "DELETE") data = Remove(id);
                        else return Envelope(false, null, "method_not_allowed", m + " /watch/{id}", false, 405);
                    }
                }
                return Envelope(true, data, null, null, false, 200);
            }
            catch (ApiException ex)
            {
                return Envelope(false, null, ex.Code, ex.Message, ex.Retryable, ex.Status);
            }
            catch (Exception ex)
            {
                Core.Log("watch route failed: " + ex.Message);
                return Envelope(false, null, "watch_failed", ex.Message, false, 500);
            }
        }

        private static HttpResult Envelope(bool ok, JToken data, string code, string message, bool retryable, int status)
        {
            JObject o = new JObject();
            o["ok"] = ok;
            if (data != null) o["data"] = data;
            if (!ok)
            {
                JObject e = new JObject();
                e["code"] = code ?? "error";
                e["message"] = message ?? code;
                e["retryable"] = retryable;
                o["error"] = e;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(o.ToString(Formatting.None));
            return new HttpResult(status, bytes, "application/json");
        }

        // ------------------------------------------------------------ registration

        private static JObject Create(JObject req)
        {
            if (Rules.Count >= MaxRules)
                throw new ApiException(409, "watch_limit", "at most " + MaxRules + " triggers; delete some first", false);

            string unitKey = ReqText(req, "unit_id");
            if (string.IsNullOrEmpty(unitKey)) unitKey = ReqText(req, "unit");
            if (string.IsNullOrEmpty(unitKey)) unitKey = ReqText(req, "unique_id");
            if (string.IsNullOrEmpty(unitKey))
                throw new ApiException(400, "unit_id_required", "pass the ObjectBase.UniqueID of the unit", false);

            string field = ReqText(req, "field");
            if (string.IsNullOrEmpty(field))
                throw new ApiException(400, "field_required", "pass the field to read, e.g. \"IsDestroyed\"", false);

            string mode = (ReqText(req, "mode") ?? "state").ToLowerInvariant();
            if (mode != "state" && mode != "edge")
                throw new ApiException(400, "bad_mode", "mode is \"state\" or \"edge\"", false);

            string op = (ReqText(req, "op") ?? (mode == "edge" ? "changed" : "eq")).ToLowerInvariant();
            if (op != "eq" && op != "ne" && op != "gt" && op != "lt" && op != "exists" && op != "changed")
                throw new ApiException(400, "bad_op", "op is one of eq, ne, gt, lt, exists, changed", false);
            if (mode == "edge") op = "changed";

            object unit = ResolveUnit(unitKey);
            if (unit == null)
                throw new ApiException(404, "unit_not_found", "no unit with UniqueID " + unitKey, false);

            Reader reader = ResolveReader(unit, field);
            if (!reader.Exists)
                throw new ApiException(400, "field_unreadable", "no field or property \"" + field + "\" on " + unit.GetType().Name, false);

            Rule r = new Rule();
            r.WatchId = "w" + (++_seq).ToString(CultureInfo.InvariantCulture);
            r.UnitKey = unitKey;
            r.Unit = unit;
            r.Field = field;
            r.Reader = reader;
            r.Mode = mode;
            r.Op = op;
            r.CreatedAt = Now;
            r.WorldGeneration = CurrentGeneration();

            double ttl = TryNumber(req, "ttl_s", 0.0);
            r.ExpiresAt = ttl > 0 ? r.CreatedAt + ttl : 0.0;
            JToken onceTok = req["once"];
            if (onceTok != null && onceTok.Type != JTokenType.Null)
            {
                if (onceTok.Type == JTokenType.Boolean) r.Once = (bool)onceTok;
                else
                {
                    bool parsed;
                    r.Once = bool.TryParse(onceTok.ToString(), out parsed) && parsed;
                }
            }
            if (req["value"] != null)
            {
                r.HasWant = true;
                r.WantRaw = req["value"].ToString();
                r.Want = TryNumber(req, "value", double.NaN);
            }

            object baseline = reader.Read(unit);
            r.HadBaseline = baseline != null;
            r.BaselineRaw = AsText(baseline);
            r.LastValueRaw = r.BaselineRaw;

            Rules.Add(r);
            JObject data = Describe(r);
            if (mode == "state")
            {
                bool satisfied;
                string raw;
                bool comparable = Evaluate(r, out satisfied, out raw);
                data["satisfied_now"] = comparable && satisfied;
                if (raw != null) data["reading"] = raw;
                if (comparable && satisfied)
                {
                    r.PrevHit = true;
                    HitsAndMaybeFreeze(r, "hit", raw);
                }
            }
            else
            {
                data["baseline"] = r.BaselineRaw;
                data["baseline_known"] = r.HadBaseline;
            }
            return data;
        }

        private static object ResolveUnit(string unitKey)
        {
            int wanted;
            bool numeric = int.TryParse(unitKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted);
            foreach (object o in Api.AllObjectsForWatch())
            {
                if (o == null) continue;
                object id = GameReflect.Member(o, "UniqueID");
                if (id == null) id = GameReflect.Member(o, "UniqueId");
                if (id == null) id = GameReflect.Member(o, "uniqueID");
                if (id == null) continue;
                if (string.Equals(AsText(id), unitKey, StringComparison.Ordinal)) return o;
                if (numeric)
                {
                    double n = AsNumber(id);
                    if (!double.IsNaN(n) && Math.Abs(n - wanted) < 0.0001) return o;
                }
            }
            return null;
        }

        private static Reader ResolveReader(object unit, string field)
        {
            Reader r = new Reader();
            Type t = unit.GetType();
            while (t != null && !r.Exists)
            {
                r.Field = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (r.Field == null)
                {
                    PropertyInfo p = t.GetProperty(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) r.Property = p;
                }
                t = t.BaseType;
            }
            return r;
        }

        // ------------------------------------------------------------ sampling

        /// <summary>Called from Bridge.Update on the main thread.</summary>
        public static void Tick()
        {
            lock (Gate)
            {
                if (Rules.Count == 0) return;
                double now = Now;
                if (_lastSample >= 0 && now - _lastSample < SampleIntervalSeconds)
                {
                    RetryFrozen();
                    return;
                }
                _lastSample = now;
                _genStamp = CurrentGeneration();

                Dictionary<string, string> shared = new Dictionary<string, string>();
                Dictionary<string, bool> readable = new Dictionary<string, bool>();

                for (int i = Rules.Count - 1; i >= 0; i--)
                {
                    Rule r = Rules[i];
                    if (r.Frozen)
                    {
                        if (DeliverFrozen(r)) Rules.RemoveAt(i);
                        continue;
                    }
                    if (r.ExpiresAt > 0 && now >= r.ExpiresAt)
                    {
                        Emit(r, "expired", "deadline reached", r.LastValueRaw);
                        Rules.RemoveAt(i);
                        continue;
                    }
                    if (!string.Equals(r.WorldGeneration, _genStamp, StringComparison.Ordinal))
                    {
                        Emit(r, "expired", "the world was rebuilt; the observation ended with generation " + r.WorldGeneration, r.LastValueRaw);
                        Rules.RemoveAt(i);
                        continue;
                    }
                    if (!ObjectStillBound(r))
                    {
                        Emit(r, "expired", "the bound object is gone; nothing is re-bound by id", r.LastValueRaw);
                        Rules.RemoveAt(i);
                        continue;
                    }

                    string key = r.UnitKey + "|" + r.Field;
                    bool ok;
                    string raw;
                    if (shared.ContainsKey(key))
                    {
                        raw = shared[key];
                        ok = readable[key];
                    }
                    else
                    {
                        object v = r.Reader.Read(r.Unit);
                        ok = v != null || r.Reader.Exists;
                        raw = AsText(v);
                        shared[key] = raw;
                        readable[key] = ok;
                    }
                    if (!ok)
                    {
                        Emit(r, "unreadable", "the field could not be read", null);
                        continue;
                    }
                    r.LastValueRaw = raw;

                    bool hit;
                    if (!Evaluate(r, out hit, out raw))
                    {
                        Emit(r, "unreadable", "the value is not comparable to what was asked", raw);
                        continue;
                    }
                    if (!hit) { r.PrevHit = false; continue; }

                    // Report the transition, not the state: a predicate that stays true must not
                    // append a line every sample.
                    bool rising = r.Mode == "edge" || !r.PrevHit;
                    r.PrevHit = true;
                    if (!rising) continue;
                    if (r.Mode == "edge") r.BaselineRaw = raw;
                    HitsAndMaybeFreeze(r, r.Mode == "edge" ? "changed" : "hit", raw);
                    if (r.Frozen && DeliverFrozen(r)) Rules.RemoveAt(i);
                }
            }
        }

        private static void RetryFrozen()
        {
            for (int i = Rules.Count - 1; i >= 0; i--)
            {
                if (!Rules[i].Frozen) continue;
                if (DeliverFrozen(Rules[i])) Rules.RemoveAt(i);
            }
        }

        private static bool ObjectStillBound(Rule r)
        {
            if (r.Unit == null) return false;
            object id = GameReflect.Member(r.Unit, "UniqueID");
            if (id == null) id = GameReflect.Member(r.Unit, "UniqueId");
            if (id == null) id = GameReflect.Member(r.Unit, "uniqueID");
            if (id == null) return false;
            return string.Equals(AsText(id), r.UnitKey, StringComparison.Ordinal);
        }

        /// <summary>False only when the very thing asked about cannot be compared.</summary>
        private static bool Evaluate(Rule r, out bool hit, out string raw)
        {
            hit = false;
            object value = r.Reader.Read(r.Unit);
            raw = AsText(value);

            if (r.Mode == "edge")
            {
                if (value == null) hit = r.HadBaseline;
                else hit = !string.Equals(raw, r.BaselineRaw, StringComparison.Ordinal);
                return true;
            }

            if (r.Op == "exists")
            {
                hit = value != null;
                return true;
            }
            if (value == null) return false;

            double have = AsNumber(value);
            if (!r.HasWant) return false;
            if (double.IsNaN(have) || double.IsNaN(r.Want))
            {
                if (r.Op == "gt" || r.Op == "lt") return false;
                bool same = string.Equals(raw, r.WantRaw, StringComparison.OrdinalIgnoreCase);
                hit = r.Op == "eq" ? same : !same;
                return true;
            }
            if (r.Op == "eq") hit = Math.Abs(have - r.Want) < 0.0001;
            else if (r.Op == "ne") hit = Math.Abs(have - r.Want) >= 0.0001;
            else if (r.Op == "gt") hit = have > r.Want;
            else if (r.Op == "lt") hit = have < r.Want;
            return true;
        }

        private static void HitsAndMaybeFreeze(Rule r, string kind, string raw)
        {
            r.Hits++;
            if (!r.Once)
            {
                Emit(r, kind, null, raw);
                return;
            }
            r.Frozen = true;
            r.FrozenJson = EventJson(r, kind, null, raw);
        }

        private static bool DeliverFrozen(Rule r)
        {
            if (r.FrozenJson == null) return false;
            if (!AppendOutbox(r.FrozenJson)) return false;
            r.FrozenJson = null;
            r.Frozen = false;
            r.Removed = "delivered";
            return true;
        }

        private static void Emit(Rule r, string kind, string reason, string raw)
        {
            AppendOutbox(EventJson(r, kind, reason, raw));
        }

        private static string EventJson(Rule r, string kind, string reason, string raw)
        {
            JObject e = new JObject();
            e["k"] = "event";
            e["from"] = "trigger";
            e["event_id"] = "e" + (++_eventSeq).ToString(CultureInfo.InvariantCulture);
            e["watch_id"] = r.WatchId;
            e["kind"] = kind;
            e["evidence"] = kind == "hit" || kind == "changed";
            e["unit_id"] = r.UnitKey;
            e["field"] = r.Field;
            e["mode"] = r.Mode;
            e["op"] = r.Op;
            if (r.HasWant) e["want"] = r.WantRaw;
            if (raw != null) e["value"] = raw;
            if (r.Mode == "edge" && r.BaselineRaw != null) e["baseline"] = r.BaselineRaw;
            e["world_generation"] = r.WorldGeneration;
            e["hits"] = r.Hits;
            e["at"] = Now;
            if (reason != null) e["reason"] = reason;
            return e.ToString(Formatting.None);
        }

        private static bool AppendOutbox(string line)
        {
            try
            {
                string dir = Path.Combine(Core.ModDir(), "comms");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "outbox.jsonl");
                using (FileStream fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.Write(line);
                        w.Write('\n');
                        w.Flush();
                    }
                    fs.Flush(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("watch outbox append failed: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------ queries

        private static JObject List()
        {
            JObject o = new JObject();
            o["count"] = Rules.Count;
            o["max"] = MaxRules;
            o["sample_interval_s"] = SampleIntervalSeconds;
            o["world_generation"] = _genStamp;
            JArray arr = new JArray();
            foreach (Rule r in Rules) arr.Add(Describe(r));
            o["rules"] = arr;
            JObject sem = new JObject();
            sem["evidence_kinds"] = new JArray(new object[] { "hit", "changed" });
            sem["not_evidence"] = new JArray(new object[] { "unreadable", "missing", "unknown", "expired" });
            o["semantics"] = sem;
            return o;
        }

        private static JObject One(string id)
        {
            foreach (Rule r in Rules)
                if (string.Equals(r.WatchId, id, StringComparison.OrdinalIgnoreCase)) return Describe(r);
            throw new ApiException(404, "watch_not_found", "no trigger " + id, false);
        }

        private static JObject Remove(string id)
        {
            for (int i = 0; i < Rules.Count; i++)
            {
                if (!string.Equals(Rules[i].WatchId, id, StringComparison.OrdinalIgnoreCase)) continue;
                Rule r = Rules[i];
                Rules.RemoveAt(i);
                JObject o = Describe(r);
                o["removed"] = "by operator";
                return o;
            }
            throw new ApiException(404, "watch_not_found", "no trigger " + id, false);
        }

        private static JObject Describe(Rule r)
        {
            JObject o = new JObject();
            o["watch_id"] = r.WatchId;
            o["unit_id"] = r.UnitKey;
            o["field"] = r.Field;
            o["mode"] = r.Mode;
            o["op"] = r.Op;
            if (r.HasWant) o["want"] = r.WantRaw;
            o["once"] = r.Once;
            o["hits"] = r.Hits;
            o["created_at"] = r.CreatedAt;
            o["expires_at"] = r.ExpiresAt > 0 ? (JToken)r.ExpiresAt : JValue.CreateNull();
            o["world_generation"] = r.WorldGeneration;
            o["reader"] = r.Reader.Exists ? (JToken)"member" : JValue.CreateNull();
            o["frozen"] = r.Frozen;
            if (r.Removed != null) o["removed"] = r.Removed;
            return o;
        }

        // ------------------------------------------------------------ helpers

        private static double Now
        {
            get { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        }

        private static string CurrentGeneration()
        {
            try { return Api.WorldGenerationForWatch(); }
            catch (Exception) { return "unknown"; }
        }

        private static string ReqText(JObject o, string name)
        {
            if (o == null) return null;
            JToken t = o[name];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.ToString();
        }

        private static double TryNumber(JObject o, string name, double fallback)
        {
            if (o == null) return fallback;
            JToken t = o[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            double d;
            if (double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        private static string AsText(object v)
        {
            if (v == null) return null;
            IConvertible c = v as IConvertible;
            if (c != null)
            {
                try { return c.ToString(CultureInfo.InvariantCulture); }
                catch (Exception) { }
            }
            return v.ToString();
        }

        private static double AsNumber(object v)
        {
            if (v == null) return double.NaN;
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is bool) return ((bool)v) ? 1 : 0;
            string s = AsText(v);
            double d;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return double.NaN;
        }
    }
}
