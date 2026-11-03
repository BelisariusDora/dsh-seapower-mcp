using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    /// <summary>Native triggers for layer 1.
    ///
    /// The upper layer registers "when this unit satisfies this condition, tell me". The rule is
    /// evaluated on the existing main-thread tick (0.5 s) and every hit is written to the
    /// CommsBridge outbox as a k:"event" line, which is the one path that already reaches the
    /// harness with locks, cursors and a tri-state delivery record.
    ///
    /// Deliberately NOT here:
    ///  - no intel gate (layer 1 answers with truth; the policy layer belongs above it);
    ///  - no HOLD check (watching is not commanding);
    ///  - no persistence (a trigger is temporary by request: ttl_s + MaxRules + once).
    ///
    /// The one rule that is not negotiable: a condition whose truth cannot be read answers
    /// "unknown". Folding unknown into true or false is the lie this project keeps paying for.
    /// </summary>
    internal static class Watch
    {
        private sealed class Rule
        {
            public int Id;
            public string UnitId;
            public string Field;
            public string Op;      // eq | ne | gt | lt | exists | changes | crosses
            public string Value;   // JSON literal as text
            public bool Once;
            public int Hits;
            public string CreatedUtc;
            public double ExpiresAt;   // unix seconds; 0 = no expiry
            public bool Removed;
            public string LastValue;
            public string WorldGeneration;
            public bool PrevHit;
            public bool PrevKnown;
        }

        public const int MaxRules = 64;
        private static readonly object Gate = new object();
        private static readonly List<Rule> Rules = new List<Rule>();
        private static int _seq;

        public static bool Matches(string path)
        {
            if (path == null) return false;
            return path == "/watch" || path.StartsWith("/watch/", StringComparison.Ordinal);
        }

        /// <summary>Off-main-thread entry: parse, then evaluate registrations on the main thread.</summary>
        public static HttpResult Dispatch(string method, string path, string body)
        {
            try
            {
                JObject parsed = null;
                if (!string.IsNullOrEmpty(body))
                {
                    try { parsed = JObject.Parse(body); }
                    catch (Exception e)
                    {
                        return Envelope(false, null, "bad_json", "the request body is not JSON: " + e.Message, false, 400);
                    }
                }
                if (!Core.IsMain)
                {
                    object raw = Core.OnMainThread(delegate { return Handle(method, path, parsed); }, 8000);
                    JObject res = raw as JObject;
                    if (res == null) return Envelope(false, null, "bad_reply", "the watch handler returned nothing", false, 500);
                    return new HttpResult(200, Encoding.UTF8.GetBytes(res.ToString(Formatting.None)), "application/json");
                }
                JObject direct = Handle(method, path, parsed);
                return new HttpResult(200, Encoding.UTF8.GetBytes(direct.ToString(Formatting.None)), "application/json");
            }
            catch (MainThreadTimeoutException e)
            {
                return Envelope(false, null, "main_thread_timeout",
                    "the game did not answer within 8000 ms; whether the registration happened is unknown: " + e.Message,
                    e.Safe, 504);
            }
            catch (Exception e)
            {
                return Envelope(false, null, "route_error", e.GetType().Name + ": " + e.Message, false, 500);
            }
        }

        private static HttpResult Envelope(bool ok, JToken data, string code, string message, bool retryable, int status)
        {
            JObject o = new JObject();
            o["ok"] = ok;
            if (data != null) o["data"] = data;
            if (!ok)
            {
                JObject err = new JObject();
                err["code"] = code;
                err["message"] = message;
                err["retryable"] = retryable;
                o["error"] = err;
            }
            return new HttpResult(status, Encoding.UTF8.GetBytes(o.ToString(Formatting.None)), "application/json");
        }

        /// <summary>Main-thread handler for the four watch routes.</summary>
        private static JObject Handle(string method, string path, JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "watch routes touch game objects", false);

            string tail = path.Length > "/watch".Length ? path.Substring("/watch".Length).Trim('/') : "";
            if (method == "GET" && tail.Length == 0) return List();
            if (method == "POST" && tail.Length == 0) return Create(req ?? new JObject());
            if (method == "DELETE" && tail.Length > 0) return Remove(tail);
            if (method == "GET" && tail.Length > 0) return One(tail);
            return Fail("method_not_allowed", method + " " + path + " is not a watch route", false);
        }

        private static JObject Create(JObject req)
        {
            string unitId = ReqText(req, "unit_id");
            if (unitId.Length == 0) return Fail("missing_unit_id", "unit_id is required", false);
            string field = ReqText(req, "field");
            if (field.Length == 0) return Fail("missing_field", "field is required (a property or field name on the unit)", false);
            string op = ReqText(req, "op");
            if (op.Length == 0) op = "eq";
            op = op.ToLowerInvariant();
            if (op != "eq" && op != "ne" && op != "gt" && op != "lt" && op != "exists" && op != "changes" && op != "crosses")
                return Fail("unknown_op", "op must be eq|ne|gt|lt|exists|changes|crosses", false);

            int ttl = 0;
            if (req["ttl_s"] != null) ttl = (int)req["ttl_s"];
            bool once = req["once"] != null && req["once"].Value<bool>();

            // Resolve once now so a typo answers immediately instead of never firing.
            object unit = FindUnit(unitId);
            if (unit == null) return Fail("unit_not_found", "no unit with id " + unitId, false);
            string why;
            string probe = ReadAsText(unit, field, out why);
            if (why.Length > 0 && op != "exists")
                return Fail("field_unreadable", "'" + field + "' cannot be read on that unit: " + why, false);

            Rule r = new Rule();
            r.UnitId = unitId;
            r.Field = field;
            r.Op = op;
            r.Value = req["value"] != null ? req["value"].ToString(Formatting.None) : null;
            r.Once = once;
            r.ExpiresAt = ttl > 0 ? NowSeconds() + ttl : 0;
            r.CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            r.LastValue = probe;
            r.WorldGeneration = SafeString(delegate { return Api.WorldGenerationForWatch(); }, "unknown");
            lock (Gate)
            {
                if (Rules.Count >= MaxRules) return Fail("watch_limit", "at most " + MaxRules + " triggers; delete some first", false);
                r.Id = ++_seq;
                Rules.Add(r);
            }
            Core.Log("watch +" + r.Id + " unit " + unitId + " " + field + " " + op + " " + (r.Value ?? "") + (once ? " once" : ""));
            JObject d = Describe(r);
            d["probe"] = probe;
            d["probeKnown"] = why.Length == 0;
            return Ok(d);
        }

        private static JObject List()
        {
            JArray arr = new JArray();
            lock (Gate)
            {
                foreach (Rule r in Rules) if (!r.Removed) arr.Add(Describe(r));
            }
            JObject d = new JObject();
            d["count"] = arr.Count;
            d["max"] = MaxRules;
            d["triggers"] = arr;
            return Ok(d);
        }

        private static JObject One(string tail)
        {
            int id;
            if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return Fail("bad_id", "watch id must be a number", false);
            lock (Gate)
            {
                foreach (Rule r in Rules)
                    if (r.Id == id && !r.Removed) return Ok(Describe(r));
            }
            return Fail("watch_not_found", "no trigger with id " + id, false);
        }

        private static JObject Remove(string tail)
        {
            int id;
            if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return Fail("bad_id", "watch id must be a number", false);
            lock (Gate)
            {
                foreach (Rule r in Rules)
                {
                    if (r.Id != id || r.Removed) continue;
                    r.Removed = true;
                    Core.Log("watch -" + id);
                    JObject d = Describe(r);
                    d["removed"] = true;
                    return Ok(d);
                }
            }
            return Fail("watch_not_found", "no trigger with id " + id, false);
        }

        /// <summary>Called from the main-thread tick. Never throws: a trigger that breaks the
        /// frame loop is worse than a trigger that misses a beat.</summary>
        public static void Tick()
        {
            try
            {
                if (!Core.IsMain) return;
                double now = NowSeconds();
                if (now - _lastEval < EvalIntervalSeconds) return;
                _lastEval = now;
                List<Rule> due = null;
                string gen = CurrentGeneration();
                lock (Gate)
                {
                    for (int i = Rules.Count - 1; i >= 0; i--)
                    {
                        Rule r = Rules[i];
                        if (r.Removed) { Rules.RemoveAt(i); continue; }
                        if (r.WorldGeneration != "unknown" && gen != "unknown" &&
                            !string.Equals(r.WorldGeneration, gen, StringComparison.Ordinal))
                        {
                            // Every id in this rule now means a different object.
                            r.Removed = true;
                            Rules.RemoveAt(i);
                            Emit(r, "expired", null, "the world was rebuilt (" + r.WorldGeneration + " -> " + gen + ")");
                            continue;
                        }
                        if (r.ExpiresAt > 0 && now > r.ExpiresAt)
                        {
                            r.Removed = true;
                            Rules.RemoveAt(i);
                            Emit(r, "expired", null, "ttl elapsed");
                            continue;
                        }
                        if (due == null) due = new List<Rule>();
                        due.Add(r);
                    }
                }
                if (due == null) return;

                // Evaluate outside the lock: reflection can be slow, and a rule cannot be
                // removed between the copy and the read.
                for (int i = 0; i < due.Count; i++)
                {
                    Rule r = due[i];
                    object unit = FindUnit(r.UnitId);
                    if (unit == null)
                    {
                        Fire(r, "missing", null, "the unit object is gone");
                        continue;
                    }
                    string why;
                    string nowText = ReadAsText(unit, r.Field, out why);
                    if (why.Length > 0)
                    {
                        // Not "no" and not "yes": say it could not be read, once per transition.
                        if (r.LastValue != "?unknown") Fire(r, "unknown", null, why);
                        r.LastValue = "?unknown";
                        continue;
                    }
                    if (r.Op == "changes")
                    {
                        if (r.LastValue != null && r.LastValue != nowText) Fire(r, "changed", nowText, null);
                        r.LastValue = nowText;
                        continue;
                    }
                    bool hit;
                    if (!Compare(r, nowText, out hit))
                    {
                        r.LastValue = nowText;
                        continue;
                    }
                    // Edge triggered: a permanent eq rule used to append one outbox line per
                    // frame while it stayed true, which is both a log flood and real main-thread
                    // cost (reflection + file append inside the frame loop).
                    if (hit && !r.PrevHit) Fire(r, "hit", nowText, null);
                    r.PrevHit = hit;
                    r.LastValue = nowText;
                }
            }
            catch (Exception e)
            {
                Core.Log("watch tick failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Compare(Rule r, string now, out bool hit)
        {
            hit = false;
            if (r.Op == "exists") { hit = now != null; return true; }
            string want = r.Value;
            if (want != null && want.Length >= 2 && want[0] == '"') want = want.Substring(1, want.Length - 2);
            if (r.Op == "eq") { hit = Same(now, want); return true; }
            if (r.Op == "ne") { hit = !Same(now, want); return true; }
            if (r.Op == "gt" || r.Op == "lt" || r.Op == "crosses")
            {
                double a, b;
                if (!TryNumber(now, out a) || !TryNumber(want, out b)) return false;
                if (r.Op == "gt") { hit = a > b; return true; }
                if (r.Op == "lt") { hit = a < b; return true; }
                double prev;
                bool had = TryNumber(r.LastValue, out prev);
                hit = had && ((prev < b && a >= b) || (prev > b && a <= b));
                return true;
            }
            return false;
        }

        private static bool Same(string a, string b)
        {
            if (a == null || b == null) return a == b;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            double x, y;
            if (TryNumber(a, out x) && TryNumber(b, out y)) return Math.Abs(x - y) < 1e-9;
            return false;
        }

        private static bool TryNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static void Fire(Rule r, string kind, string now, string why)
        {
            bool wasHit = kind == "hit" || kind == "changed";
            if (wasHit) r.Hits++;
            bool delivered = Emit(r, kind, now, why);
            // Retiring a once-rule whose line never reached the outbox loses the only chance to
            // report it. Keep it and try again next evaluation.
            if (r.Once && wasHit && delivered)
            {
                r.Removed = true;
                lock (Gate) Rules.Remove(r);
            }
            if (r.Once && (kind == "missing" || kind == "unknown")) r.Removed = true;
        }

        /// <summary>One line into the outbox the harness already polls. k:"event" keeps trigger
        /// traffic apart from the player's chat, which the comms panel paints verbatim.</summary>
        private static bool Emit(Rule r, string kind, string now, string why)
        {
            try
            {
                JObject e = new JObject();
                e["k"] = "event";
                e["watchId"] = r.Id;
                e["kind"] = kind;
                e["unitId"] = r.UnitId;
                e["field"] = r.Field;
                e["op"] = r.Op;
                e["want"] = r.Value;
                e["now"] = now;
                if (why != null) e["why"] = why;
                e["hits"] = r.Hits;
                e["at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                e["from"] = "trigger";
                AppendOutbox(e.ToString(Formatting.None));
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("watch emit failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>The outbox is the harness-facing queue; harness_link.js reads it line by line
        /// and has the lock, the byte cursor and the tri-state delivery record. Reusing it means a
        /// trigger inherits that honesty instead of inventing a second, quieter channel.</summary>
        private static void AppendOutbox(string line)
        {
            string dir = System.IO.Path.Combine(Core.ModDir(), "comms");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "outbox.jsonl");
            System.IO.File.AppendAllText(file, line + System.Environment.NewLine, Encoding.UTF8);
        }

        private static JObject Describe(Rule r)
        {
            JObject o = new JObject();
            o["watch_id"] = r.Id;
            o["unit_id"] = r.UnitId;
            o["field"] = r.Field;
            o["op"] = r.Op;
            o["value"] = r.Value;
            o["once"] = r.Once;
            o["hits"] = r.Hits;
            o["created"] = r.CreatedUtc;
            o["expires_at_unix"] = r.ExpiresAt > 0 ? (JToken)r.ExpiresAt : JValue.CreateNull();
            o["last_value"] = r.LastValue;
            o["world_generation"] = r.WorldGeneration;
            return o;
        }

        private static JObject Ok(JToken data)
        {
            JObject o = new JObject();
            o["ok"] = true;
            o["data"] = data;
            return o;
        }

        private static JObject Fail(string code, string message, bool retryable)
        {
            JObject o = new JObject();
            o["ok"] = false;
            JObject err = new JObject();
            err["code"] = code;
            err["message"] = message;
            err["retryable"] = retryable;
            o["error"] = err;
            return o;
        }

        private static string ReqText(JObject req, string name)
        {
            JToken t = req == null ? null : req[name];
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString().Trim();
        }

        private static double NowSeconds()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string SafeString(Func<string> f, string fallback)
        {
            try { return f() ?? fallback; } catch (Exception) { return fallback; }
        }

        /// <summary>Find a unit by id without any intel policy: layer 1 answers in truth and the
        /// caller above decides what a player is allowed to see.</summary>
        private static object FindUnit(string unitId)
        {
            int want;
            if (!int.TryParse(unitId, NumberStyles.Integer, CultureInfo.InvariantCulture, out want)) return null;
            foreach (object o in Api.AllObjectsForWatch())
            {
                if (o == null) continue;
                foreach (string name in new string[] { "UniqueID", "UniqueId", "uniqueID", "uniqueId", "UnitId", "unitId", "_uniqueId", "_uniqueID" })
                {
                    object v = GameReflect.Member(o, name);
                    if (v == null) continue;
                    int id;
                    try { id = Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                    catch (Exception) { continue; }
                    if (id == want) return o;
                }
            }
            return null;
        }

        private static bool MemberExists(object unit, string name)
        {
            System.Reflection.BindingFlags f = System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            Type t = unit.GetType();
            while (t != null && t != typeof(object))
            {
                if (t.GetField(name, f) != null) return true;
                if (t.GetProperty(name, f) != null) return true;
                t = t.BaseType;
            }
            return false;
        }

        private static string CurrentGeneration()
        {
            return SafeString(delegate { return Api.WorldGenerationForWatch(); }, "unknown");
        }

        /// <summary>Evaluation cadence. Every frame is needless: the world moves at human speed
        /// and each rule costs a reflection read (+ a file append when it fires).</summary>
        private const double EvalIntervalSeconds = 0.2;
        private static double _lastEval;

        private static string ReadAsText(object unit, string field, out string why)
        {
            why = "";
            if (unit == null) { why = "the unit object is gone"; return null; }
            try
            {
                object v = GameReflect.Member(unit, field);
                if (v == null)
                {
                    // Property, then field, then the two boolean spellings the game uses.
                    foreach (string alt in new string[] { field.ToLowerInvariant(), "is" + field, "_" + field, field + "Flag" })
                    {
                        v = GameReflect.Member(unit, alt);
                        if (v != null) break;
                    }
                }
                if (v == null)
                {
                    // exists must answer yes and changes must see the null -> value edge; a
                    // missing member is the only thing that is genuinely unreadable.
                    if (MemberExists(unit, field)) return "null";
                    why = "no readable member named '" + field + "'";
                    return null;
                }
                if (v is bool) return ((bool)v) ? "true" : "false";
                if (v is string) return (string)v;
                IFormattable f = v as IFormattable;
                if (f != null) return f.ToString(null, CultureInfo.InvariantCulture);
                return v.ToString();
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }
}
