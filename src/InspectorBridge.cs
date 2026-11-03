// Agent Inspector core: bounded event buffer, INI settings, JSONL export.
//
// WHAT THIS FILE IS: the观察 side of the Agent Inspector (design change
// AGENT_INSPECTOR_DESIGN_CHANGE.md). It records what the bridge actually saw and
// said, and it can write a bounded JSONL export.
//
// WHAT THIS FILE IS NOT:
//   - It is not authorisation. Recording an event grants nothing; the T09 grant
//     check lives in the service layer.
//   - It is not a telemetry platform. Bounded ring buffer, one file on demand,
//     no database, no event bus, no background upload.
//   - It is not proof of game-level success. "HTTP 200" only means the bridge
//     answered; whether the game executed anything is a separate read-back that
//     the UI must not pretend to have.
//
// Deliberately free of UnityEngine so the offline suites can exercise the buffer,
// the INI merge and the redaction by reflection against the real DLL. The mod
// directory is passed in by the caller instead of being discovered here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// One observation. Field names follow the design change §4 list so the UI and
    /// the export do not invent a second vocabulary.
    /// </summary>
    public sealed class InspectorEvent
    {
        public string EventId;
        public string SessionId;
        public string StepId;
        public string CommandId;
        public string Timestamp;        // ISO-8601 UTC, display only
        public string FirstTimestamp;   // first occurrence when repeats are merged
        public string EventType;        // Input | Observation | Decision | ActionIssued | ActionResult | Error
        public string Source;           // bridge | service | mock
        public string Summary;
        public string InputPayload;
        public string OutputPayload;
        public string Status;
        public double DurationMs;
        public string ErrorMessage;
        public int Repeat;
        public bool Truncated;
        public int Bytes;
    }

    /// <summary>
    /// Parsed [AgentInspector] section. Every invalid value leaves <see cref="Diagnostics"/>
    /// behind and falls back to the documented default; nothing here throws.
    /// </summary>
    public sealed class InspectorConfig
    {
        public const string Section = "AgentInspector";

        public string ToggleKey = "N";
        // 78% of the viewport, centred. A first run used 62% and a maximise could
        // persist 0,0,1,1 - both were reported as "the window opens the wrong size",
        // so the default is now deliberately larger than comfortable but never full.
        public double Left = 0.11;
        public double Top = 0.11;
        public double Width = 0.78;
        public double Height = 0.78;
        public double SplitX = 0.45;
        public double SplitY = 0.6;
        // Text family for the inspector window. A config value the player can edit,
        // because "which family has CJK glyphs on this machine" is a local fact: the
        // bridge can only report what it resolved, it can not decide for them.
        public string Font = "Microsoft YaHei";
        // UI language for the inspector chrome: "" (follow the game), "en" or "zh"/"cn".
        // The value is carried here so Merge() can write it back: the section is
        // rewritten on every geometry save, and a hand-added Language= line that gets
        // deleted on the next launch looks exactly like a broken setting.
        public string Language = "";
        // Keep the original pixels on disk while still delivering the small picture.
        // Without this the saved file is exactly the bytes the model got, i.e. the
        // downscaled one - so "save the screenshot" quietly meant "save the 1024 copy".
        // Off by default: a 4K PNG is ~14 MB and callers do not expect a capture to
        // double its encoding cost unless they asked for it.
        public bool ShotSaveFull = false;
        public int MaxEvents = 2000;
        public int MaxBytes = 2000000;
        public int MaxPayloadChars = 4000;

        public List<string> Diagnostics = new List<string>();

        public JObject ToJson()
        {
            JObject o = new JObject();
            o["toggleKey"] = ToggleKey;
            o["geometry"] = new JArray(Left, Top, Width, Height);
            o["splitX"] = SplitX;
            o["splitY"] = SplitY;
            o["font"] = Font;
            o["language"] = Language;
            o["shotSaveFull"] = ShotSaveFull;
            o["maxEvents"] = MaxEvents;
            o["maxBytes"] = MaxBytes;
            o["maxPayloadChars"] = MaxPayloadChars;
            o["diagnostics"] = new JArray(Diagnostics.ToArray());
            return o;
        }

        public InspectorConfig Clone()
        {
            InspectorConfig c = (InspectorConfig)MemberwiseClone();
            c.Diagnostics = new List<string>(Diagnostics);
            return c;
        }
    }

    /// <summary>
    /// INI read/merge for the deployment folder. Merge keeps every line outside our own
    /// section byte for byte, so a user's comments and other sections survive an update.
    /// </summary>
    public static class InspectorSettings
    {
        public static string PathFor(string dir)
        {
            return System.IO.Path.Combine(dir, "setting.ini");
        }

        public static InspectorConfig Defaults()
        {
            return new InspectorConfig();
        }

        /// <summary>Pure parser: same behaviour offline and in game.</summary>
        public static InspectorConfig Parse(string text)
        {
            InspectorConfig c = new InspectorConfig();
            if (string.IsNullOrEmpty(text)) return c;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new char[] { '\n' });
            bool inSection = false;
            bool sectionSeen = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    string name = close > 1 ? line.Substring(1, close - 1).Trim() : "";
                    inSection = string.Equals(name, InspectorConfig.Section, StringComparison.OrdinalIgnoreCase);
                    if (inSection) sectionSeen = true;
                    continue;
                }
                if (!inSection) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                Apply(c, key, val, i + 1);
            }
            if (!sectionSeen)
                c.Diagnostics.Add("setting.ini has no [" + InspectorConfig.Section + "] section; using defaults");
            return c;
        }

        private static void Apply(InspectorConfig c, string key, string val, int lineNo)
        {
            string k = key.ToLowerInvariant();
            if (k == "togglekey")
            {
                string parsed;
                if (TryParseKey(val, out parsed)) c.ToggleKey = parsed;
                else c.Diagnostics.Add("line " + lineNo + ": ToggleKey '" + val + "' is not a single key name; using " + c.ToggleKey);
                return;
            }
            if (k == "geometry")
            {
                double[] nums;
                if (TryParseNumbers(val, 4, out nums))
                {
                    c.Left = nums[0]; c.Top = nums[1]; c.Width = nums[2]; c.Height = nums[3];
                    ClampGeometry(c, lineNo);
                }
                else c.Diagnostics.Add("line " + lineNo + ": Geometry needs four numbers 'left,top,width,height'; keeping defaults");
                return;
            }
            if (k == "splitx") { c.SplitX = ParseRatio(c, val, c.SplitX, lineNo, "SplitX"); return; }
            if (k == "splity") { c.SplitY = ParseRatio(c, val, c.SplitY, lineNo, "SplitY"); return; }
            if (k == "font")
            {
                if (string.IsNullOrEmpty(val)) c.Diagnostics.Add("line " + lineNo + ": Font is empty; keeping " + c.Font);
                else c.Font = val;
                return;
            }
            if (k == "language") { c.Language = val == null ? "" : val.Trim(); return; }
            if (k == "shotsavefull") { c.ShotSaveFull = ParseFlag(c, val, c.ShotSaveFull, lineNo, "ShotSaveFull"); return; }
            if (k == "maxevents") { c.MaxEvents = ParseInt(c, val, c.MaxEvents, 50, 20000, lineNo, "MaxEvents"); return; }
            if (k == "maxbytes") { c.MaxBytes = ParseInt(c, val, c.MaxBytes, 50000, 50000000, lineNo, "MaxBytes"); return; }
            if (k == "maxpayloadchars") { c.MaxPayloadChars = ParseInt(c, val, c.MaxPayloadChars, 200, 100000, lineNo, "MaxPayloadChars"); return; }
            // Unknown keys are not an error: other plugins may share this file.
        }

        private static void ClampGeometry(InspectorConfig c, int lineNo)
        {
            double[] v = new double[] { c.Left, c.Top, c.Width, c.Height };
            bool changed = false;
            for (int i = 0; i < 4; i++)
            {
                if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) { changed = true; v[i] = i < 2 ? 0.08 : 0.62; }
            }
            if (v[2] < 0.2) { v[2] = 0.2; changed = true; }
            if (v[3] < 0.2) { v[3] = 0.2; changed = true; }
            if (v[0] < 0) { v[0] = 0; changed = true; }
            if (v[1] < 0) { v[1] = 0; changed = true; }
            // Keep the window inside the viewport: fractions, so left+width must fit.
            if (v[0] + v[2] > 1.0) { v[0] = Math.Max(0.0, 1.0 - v[2]); changed = true; }
            if (v[1] + v[3] > 1.0) { v[1] = Math.Max(0.0, 1.0 - v[3]); changed = true; }
            if (changed)
            {
                c.Left = v[0]; c.Top = v[1]; c.Width = v[2]; c.Height = v[3];
                c.Diagnostics.Add("line " + lineNo + ": Geometry was outside the viewport or degenerate; clamped to "
                    + Describe(c.Left) + "," + Describe(c.Top) + "," + Describe(c.Width) + "," + Describe(c.Height));
            }
        }

        private static double ParseRatio(InspectorConfig c, string val, double dflt, int lineNo, string name)
        {
            double d;
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                && !double.IsNaN(d) && d > 0.05 && d < 0.95)
                return d;
            c.Diagnostics.Add("line " + lineNo + ": " + name + " '" + val + "' must be a fraction between 0.05 and 0.95; using " + Describe(dflt));
            return dflt;
        }

        private static int ParseInt(InspectorConfig c, string val, int dflt, int lo, int hi, int lineNo, string name)
        {
            int n;
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= lo && n <= hi)
                return n;
            c.Diagnostics.Add("line " + lineNo + ": " + name + " '" + val + "' must be an integer in [" + lo + "," + hi + "]; using " + dflt);
            return dflt;
        }

        /// <summary>true/false (any case), plus 1/0 and yes/no because people write those.</summary>
        private static bool ParseFlag(InspectorConfig c, string val, bool dflt, int lineNo, string name)
        {
            string s = val == null ? "" : val.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            c.Diagnostics.Add("line " + lineNo + ": " + name + " '" + val + "' must be true or false; using "
                + (dflt ? "true" : "false"));
            return dflt;
        }

        /// <summary>A key name is one printable token; anything fancier is rejected rather than guessed.</summary>
        public static bool TryParseKey(string val, out string parsed)
        {
            parsed = null;
            if (val == null) return false;
            string s = val.Trim();
            if (s.Length == 0 || s.Length > 24) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '/' || ch == '-';
                if (!ok) return false;
            }
            parsed = s;
            return true;
        }

        private static bool TryParseNumbers(string val, int count, out double[] nums)
        {
            nums = null;
            if (val == null) return false;
            string[] parts = val.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);   // (char[], options) exists in every corlib - unlike (char, options)
            if (parts.Length != count) return false;
            double[] d = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d[i])) return false;
            }
            nums = d;
            return true;
        }

        private static string Describe(double d)
        {
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static InspectorConfig Load(string dir)
        {
            string p = PathFor(dir);
            try
            {
                if (!File.Exists(p)) return Defaults();
                return Parse(File.ReadAllText(p, Encoding.UTF8));
            }
            catch (Exception)
            {
                InspectorConfig c = Defaults();
                c.Diagnostics.Add("setting.ini could not be read; using defaults");
                return c;
            }
        }

        /// <summary>
        /// Returns the file text with only our section replaced. Everything outside the
        /// section - including comments and other plugins' keys - is preserved verbatim.
        /// </summary>
        public static string Merge(string original, InspectorConfig cfg)
        {
            string nl = "\n";
            string src = original == null ? "" : original.Replace("\r\n", "\n").Replace('\r', '\n');
            List<string> ours = new List<string>();
            ours.Add("[" + InspectorConfig.Section + "]");
            ours.Add("; Agent Inspector settings. Only this section is rewritten by the bridge.");
            ours.Add("; ToggleKey: single key name that shows/hides the inspector. Default N.");
            ours.Add("ToggleKey=" + cfg.ToggleKey);
            ours.Add("; Geometry: left,top,width,height as fractions of the viewport.");
            ours.Add("Geometry=" + Describe(cfg.Left) + "," + Describe(cfg.Top) + "," + Describe(cfg.Width) + "," + Describe(cfg.Height));
            ours.Add("SplitX=" + Describe(cfg.SplitX));
            ours.Add("SplitY=" + Describe(cfg.SplitY));
            ours.Add("; Font: text family for the inspector window. CJK needs a family that has the glyphs.");
            ours.Add("Font=" + cfg.Font);
            ours.Add("; Language: empty follows the game's own language; en, or zh/cn to force one.");
            ours.Add("Language=" + cfg.Language);
            ours.Add("; ShotSaveFull: true saves the original resolution on disk. The picture handed to the agent stays small either way.");
            ours.Add("ShotSaveFull=" + (cfg.ShotSaveFull ? "true" : "false"));
            ours.Add("MaxEvents=" + cfg.MaxEvents);
            ours.Add("MaxBytes=" + cfg.MaxBytes);
            ours.Add("MaxPayloadChars=" + cfg.MaxPayloadChars);

            if (src.Length == 0)
            {
                StringBuilder sb0 = new StringBuilder();
                for (int i = 0; i < ours.Count; i++) { sb0.Append(ours[i]).Append(nl); }
                return sb0.ToString();
            }

            string[] lines = src.Split(new char[] { '\n' });
            List<string> outLines = new List<string>();
            int i2 = 0;
            bool written = false;
            while (i2 < lines.Length)
            {
                string line = lines[i2];
                string trimmed = line.Trim();
                bool isHeader = trimmed.Length > 0 && trimmed[0] == '[';
                string name = "";
                if (isHeader)
                {
                    int close = trimmed.IndexOf(']');
                    name = close > 1 ? trimmed.Substring(1, close - 1).Trim() : "";
                }
                if (isHeader && string.Equals(name, InspectorConfig.Section, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip the previous body of our section.
                    i2++;
                    while (i2 < lines.Length)
                    {
                        string t2 = lines[i2].Trim();
                        if (t2.Length > 0 && t2[0] == '[') break;
                        i2++;
                    }
                    if (!written)
                    {
                        for (int k = 0; k < ours.Count; k++) outLines.Add(ours[k]);
                        written = true;
                    }
                    continue;
                }
                outLines.Add(line);
                i2++;
            }
            if (!written)
            {
                if (outLines.Count > 0 && outLines[outLines.Count - 1].Trim().Length != 0) outLines.Add("");
                for (int k = 0; k < ours.Count; k++) outLines.Add(ours[k]);
            }
            StringBuilder sb = new StringBuilder();
            for (int k = 0; k < outLines.Count; k++)
            {
                sb.Append(outLines[k]);
                if (k < outLines.Count - 1) sb.Append(nl);
            }
            return sb.ToString();
        }

        public static bool Save(string dir, InspectorConfig cfg, out string error)
        {
            error = null;
            string p = PathFor(dir);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string original = File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : "";
                string merged = Merge(original, cfg);
                File.WriteAllText(p, merged, new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Redaction happens before storage, not before display: a secret that was buffered
    /// once is already in the export file and in any screenshot of the panel.
    /// </summary>
    public static class InspectorRedact
    {
        public const string Mask = "[redacted]";

        public static bool IsSensitiveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n.IndexOf("token", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("secret", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("password", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("passwd", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("authorization", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("apikey", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("api_key", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("credential", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("cookie", StringComparison.Ordinal) >= 0) return true;
            if (n == "key") return true;
            return false;
        }

        /// <summary>
        /// Scrubs JSON-shaped text by walking the tree. Non-JSON text is truncated but not
        /// pattern-scrubbed: a regex over free text invents false confidence, and the bridge
        /// payloads are JSON in every path that matters. Callers that have JSON get real
        /// redaction; callers that do not are at least bounded and marked truncated.
        /// </summary>
        public static string Scrub(string payload, int maxChars, out bool truncated)
        {
            truncated = false;
            if (payload == null) return null;
            string text = payload;
            if (text.Length > 0 && (text[0] == '{' || text[0] == '['))
            {
                try
                {
                    JToken t = JToken.Parse(text);
                    ScrubToken(t);
                    text = t.ToString(Formatting.None);
                }
                catch (Exception) { }
            }
            if (maxChars > 0 && text.Length > maxChars)
            {
                text = text.Substring(0, maxChars) + "...[truncated " + (text.Length - maxChars) + " chars]";
                truncated = true;
            }
            return text;
        }

        private static void ScrubToken(JToken t)
        {
            JObject o = t as JObject;
            if (o != null)
            {
                foreach (JProperty p in o.Properties())
                {
                    if (IsSensitiveName(p.Name))
                    {
                        p.Value = new JValue(Mask);
                        continue;
                    }
                    ScrubToken(p.Value);
                }
                return;
            }
            JArray a = t as JArray;
            if (a != null)
            {
                for (int i = 0; i < a.Count; i++) ScrubToken(a[i]);
            }
        }
    }

    /// <summary>
    /// One bounded ring buffer for the whole bridge. Two limits on purpose: a count cap
    /// alone lets ten 1 MB payloads exhaust memory, a byte cap alone lets a million tiny
    /// events exhaust the UI.
    /// </summary>
    public static class InspectorBuffer
    {
        private static readonly object _lock = new object();
        private static readonly List<InspectorEvent> _events = new List<InspectorEvent>();
        private static int _maxEvents = 2000;
        private static int _maxBytes = 2000000;
        private static int _maxPayloadChars = 4000;
        private static long _seq;
        private static int _bytes;
        private static int _dropped;
        private static int _truncated;
        private static int _merged;
        private static int _rejected;
        private static string _lastExport;
        private static int _lastExportCount;

        public static void Configure(int maxEvents, int maxBytes, int maxPayloadChars)
        {
            lock (_lock)
            {
                if (maxEvents >= 50) _maxEvents = maxEvents;
                if (maxBytes >= 50000) _maxBytes = maxBytes;
                if (maxPayloadChars >= 200) _maxPayloadChars = maxPayloadChars;
            }
        }

        public static int Count { get { lock (_lock) { return _events.Count; } } }
        public static int BytesUsed { get { lock (_lock) { return _bytes; } } }
        public static int Dropped { get { lock (_lock) { return _dropped; } } }
        public static int Truncated { get { lock (_lock) { return _truncated; } } }

        /// <summary>Adds one event. Never throws: the inspector must not be able to break a command.</summary>
        public static void Add(string eventType, string source, string summary, string sessionId,
            string stepId, string commandId, string inputPayload, string outputPayload,
            string status, double durationMs, string errorMessage)
        {
            try
            {
                AddInner(eventType, source, summary, sessionId, stepId, commandId,
                    inputPayload, outputPayload, status, durationMs, errorMessage);
            }
            catch (Exception)
            {
                lock (_lock) { _rejected++; }
            }
        }

        private static void AddInner(string eventType, string source, string summary, string sessionId,
            string stepId, string commandId, string inputPayload, string outputPayload,
            string status, double durationMs, string errorMessage)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            bool t1, t2;
            string inP = InspectorRedact.Scrub(inputPayload, _maxPayloadChars, out t1);
            string outP = InspectorRedact.Scrub(outputPayload, _maxPayloadChars, out t2);
            bool truncated = t1 || t2;

            lock (_lock)
            {
                InspectorEvent last = _events.Count > 0 ? _events[_events.Count - 1] : null;
                if (last != null && last.Repeat < 1000
                    && Same(last.EventType, eventType) && Same(last.Source, source)
                    && Same(last.Summary, summary) && Same(last.Status, status)
                    && Same(last.InputPayload, inP) && Same(last.OutputPayload, outP))
                {
                    last.Repeat++;
                    last.Timestamp = now;
                    _merged++;
                    return;
                }

                InspectorEvent e = new InspectorEvent();
                _seq++;
                e.EventId = "evt-" + _seq.ToString("D6", CultureInfo.InvariantCulture);
                e.SessionId = sessionId;
                e.StepId = stepId;
                e.CommandId = commandId;
                e.Timestamp = now;
                e.FirstTimestamp = now;
                e.EventType = eventType;
                e.Source = source;
                e.Summary = Bounded(summary, 400);
                e.InputPayload = inP;
                e.OutputPayload = outP;
                e.Status = status;
                e.DurationMs = durationMs;
                e.ErrorMessage = Bounded(errorMessage, 800);
                e.Repeat = 0;
                e.Truncated = truncated;
                int cost = Cost(e);
                int remaining = _maxBytes - _bytes;
                if (cost > remaining)
                {
                    // A payload bigger than the whole budget is truncated again rather than
                    // stored whole: the byte cap is the promise, not the count cap.
                    e.OutputPayload = Bounded(e.OutputPayload, Math.Max(120, remaining / 2));
                    e.InputPayload = Bounded(e.InputPayload, 120);
                    e.Truncated = true;
                    cost = Cost(e);
                }
                e.Bytes = cost;
                if (cost > _maxBytes)
                {
                    _rejected++;
                    return;
                }
                _events.Add(e);
                _bytes += cost;
                if (truncated) _truncated++;
                EvictLocked();
            }
        }

        private static void EvictLocked()
        {
            while (_events.Count > _maxEvents || _bytes > _maxBytes)
            {
                if (_events.Count == 0) break;
                InspectorEvent e = _events[0];
                _events.RemoveAt(0);
                _bytes -= e.Bytes;
                if (_bytes < 0) _bytes = 0;
                _dropped++;
            }
        }

        private static int Cost(InspectorEvent e)
        {
            int n = 96;
            n += Len(e.Summary) + Len(e.InputPayload) + Len(e.OutputPayload) + Len(e.ErrorMessage) + Len(e.Status);
            return n;
        }

        private static int Len(string s) { return s == null ? 0 : s.Length; }

        private static string Bounded(string s, int max)
        {
            if (s == null) return null;
            if (max <= 0 || s.Length <= max) return s;
            return s.Substring(0, max) + "...[cut " + (s.Length - max) + "]";
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        public static int Clear()
        {
            lock (_lock)
            {
                int n = _events.Count;
                _events.Clear();
                _bytes = 0;
                _dropped = 0;
                _truncated = 0;
                _merged = 0;
                return n;
            }
        }

        public static JArray Page(string type, string source, string q, int limit, out int total, out int matched)
        {
            JArray outArr = new JArray();
            lock (_lock)
            {
                total = _events.Count;
                matched = 0;
                for (int i = _events.Count - 1; i >= 0; i--)
                {
                    InspectorEvent e = _events[i];
                    if (!Matches(e, type, source, q)) continue;
                    matched++;
                    if (outArr.Count >= limit) continue;
                    outArr.Add(ToJson(e));
                }
            }
            return outArr;
        }

        public static List<InspectorEvent> Snapshot(string type, string source, string q)
        {
            List<InspectorEvent> res = new List<InspectorEvent>();
            lock (_lock)
            {
                for (int i = 0; i < _events.Count; i++)
                {
                    InspectorEvent e = _events[i];
                    if (Matches(e, type, source, q)) res.Add(e);
                }
            }
            return res;
        }

        private static bool Matches(InspectorEvent e, string type, string source, string q)
        {
            if (!string.IsNullOrEmpty(type) && !SameInsensitive(e.EventType, type)) return false;
            if (!string.IsNullOrEmpty(source) && !SameInsensitive(e.Source, source)) return false;
            if (!string.IsNullOrEmpty(q))
            {
                string needle = q.ToLowerInvariant();
                bool hit = Contains(e.Summary, needle) || Contains(e.InputPayload, needle)
                    || Contains(e.OutputPayload, needle) || Contains(e.ErrorMessage, needle)
                    || Contains(e.StepId, needle) || Contains(e.CommandId, needle);
                if (!hit) return false;
            }
            return true;
        }

        private static bool SameInsensitive(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay)) return false;
            return hay.ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        public static JObject ToJson(InspectorEvent e)
        {
            JObject o = new JObject();
            o["eventId"] = e.EventId;
            o["sessionId"] = e.SessionId;
            o["stepId"] = e.StepId;
            o["commandId"] = e.CommandId;
            o["timestamp"] = e.Timestamp;
            if (!Same(e.FirstTimestamp, e.Timestamp)) o["firstTimestamp"] = e.FirstTimestamp;
            o["eventType"] = e.EventType;
            o["source"] = e.Source;
            o["summary"] = e.Summary;
            o["inputPayload"] = e.InputPayload;
            o["outputPayload"] = e.OutputPayload;
            o["status"] = e.Status;
            o["durationMs"] = e.DurationMs;
            o["errorMessage"] = e.ErrorMessage;
            o["repeat"] = e.Repeat;
            o["truncated"] = e.Truncated;
            o["bytes"] = e.Bytes;
            return o;
        }

        public static JObject Stats()
        {
            lock (_lock)
            {
                JObject o = new JObject();
                o["events"] = _events.Count;
                o["maxEvents"] = _maxEvents;
                o["bytes"] = _bytes;
                o["maxBytes"] = _maxBytes;
                o["maxPayloadChars"] = _maxPayloadChars;
                o["dropped"] = _dropped;
                o["truncated"] = _truncated;
                o["mergedRepeats"] = _merged;
                o["rejected"] = _rejected;
                o["lastExport"] = _lastExport;
                o["lastExportCount"] = _lastExportCount;
                return o;
            }
        }

        public static JObject Export(string dir, string type, string source, string q, int limit)
        {
            List<InspectorEvent> snap = Snapshot(type, source, q);
            bool capped = limit > 0 && snap.Count > limit;
            int take = capped ? limit : snap.Count;

            JObject head = new JObject();
            head["kind"] = "agent-inspector-export";
            head["filter"] = new JObject();
            head["filter"]["eventType"] = type;
            head["filter"]["source"] = source;
            head["filter"]["q"] = q;
            head["filter"]["limit"] = limit;
            head["count"] = take;
            head["matched"] = snap.Count;
            head["truncatedByLimit"] = capped;
            head["createdUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            head["note"] = "bounded view of the in-memory inspector buffer; not the full history and not TR evidence";

            StringBuilder sb = new StringBuilder();
            sb.Append(head.ToString(Formatting.None)).Append('\n');
            for (int i = 0; i < take; i++) sb.Append(ToJson(snap[i]).ToString(Formatting.None)).Append('\n');

            string dirExports = System.IO.Path.Combine(dir, "exports");
            string name = "inspector-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";
            string path = System.IO.Path.Combine(dirExports, name);

            JObject o = new JObject();
            o["path"] = path;
            o["count"] = take;
            o["matched"] = snap.Count;
            o["truncatedByLimit"] = capped;
            try
            {
                if (!Directory.Exists(dirExports)) Directory.CreateDirectory(dirExports);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                o["ok"] = true;
            }
            catch (Exception e)
            {
                o["ok"] = false;
                o["error"] = e.Message;
            }
            lock (_lock)
            {
                _lastExport = path;
                _lastExportCount = take;
            }
            return o;
        }
    }

    /// <summary>
    /// HTTP-facing facade used by <c>Api</c>. Pure: the mod directory arrives as a parameter.
    /// </summary>
    public static class InspectorBridge
    {
        public const string Prefix = "/inspector";

        public static bool IsInspectorPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Records one completed bridge request. Deliberately never throws and never records
        /// its own endpoints: otherwise reading /inspector/events would create events, which
        /// would change the thing being read.
        /// </summary>
        public static void RecordRequest(string method, string path, string body, int status,
            bool ok, string errorCode, string errorMessage, string responseJson,
            double durationMs, string sessionId)
        {
            try
            {
                if (IsInspectorPath(path)) return;
                // R5: "4xx = Error" made every deliberate refusal look like a bridge fault.
                // A rejected request is the bridge WORKING: the caller asked for something the
                // contract does not allow and got a code back. A failure is when the bridge
                // itself broke (5xx, an unhandled exception, a timeout) or when there is no code
                // to attribute it to. The two now have different kinds so a red row means
                // "something is broken", not "somebody sent a bad parameter".
                string eventType;
                if (status >= 400 || !ok)
                    eventType = IsBridgeFault(status, errorCode) ? "Error" : "Rejected";
                else if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) eventType = "Observation";
                else eventType = "ActionIssued";

                string statusText = "http_" + status.ToString(CultureInfo.InvariantCulture);
                if (!ok && !string.IsNullOrEmpty(errorCode)) statusText = statusText + ":" + errorCode;

                string summary = method + " " + path + " -> " + status.ToString(CultureInfo.InvariantCulture);
                string err = null;
                if (!ok) err = errorMessage;

                InspectorBuffer.Add(eventType, "bridge", summary, sessionId, null, null,
                    body, responseJson, statusText, durationMs, err);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// True when the row reflects a broken bridge rather than a refused request:
        /// 5xx, or a code that says "the bridge could not do its job", or no code at all.
        /// Everything else in the 4xx family is a contract rejection.
        /// </summary>
        private static bool IsBridgeFault(int status, string errorCode)
        {
            if (status >= 500) return true;
            if (string.IsNullOrEmpty(errorCode)) return true;
            switch (errorCode)
            {
                case "route_error":                 // unhandled exception inside a handler
                case "off_main_thread":             // called from a thread that must not touch the game
                case "ui_build_failed":
                case "ui_mount_failed":
                case "shot_failed":
                case "measure_failed":
                case "ruler_failed":
                case "no_world_frame":              // the game had no frame to read
                case "session_boundary_unverified": // the session guard could not decide
                    return true;
            }
            return false;
        }
        public static JObject State(string dir)
        {
            InspectorConfig cfg = InspectorSettings.Load(dir);
            InspectorBuffer.Configure(cfg.MaxEvents, cfg.MaxBytes, cfg.MaxPayloadChars);
            JObject o = new JObject();
            o["buffer"] = InspectorBuffer.Stats();
            o["settings"] = InspectorSettings.PathFor(dir);
            o["settingsExists"] = File.Exists(InspectorSettings.PathFor(dir));
            o["toggleKey"] = cfg.ToggleKey;
            o["configDiagnostics"] = new JArray(cfg.Diagnostics.ToArray());
            o["exportDir"] = System.IO.Path.Combine(dir, "exports");
            o["note"] = "buffer counts only what the bridge saw; game-level success still needs a read-back";
            return o;
        }

        public static JObject Config(string dir)
        {
            InspectorConfig cfg = InspectorSettings.Load(dir);
            JObject o = cfg.ToJson();
            o["path"] = InspectorSettings.PathFor(dir);
            o["exists"] = File.Exists(InspectorSettings.PathFor(dir));
            JObject def = new InspectorConfig().ToJson();
            o["defaults"] = def;
            return o;
        }

        public static JObject Events(string query)
        {
            string type = QueryValue(query, "type");
            string source = QueryValue(query, "source");
            string q = QueryValue(query, "q");
            int limit = ParseLimit(QueryValue(query, "limit"), 100);
            int total; int matched;
            JArray arr = InspectorBuffer.Page(type, source, q, limit, out total, out matched);
            JObject o = new JObject();
            o["events"] = arr;
            o["returned"] = arr.Count;
            o["matched"] = matched;
            o["buffered"] = total;
            o["limit"] = limit;
            o["stats"] = InspectorBuffer.Stats();
            return o;
        }

        public static JObject Export(string dir, JObject req)
        {
            string type = req == null ? null : Str(req, "type");
            string source = req == null ? null : Str(req, "source");
            string q = req == null ? null : Str(req, "q");
            int limit = req == null ? 0 : Int(req, "limit", 0);
            return InspectorBuffer.Export(dir, type, source, q, limit);
        }

        /// <summary>
        /// Mock injection exists so the UI can be exercised without a model. Everything it
        /// creates is stamped source=mock, and the panel must render that label: a mock
        /// decision shown as real is worse than no decision shown.
        /// </summary>
        public static JObject InjectMock(JObject req)
        {
            if (req == null) return null;
            string type = Str(req, "eventType");
            if (string.IsNullOrEmpty(type)) type = "Decision";
            string summary = Str(req, "summary");
            if (string.IsNullOrEmpty(summary)) summary = "mock " + type;
            string input = req["inputPayload"] == null ? null : req["inputPayload"].ToString(Formatting.None);
            string output = req["outputPayload"] == null ? null : req["outputPayload"].ToString(Formatting.None);
            JObject meta = new JObject();
            meta["mock"] = true;
            string tagged = meta.ToString(Formatting.None);
            string merged = input == null ? tagged : input;
            InspectorBuffer.Add(type, "mock", summary, Str(req, "sessionId"), Str(req, "stepId"),
                Str(req, "commandId"), merged, output, "mock", 0, Str(req, "errorMessage"));
            JObject o = new JObject();
            o["ok"] = true;
            o["source"] = "mock";
            o["eventType"] = type;
            o["buffered"] = InspectorBuffer.Count;
            return o;
        }

        public static JObject Clear()
        {
            JObject o = new JObject();
            o["cleared"] = InspectorBuffer.Clear();
            o["note"] = "clears the inspector view only; the command ledger and TR evidence are untouched";
            return o;
        }

        private static string Str(JObject o, string key)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            return Convert.ToString(t);
        }

        private static int Int(JObject o, string key, int dflt)
        {
            JToken t = o[key];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            int n;
            if (int.TryParse(Convert.ToString(t), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return dflt;
        }

        private static int ParseLimit(string raw, int dflt)
        {
            int n;
            if (string.IsNullOrEmpty(raw)) return dflt;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return dflt;
            if (n < 1) return 1;
            if (n > 500) return 500;
            return n;
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string[] parts = query.Split(new char[] { '&' });
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(parts[i].Substring(0, eq), key, StringComparison.OrdinalIgnoreCase)) continue;
                return System.Uri.UnescapeDataString(parts[i].Substring(eq + 1));
            }
            return null;
        }
    }
}
