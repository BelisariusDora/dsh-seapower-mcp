// Game API surface: routes HTTP requests to game objects on the Unity main thread.
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// Carries an HTTP status and a stable machine-readable code alongside the message.
    /// Business-level refusals travel as HTTP 200 with ok=false; these are for
    /// request-shape problems that never reached the game.
    /// </summary>
    public sealed class ApiException : Exception
    {
        public readonly int Status;
        public readonly string Code;
        public readonly bool Retryable;

        public ApiException(int status, string code, string message, bool retryable)
            : base(message)
        {
            Status = status;
            Code = code;
            Retryable = retryable;
        }
    }

    public static class Api
    {
        private static readonly BindingFlags PrivInst =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo _objectsField;

        // ------------------------------------------------------------ probes
        public static string Probe1() { return "{\"probe\":\"api-type-loaded\"}"; }

        public static string Probe2()
        {
            JObject o = new JObject();
            o["probe"] = "newtonsoft-ok";
            return o.ToString(Formatting.None);
        }

        public static string Probe3()
        {
            try { Route("GET", "/definitely-not-a-route", ""); return "{\"probe\":\"route-returned\"}"; }
            catch (Exception e) { return "{\"probe\":\"route-catch\",\"message\":\"caught\"}"; }
        }

        // ---------------------------------------------------------------- entry

        // One request = one wait budget = one main-thread hop. The budget lives here, keyed by
        // the route, because it can only be enforced where the hop is made (see OnMain); every
        // handler used to carry its own number, which OnMainThread never read once the call was
        // already on the main thread - so /plot/draw advertised 45 s and was cut at 20 s.
        private static int DeadlineMs(string method, string path)
        {
            string query;
            string[] seg = SplitPath(path, out query);
            string head = seg.Length > 0 ? seg[0] : "";
            string op = seg.Length > 1 ? seg[1] : "";
            // The last segment, not the second one: a unit read is /units/{id}/{what},
            // so seg[1] is the id and the interesting word is at the end.
            string tail = seg.Length > 0 ? seg[seg.Length - 1] : "";
            switch (head)
            {
                case "console":
                    // Route lowercases console ops; comparing the raw segment made
                    // GET /console/STATE claim 15000 instead of its own bucket.
                    // 8000 rather than 4000: the first request of a session can arrive before
                    // the first Update (Bridge.Start opens the port first), and the old 4 s
                    // bucket reported that load window as a timeout.
                    return (op.Length == 0 || op.ToLowerInvariant() == "state") ? 8000 : 15000;
                case "ui": return op == "probe-controls" ? 15000 : 8000;
                case "comms":
                    // /comms/page builds the whole page and had no handler budget before S03;
                    // leaving it on the tightest bucket made the heaviest route the shortest.
                    // Compared as the raw segment, the way Route compares it: lowercasing here
                    // only widened the budget of paths that answer 404 anyway.
                    if (op.Length == 0 || op == "page" || op == "state") return 15000;
                    return (op == "window" && method != "GET") ? 15000 : 8000;
                case "inspector": return 15000;
                case "map": return (op == "units" || op == "unit-screen") ? 20000 : 15000;
                case "plot":
                    // Drawing touches the serialised plot table and resolves type names on a
                    // cold bridge: the first touch used to spend >20 s, so the client gave up
                    // while the main thread was still working.
                    if (op == "draw" || op == "drawings" || op == "erase") return 45000;
                    if (op == "course") return 30000;
                    if (op == "clear" || op == "ruler") return 20000;
                    return 15000;
                case "units":
                    if (op.Length == 0) return 15000;
                    if (method != "GET") return 15000;
                    // Seven reads declared 12000 before the budget lived in one table; four of
                    // them are addressed by the last segment (/units/12/deck), and sub/land are
                    // their own last segment too.
                    if (tail == "weapons" || tail == "deck" || tail == "airborne"
                        || tail == "decktasks" || tail == "vertical"
                        || tail == "sub" || tail == "land") return 12000;
                    return 8000;
                case "group": return 25000;
                case "probe": return 20000;
                case "session": return 12000;
                case "tracks": return 15000;
                case "state":
                case "taskforces":
                case "time": return 8000;
                // /diag and /ping are not listed: Dispatch answers them without the hop, so no
                // deadline applies to them at all (see IsThreadFreeProbe).
                // Shot capture waits for end of frame (Shots.Take allows 12 s internally).
                case "shot": return 20000;
                default: return 15000;
            }
        }

        /// <summary>GET /ping and GET /diag read only fields the main thread already cached, so
        /// Dispatch can answer them without the hop - which is the whole point: they must still
        /// answer while the main-thread pump is dead. Their handlers never call OnMain.</summary>
        private static bool IsThreadFreeProbe(string path)
        {
            string q;
            string[] seg = SplitPath(path, out q);
            if (seg.Length != 1) return false;
            return string.Equals(seg[0], "ping", StringComparison.OrdinalIgnoreCase)
                || string.Equals(seg[0], "diag", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Public entry. /watch is answered by the trigger registry; the rest of the
        /// surface is handed to the migrated implementation untouched.</summary>
        public static HttpResult Dispatch(string method, string path, string body)
        {
            if (Watch.Matches(path)) return Watch.Dispatch(method, path, body);
            return DispatchCore(method, path, body);
        }

        public static HttpResult DispatchCore(string method, string path, string body)

        {
            // Every route reads Unity / game state, and Unity APIs are main-thread only.
            try
            {
                // D6: the id belongs to the HTTP worker thread (MiniHttp.HandleClient
                // sets it there), while this envelope is built on the main thread after
                // the hop below - a [ThreadStatic] read there is always 0. Capture it here
                // and carry it across.
                long requestId = MiniHttp.CurrentCommandId;
                // Answered inline: see IsThreadFreeProbe. A hop would turn the one route that can
                // report a dead pump into a 504, which is indistinguishable from a busy pump.
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                    && IsThreadFreeProbe(path))
                    return DispatchOnMain(method, path, body, requestId);
                object r = Core.OnMainThread(
                    delegate { return DispatchOnMain(method, path, body, requestId); },
                    DeadlineMs(method, path));
                if (r == null)
                    return new HttpResult(500, MiniHttp.ErrorJson("empty_reply", "handler produced no response", false));
                return (HttpResult)r;
            }
            catch (MainThreadTimeoutException te)
            {
                // R6: only a pending item that was cancelled outright is safe to retry.
                // Once the main thread has started, the native call may already have fired.
                // A GET cannot have written anything, so an expired wait on a read is
                // always safe to retry -- even when the main thread had already started.
                // Reporting "outcome unknown" for a read teaches a caller not to retry a
                // question it may simply ask again.
                bool safe = te.Safe || method == "GET";
                return new HttpResult(504, MiniHttp.ErrorJson(
                    safe ? "main_thread_timeout" : "main_thread_state_unknown",
                    te.Message, safe));
            }
            catch (Exception e)
            {
                string msg = e.GetType().Name + ": " + e.Message;
                if (e.InnerException != null) msg += " <- " + e.InnerException.Message;
                return new HttpResult(500, MiniHttp.ErrorJson("bridge_error", msg, true));
            }
        }

        // R3: a refusal is not a success. Unit commands report refusals inside data
        // (refused / ok:false) and group commands report a failed count, but the MCP
        // host only looks at the envelope's ok field -- so a refused shot used to come
        // back as isError:false and the model could not tell it had been blocked.
        private static bool BusinessOk(JToken data)
        {
            JObject o = data as JObject;
            if (o == null) return true;
            // R4: order matters. A handler that says ok:true AND refused:true used to be read as
            // a success, because the ok field was consulted first - two statements, one winner,
            // and the winner was the wrong one. Refusal and failure now speak before success,
            // so there is a single rule: a route may not claim success while refusing.
            JToken refused = o["refused"];
            if (refused != null && refused.Type == JTokenType.Boolean && (bool)refused) return false;
            // A batch is only a success when every item succeeded; a partial batch stays
            // a failure but keeps its per-item results under data.results.
            JToken failed = o["failed"];
            if (failed != null && failed.Type == JTokenType.Integer && Convert.ToInt32(failed) > 0) return false;
            JToken ok = o["ok"];
            if (ok != null && ok.Type == JTokenType.Boolean) return (bool)ok;
            return true;
        }

        private static string RefusalMessage(JToken data)
        {
            JObject o = data as JObject;
            if (o == null) return "command was refused";
            JArray reasons = o["reasons"] as JArray;
            if (reasons != null && reasons.Count > 0)
            {
                string s = "";
                for (int i = 0; i < reasons.Count; i++)
                {
                    string r = Convert.ToString(reasons[i]);
                    if (r == null) continue;
                    if (s.Length > 0) s += "; ";
                    s += r;
                }
                if (s.Length > 0) return s;
            }
            if (o["error"] != null) return Convert.ToString(o["error"]);
            return "command was refused";
        }

        private static HttpResult DispatchOnMain(string method, string path, string body, long requestId)
        {
            JObject resp = new JObject();
            int status = 200;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            string recCode = null;
            string recMessage = null;

            // GET /shot/png is the only route that answers with a picture instead of JSON.
            // It is served here, before the envelope, because an image cannot sit inside
            // {"data": ...}: base64 in a JSON field reaches the model as a wall of text.
            // The bytes themselves are read on the HTTP thread-safe path (no Unity API is
            // touched - the PNG is a byte[] filled in on the main thread at end of frame).
            string pngQuery;
            string[] pngSeg = SplitPath(path, out pngQuery);
            if (pngSeg.Length == 2
                && string.Equals(pngSeg[0], "shot", StringComparison.OrdinalIgnoreCase)
                && string.Equals(pngSeg[1], "png", StringComparison.OrdinalIgnoreCase))
            {
                ApiException fail = null;
                byte[] png = null;
                try
                {
                    // GET only. HEAD is refused rather than answered: this bridge has no
                    // body suppression for HEAD anywhere, so advertising it would promise a
                    // headers-only reply and then send the whole 3 MB PNG.
                    if (method != "GET")
                        throw MethodNotAllowed(method, path, "GET");
                    string why;
                    png = Shots.Png(out why);
                    if (png == null) throw new ApiException(409, "shot_not_ready", why, true);
                }
                catch (ApiException ae) { fail = ae; }
                catch (Exception e) { fail = new ApiException(500, "shot_failed", e.Message, false); }

                int pngStatus = fail == null ? 200 : fail.Status;
                string pngBody = fail == null
                    ? "{\"content_type\":\"image/png\",\"bytes\":" + png.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "}"
                    : MiniHttp.ErrorJson(fail.Code, fail.Message, fail.Retryable);
                try
                {
                    double pms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
                    string psession = "world-" + SessionRegistry.WorldGeneration.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    InspectorBridge.RecordRequest(method, path, body, pngStatus, fail == null,
                        fail == null ? null : fail.Code, fail == null ? null : fail.Message,
                        pngBody, pms, psession);
                }
                catch (Exception) { }

                if (fail != null) return new HttpResult(pngStatus, pngBody);
                return new HttpResult(200, png, "image/png");
            }

            try
            {
                // D6: stamp the envelope with the request id so a reply can be tied to the
                // bridge.log lines that describe it.
                resp["cmd"] = (JToken)new JValue(requestId);
                JToken data = Route(method, path, body);
                resp["data"] = data;
                // R3: propagate a business-level refusal into the envelope so the MCP
                // layer sees ok:false and can raise isError.
                bool businessOk = BusinessOk(data);
                resp["ok"] = businessOk;
                // R4: one decision, copied down. Handlers still write data.ok (the offline suites
                // and the MCP layer read it), but the envelope is the authority - so the same
                // boolean is written into data as well instead of leaving a second, possibly
                // stale opinion in the body. If a handler ever writes ok:true while refusing,
                // the envelope and the body now say the same thing.
                JObject dataObj = data as JObject;
                if (dataObj != null)
                {
                    JToken dataOk = dataObj["ok"];
                    if (dataOk != null && dataOk.Type == JTokenType.Boolean) dataObj["ok"] = businessOk;
                }
                if (!businessOk)
                {
                    resp["refused"] = true;
                    JObject rerr = new JObject();
                    rerr["code"] = "refused";
                    rerr["message"] = RefusalMessage(data);
                    rerr["retryable"] = false;
                    resp["error"] = rerr;
                }
            }
            catch (ApiException ae)
            {
                JObject err = new JObject();
                err["code"] = ae.Code;
                err["message"] = ae.Message;
                err["retryable"] = ae.Retryable;
                resp["ok"] = false;
                resp["error"] = err;
                status = ae.Status;
                recCode = ae.Code;
                recMessage = ae.Message;
            }
            catch (Exception e)
            {
                // B21 prevention: a 500 seen once and never reproduced is unactionable
                // unless the envelope names the request that caused it. method/path/body
                // are right here, so put them in the error itself and in the log - the
                // next occurrence diagnoses itself instead of costing another session.
                JObject err = new JObject();
                err["code"] = "route_error";
                err["message"] = e.Message;
                err["retryable"] = false;
                err["error_type"] = e.GetType().Name;
                if (e.InnerException != null) err["inner"] = e.InnerException.Message;
                // The frame list is what turns "route_error" into a fixable report. Only
                // unexpected exceptions get here, so there is no ApiException noise.
                string st = e.StackTrace;
                if (!string.IsNullOrEmpty(st))
                    err["stack"] = st.Length <= 1200 ? st : st.Substring(0, 1200);
                err["request"] = method + " " + path;
                err["body_len"] = body == null ? 0 : body.Length;
                if (!string.IsNullOrEmpty(body))
                {
                    err["body"] = body.Length <= 300 ? body : body.Substring(0, 300) + "...";
                }
                Core.Log("route_error " + method + " " + path
                    + " bodyLen=" + (body == null ? 0 : body.Length)
                    + " -> " + e.GetType().Name + ": " + e.Message);
                resp["ok"] = false;
                resp["error"] = err;
                status = 500;
                recCode = "route_error";
                recMessage = e.Message;
            }
            HttpResult hr = new HttpResult(status, resp.ToString(Formatting.None));
            // I0: one observation per completed request, recorded at the single choke point
            // so no route can forget to report itself. InspectorBridge drops its own
            // endpoints; refusals, 4xx and 500s are all kept, because those are the events
            // an operator actually needs.
            try
            {
                bool recOk = true;
                JToken okTok = resp["ok"];
                if (okTok != null && okTok.Type == JTokenType.Boolean) recOk = okTok.Value<bool>();
                JObject errObj = resp["error"] as JObject;
                if (errObj != null)
                {
                    if (recCode == null) recCode = Convert.ToString(errObj["code"]);
                    if (recMessage == null) recMessage = Convert.ToString(errObj["message"]);
                    recOk = false;
                }
                double recMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                string recSession = "world-" + SessionRegistry.WorldGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                InspectorBridge.RecordRequest(method, path, body, status, recOk, recCode, recMessage,
                    hr.Body, recMs, recSession);
            }
            catch (Exception) { }
            return hr;
        }

        private static ApiException MethodNotAllowed(string method, string path, string allow)
        {
            return new ApiException(405, "method_not_allowed",
                method + " is not allowed on " + path + "; use " + allow, false);
        }

        /// <summary>
        /// Folder for inspector settings and exports. Core.ModDir already falls back when
        /// Application.dataPath is unavailable, which is how the offline suites get a real
        /// directory instead of an exception.
        /// </summary>
        private static string InspectorDir()
        {
            try { return Core.ModDir(); }
            // Fully qualified: Api.cs imports SeaPower, which also declares an
            // Environment type, so a bare Environment.CurrentDirectory is CS0104.
            catch (Exception) { return System.Environment.CurrentDirectory; }
        }

        /// <summary>
        /// Query string as a JObject so a GET and a POST can travel through the same
        /// handler signature. Values that parse as numbers become numbers; everything
        /// else stays a string, which is what the map handlers validate against.
        /// </summary>
        private static JObject QueryObject(string query)
        {
            JObject o = new JObject();
            if (string.IsNullOrEmpty(query)) return o;
            // Explicit array on purpose: Unity's Mono mscorlib declares
            // Split(Char, StringSplitOptions = None), and C# prefers that optional
            // overload over the params one - the result is a MissingMethodException
            // on any runtime whose mscorlib lacks it (the offline suites run on
            // .NET Framework 4.8, which does). InspectorBridge hit the same thing.
            string[] parts = query.Split(new char[] { '&' });
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                int eq = parts[i].IndexOf('=');
                string key = eq < 0 ? parts[i] : parts[i].Substring(0, eq);
                string val = eq < 0 ? "" : parts[i].Substring(eq + 1);
                key = System.Uri.UnescapeDataString(key.Replace("+", " "));
                val = System.Uri.UnescapeDataString(val.Replace("+", " "));
                if (key.Length == 0) continue;
                double d;
                if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d))
                    o[key] = d;
                else
                    o[key] = val;
            }
            return o;
        }
        private static JToken Route(string method, string path, string body)
        {
            string query;
            string[] seg = SplitPath(path, out query);
            JObject req = ParseBody(body);
            bool isGet = method == "GET" || method == "HEAD";

            if (seg.Length == 0 || seg[0].Length == 0)
                throw new ApiException(404, "not_found", "empty path", false);

            string head = seg[0];

            // ------------------------------------------------- TR2-S session read
            // Read-only and never gated: a caller whose write was refused has to be able
            // to see why, otherwise "409 session_boundary_unverified" is a dead end.
            if (head == "session")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                // Session entry: an agent that keeps an id from the previous scenario gets
                // "unit not found" - correct, but it learns nothing about what IS there. This
                // sub-route answers one question: "which of my units exist right now", stamped
                // with the world generation so a stale answer stays recognisable.
                if (seg.Length >= 2 && seg[1] == "fleet") return OnMain(delegate { return FleetSnapshot(); });
                if (seg.Length >= 2)
                    throw new ApiException(404, "not_found", "expected /session or /session/fleet", false);
                return SessionRegistry.Report();            }

            // --------------------------------------------- ACT-004 / T06 UI surface
            // Only the agent's own panel is managed here: install is idempotent,
            // remove takes out exactly the instance this bridge added. Nothing in
            // this block reads or writes game state, so it is not behind the
            // session write gate; it does need the main thread, because every
            // Noesis call must happen there.
            if (head == "ui")
            {
                if (seg.Length < 2)
                    throw new ApiException(404, "not_found",
                        "expected /ui/{state|install|remove|hold|focus|type|submit|probe-controls}", false);
                string uiOp = seg[1];
                if (uiOp == "state")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return OnMain(delegate { return UiBridge.State(); });
                }
                if (uiOp == "focus")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    // TAKING THE KEYBOARD ON PURPOSE. SetTyping(true) is exactly what a click on the
                    // input box does; until now that click was the only way in, so typingElementOk -
                    // the field the InvalidCastException fix writes - could never be observed from a
                    // script, and "the lock works" stayed unprovable. Same call, honest owner label;
                    // {"on":false} hands the keyboard back so the player can steer again.
                    bool focusOn = ReqBool(req, "on", true);
                    return OnMain(delegate { return UiBridge.SetTyping(focusOn, "http focus"); });
                }
                if (uiOp == "install")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    string uiText = ReqStr(req, "text", "DSH agent panel");
                    return OnMain(delegate { return UiBridge.Install(uiText); });
                }
                if (uiOp == "remove")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return UiBridge.Remove(); });
                }
                if (uiOp == "hold")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    // Omitting 'on' flips the current value, which is exactly what a click
                    // does. Both paths end in the same bridge state, so the toggle cannot
                    // drift away from the programmatic setter.
                    bool holdOn = ReqBool(req, "on", !UiBridge.Hold);
                    // Turning the stop ON is always allowed. Turning it OFF is the one
                    // operation the gate exists to prevent, so a caller cannot do it by
                    // accident (or by being the thing that is gated) without saying so. The
                    // operator's click on the panel dot is unchanged.
                    if (!holdOn && ReqStr(req, "confirm", null) != "release")
                        throw new ApiException(409, "hold_release_needs_confirm",
                            "releasing the operator HOLD over HTTP needs {\"on\":false,\"confirm\":\"release\"};"
                            + " the panel dot releases it without one", false);
                    return OnMain(delegate { return UiBridge.SetHold(holdOn, "http"); });
                }
                if (uiOp == "type")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    string typed = ReqStr(req, "text", null);
                    if (typed == null)
                        throw new ApiException(400, "missing_field",
                            "ui type requires 'text' (use /ui/submit to submit what is already "
                            + "in the box)", false);
                    return OnMain(delegate { return UiBridge.TypeText(typed, "http"); });
                }
                if (uiOp == "submit")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return UiBridge.Submit("http"); });
                }
                if (uiOp == "typing")
                {
                    bool on;
                    if (isGet) on = true;
                    else on = ReqBool(req, "on", true);
                    return OnMain(delegate { return UiBridge.SetTyping(on, "http"); });
                }
                if (uiOp == "probe-controls")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    // It mutates the visual tree (temporarily) and answers a development
                    // question, so it stays behind the diagnostics switch like every other
                    // diagnostic route.
                    if (!MiniHttp.DiagnosticsEnabled)
                        throw new ApiException(403, "diagnostics_disabled",
                            "control probing is development diagnostics: create diagnostics.on "
                            + "inside the mod folder to enable it", false);
                    return OnMain(delegate { return UiBridge.ProbeControls(); });
                }
                throw new ApiException(404, "not_found", "unknown ui command: " + uiOp, false);
            }

            // -------------------------------------------- ACT-010 Agent Inspector
            // Read-only views of what the bridge itself observed, plus a bounded JSONL
            // export and a mock injector. Placed BEFORE the session write gate on purpose:
            // reading the buffer is not a game command, so it must keep working while a
            // rebuild is unconfirmed. Nothing in this block touches game state.
            // TR2-H console host: one window with two tabs (N opens, Tab switches).
            // Kept next to /inspector because it only coordinates the two pages; it owns
            // no state of its own and never touches Unity outside the main thread.
            if (head == "console")
            {
                if (seg.Length < 2) return OnMain(delegate { return (JToken)ConsoleHost.State(); });
                string conOp = seg[1].Trim().ToLowerInvariant();
                if (conOp == "state")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return OnMain(delegate { return (JToken)ConsoleHost.State(); });
                }
                if (conOp != "toggle" && conOp != "next" && conOp != "open" && conOp != "close" && conOp != "show")
                {
                    throw new ApiException(404, "bad_op",
                        "unknown console command: " + conOp + " (expected state, toggle, next, open, close or show)", false);
                }
                // Reading is a GET, changing the console is a POST: a "close" that answers to
                // a GET is the kind of thing a browser prefetch can do by accident.
                if (isGet) throw MethodNotAllowed(method, path, "POST");
                JObject conReq = ParseBody(body);
                if (conReq == null) conReq = new JObject();
                conReq["op"] = conOp;
                JObject conReqFinal = conReq;
                return OnMain(delegate { return (JToken)ConsoleHost.Dispatch(conReqFinal); });
            }

            if (head == "inspector")
            {
                if (seg.Length < 2)
                    throw new ApiException(404, "not_found",
                        "expected /inspector/{state|config|events|export|mock|clear|window}", false);
                string inOp = seg[1];
                string inDir = InspectorDir();
                if (inOp == "state")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return InspectorBridge.State(inDir);
                }
                if (inOp == "config")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return InspectorBridge.Config(inDir);
                }
                if (inOp == "events")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return InspectorBridge.Events(query);
                }
                if (inOp == "export")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return InspectorBridge.Export(inDir, req);
                }
                if (inOp == "mock")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    // Development aid only: everything it creates is stamped source=mock,
                    // because a mock decision rendered as a real one is worse than none.
                    if (!MiniHttp.DiagnosticsEnabled)
                        throw new ApiException(403, "diagnostics_disabled",
                            "mock event injection is development diagnostics: create "
                            + "diagnostics.on inside the mod folder to enable it", false);
                    return InspectorBridge.InjectMock(req);
                }
                if (inOp == "window")
                {
                    // The window itself is a read-only view. GET reports its geometry and
                    // list state; POST installs, moves, resizes, maximises, collapses or
                    // removes it. Nothing here issues a game command, which is why this
                    // route sits before the session write gate like the rest of /inspector.
                    if (isGet) return InspectorWindow.State();
                    // The window ops build and mutate the Noesis visual tree, so they belong on the
                    // main thread like every other Unity-touching route (C260/C261).
            return OnMain(delegate { return (JToken)InspectorWindow.Command(req); });
                }
                if (inOp == "clear")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return InspectorBridge.Clear();
                }
                throw new ApiException(404, "not_found", "unknown inspector command: " + inOp, false);
            }

            // ------------------------------------------------- harness communication page
            // TR2-H. The page itself is a pure Noesis view, but every entry point touches
            // the UI tree, so all of it runs on the main thread like the inspector's.
            //
            // Read and write are split on purpose: GET reports state, POST drives the page
            // (install, toggle, send, clear). Sending a player line only writes the outbox
            // file - this route must never appear to have delivered anything to the
            // harness, because nothing in the game process can reach the harness HTTP API.
            if (head == "comms")
            {
                if (seg.Length < 2)
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return CommsBridge.State();
                }
                string cmOp = seg[1];
                // body arrives as the raw request string; parse it once here so every write
                // branch works on a JObject, and a malformed body fails as bad_json before
                // any of the branches run.
                JObject cmReq = isGet ? null : ParseBody(body);
                if (cmOp == "state")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return CommsBridge.State();
                }
                if (cmOp == "page")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    long sinceSeq = 0;
                    int lim = 200;
                    JObject qp = QueryObject(query);
                    if (qp != null && qp["since"] != null) sinceSeq = (long)qp["since"];
                    if (qp != null && qp["limit"] != null) lim = (int)qp["limit"];
                    JObject pg = new JObject();
                    pg["items"] = CommsBridge.Page(sinceSeq, lim);
                    pg["state"] = CommsBridge.State();
                    return pg;
                }
                if (cmOp == "send" || cmOp == "clear")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    if (cmOp == "clear") return OnMain(delegate { return CommsBridge.Clear(); });
                    // The page posts {"text": "..."} with no op at all. An explicit op is
                    // still honoured, so a generic client can drive every command through
                    // this one URL - but a missing op must mean "send", not an error.
                    JObject sendReq = cmReq;
                    string sendOp = sendReq == null || sendReq["op"] == null ? null : (string)sendReq["op"];
                    if (!string.IsNullOrEmpty(sendOp))
                        return OnMain(delegate { return CommsBridge.Dispatch(sendReq); });
                    return OnMain(delegate { return CommsBridge.Send(sendReq); });
                }
                if (cmOp == "window")
                {
                    if (isGet) return OnMain(delegate { return CommsWindow.State(); });
                    return OnMain(delegate
                    {
                        string wop = cmReq == null || cmReq["op"] == null ? null : (string)cmReq["op"];
                        if (wop == null || wop.Length == 0)
                            throw new ApiException(400, "missing_field", "comms window needs an 'op'", false);
                        if (wop == "tree") return CommsWindow.Tree();
                        if (wop == "install") return CommsWindow.Install();
                        if (wop == "remove") return CommsWindow.Remove();
                        if (wop == "toggle") return CommsWindow.Toggle();
                        if (wop == "show")
                        {
                            bool on = cmReq["on"] == null || (bool)cmReq["on"];
                            // Same reason as /inspector/window: Show() sets Visibility and can Install().
                            return OnMain(delegate { return (JToken)CommsWindow.Show(on); });
                        }
                        throw new ApiException(400, "bad_value",
                            "unknown comms window op: " + wop + " (expected install, remove, toggle or show)", false);
                    });
                }
                throw new ApiException(404, "not_found",
                    "unknown comms command: " + cmOp
                    + " (expected state, page, send, clear or window)", false);
            }
            // ------------------------------------------------------- frame screenshots
            // A PNG of the frame the game actually drew, taken at end of frame. Every other
            // readout here reports what something claims to be; only a picture answers
            // "were the glyphs rasterised, is the pane really inside the window, did the
            // marker land on the right pixel". Queued rather than returned inline because a
            // back buffer can only be read after the frame is finished.
            //
            // The picture is what the player can see, WITH whatever UI is on it. That is not
            // the same thing as an IntelPolicy-filtered AgentContext, and nothing here may
            // claim the image was scrubbed. The bytes are served by GET /shot/png outside
            // the JSON envelope (handled in DispatchOnMain): base64 inside a JSON field
            // would come back to the model as text, not as a picture.
            if (head == "shot")
            {
                if (seg.Length >= 2 && string.Equals(seg[1], "png", StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(500, "route_error",
                        "/shot/png is served as raw bytes before the JSON envelope; "
                        + "if this fires the early branch in DispatchOnMain was lost", false);
                if (isGet) return Shots.Status();
                if (method != "POST") throw MethodNotAllowed(method, path, "GET, POST");
                return Shots.Dispatch(req);
            }

            // ----------------------------------------------- TR2-S session write gate
            // One choke point instead of a check inside every handler: every write route
            // in this API is a POST, so a single place cannot be forgotten when a route is
            // added. It refuses only inside an unconfirmed rebuild window, and only when
            // session_gate.on exists - see SessionRegistry.GateEnabled for why it is OFF
            // by default rather than armed on an unverified classifier.
            if (!isGet) SessionRegistry.RequireStable(method + " " + path);

            // ---------------------------------------- plot table: map and route surface
            // Everything here reads, moves or draws on the live tactical plot
            // (MapControl), so it must run on the main thread even for the read-only
            // cases: touching a Noesis dependency property off-thread is how the
            // process dies. Writes already passed the session gate above.
            if (head == "map")
            {
                if (seg.Length < 2)
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return OnMain(delegate { return MapView.Read(); });
                }
                string mapOp = seg[1];
                if (mapOp == "units" || mapOp == "unit-screen")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject mapQuery = QueryObject(query);
                    return OnMain(delegate { return MapUnits.List(mapQuery); });
                }
                if (mapOp == "geo-to-view")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject mapQuery = QueryObject(query);
                    return OnMain(delegate { return MapUnits.GeoToView(mapQuery); });
                }
                if (mapOp == "view-to-geo")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject mapQuery = QueryObject(query);
                    return OnMain(delegate { return MapUnits.ViewToGeo(mapQuery); });
                }
                // Everything below is a POST that moves the operator's camera or changes the
                // game's selected unit (MapView.Select feeds the player's next order). While
                // HOLD is on the operator has the conn, so the map is theirs too. Unknown POST
                // ops still fall through to the 404 at the end of this block.
                bool mapWrite = mapOp == "focus" || mapOp == "rotate" || mapOp == "zoom"
                    || mapOp == "select" || mapOp == "deselect" || mapOp == "set-center";
                if (mapWrite && CommsBridge.Hold)
                    throw new ApiException(409, "held",
                        "operator HOLD is on: the map is the operator's while it is held", true);
                if (mapOp == "focus")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.Focus(req); });
                }
                if (mapOp == "rotate")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.Rotate(req); });
                }
                if (mapOp == "zoom")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.Zoom(req); });
                }
                if (mapOp == "select")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.Select(req); });
                }
                if (mapOp == "deselect")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.Deselect(req); });
                }
                if (mapOp == "set-center")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapView.SetCenter(req); });
                }
                throw new ApiException(404, "not_found", "unknown map op: " + mapOp
                    + " (expected view: /map, or /map/{focus|rotate|zoom|select|deselect|set-center|units|unit-screen|geo-to-view|view-to-geo})", false);
            }

            if (head == "plot")
            {
                if (seg.Length < 2)
                    throw new ApiException(404, "not_found",
                        "expected /plot/{course|clear|route|ruler|measure|draw|drawings|erase}", false);
                string plotOp = seg[1];
                if (plotOp == "route")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject plotQuery = QueryObject(query);
                    return OnMain(delegate { return MapPlot.Route(plotQuery); });
                }
                if (plotOp == "measure")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject plotQuery = QueryObject(query);
                    return OnMain(delegate { return MapPlot.Measure(plotQuery); });
                }
                if (plotOp == "course")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapPlot.Course(req); });
                }
                if (plotOp == "clear")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapPlot.Clear(req); });
                }
                if (plotOp == "ruler")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    // The ruler is the last write route that only touches the view: it adds a
                    // node to the map's visual tree and reparents it. Under HOLD the map is the
                    // operator's, so it is gated like draw/erase (review round 2). A dry run is
                    // still allowed - it computes the distance and adds nothing.
                    if (CommsBridge.Hold && !ReqBool(req, "validate_only", false))
                        throw new ApiException(409, "held",
                            "operator HOLD is on: plot drawings are refused until it is released", true);
                    return OnMain(delegate { return MapPlot.Ruler(req); });
                }
                // Drawing on the plotting table. Everything added here lands in the same
                // SceneMapViewModel.DrawingLayer.Graphics collection the player draws into, and
                // that collection is serialised into the auto-save - so these routes are
                // writes even when they only touch the view, and they say savedIntoAutoSave.
                if (plotOp == "draw")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapDraw.Draw(req); });
                }
                if (plotOp == "drawings")
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    JObject drawQuery = QueryObject(query);
                    // 45 s, not 15: on a cold bridge the first touch of the plot table used to
                    // spend >20 s resolving type names (one GetTypes() sweep per missing short
                    // name), so the client timed out while the main thread was still working.
                    // GameReflect now builds a name index at load, and this budget is the belt.
                    return OnMain(delegate { return MapDraw.List(drawQuery); });
                }
                if (plotOp == "erase")
                {
                    if (isGet) throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return MapDraw.Erase(req); });
                }
                throw new ApiException(404, "not_found", "unknown plot op: " + plotOp
                    + " (expected /plot/{course|clear|route|ruler|measure|draw|drawings|erase})", false);
            }
            // ---------------------------------------------------- read-only routes
            // Diagnostics that must not marshal: this answers exactly the question "is the
            // main-thread pump running for THIS copy of the assembly?" - the one question that
            // a timeout cannot answer, because a timeout looks identical either way. Dispatch
            // serves GET /diag and GET /ping inline for that reason (IsThreadFreeProbe), so
            // isMainRaw below is false on the normal path and true only if nothing hopped.
            if (head == "diag")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                JObject d = new JObject();
                d["coreIdentity"] = Core.CoreIdentity;
                d["mainThreadId"] = Core.MainThreadId;
                d["thisThreadId"] = System.Threading.Thread.CurrentThread.ManagedThreadId;
                d["isMainRaw"] = System.Threading.Thread.CurrentThread.ManagedThreadId == Core.MainThreadId;
                d["queueDepth"] = Core.QueueDepth;
                d["pumpTicks"] = Core.PumpTicks;
                d["pumpTook"] = Core.PumpTook;
                d["lastPumpAt"] = Core.LastPumpAt;
                d["scene"] = Core.SceneName;
                d["inGame"] = Core.InGame;
                d["unityVersion"] = Core.UnityVersion;
                return d;
            }

            if (head == "ping")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                return Ping();
            }

            if (head == "state")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                return OnMain(delegate { return State(); });
            }

            if (head == "taskforces")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                return OnMain(delegate { return Taskforces(); });
            }

            if (head == "tracks")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                return OnMain(delegate { return Tracks(query); });
            }

            if (head == "units")
            {
                if (seg.Length == 1)
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return OnMain(delegate { return UnitList(query); });
                }

                int id;
                if (!int.TryParse(seg[1], out id))
                    throw new ApiException(400, "bad_unit_id", "unit id must be an integer, got '" + seg[1] + "'", false);

                if (seg.Length == 2)
                {
                    if (!isGet) throw MethodNotAllowed(method, path, "GET");
                    return OnMain(delegate { return UnitDetail(id); });
                }

                if (seg.Length == 3)
                {
                    string op = seg[2];
                    if (op == "ammo" || op == "weapons" || op == "waypoints" || op == "deck" || op == "sub"
                        || op == "airborne" || op == "decktasks")
                    {
                        if (!isGet) throw MethodNotAllowed(method, path, "GET");
                        if (op == "ammo") return OnMain(delegate { return GuardedRead(id, "ammo", Ammo); });
                        if (op == "weapons") return OnMain(delegate { return GuardedRead(id, "weapons", Weapons); });
                        if (op == "deck") return OnMain(delegate { return GuardedRead(id, "deck", Deck); });
                        // T19.5: the association handle for "which aircraft actually left the deck".
                        // ObjectBase._childAircraft is the game's own list (FlightDeck.launchVehicle
                        // adds to it), so reading it is authoritative rather than inferred.
                        if (op == "airborne") return OnMain(delegate { return GuardedRead(id, "airborne", Airborne); });
                        // T19.5: the task evidence needed to abort ONE task without touching others.
                        if (op == "decktasks") return OnMain(delegate { return GuardedRead(id, "decktasks", DeckTasks); });
                        // T19.4: 'sub' is a read-only submarine systems view. It is GET-only ON
                        // PURPOSE - depth/periscope/preset commands already exist on
                        // POST /units/{id}/vertical (T19.2) and the task card forbids a second,
                        // equivalent op. A POST here therefore gets 405 with Allow: GET.
                        if (op == "sub") return OnMain(delegate { return GuardedRead(id, "sub", Sub); });
                        return OnMain(delegate { return GuardedRead(id, "waypoints", Waypoints); });
                    }
                    // PR3 (ground units): the read surface LandRead implements. Every entry it
                    // exposes takes an ObjectBase, so the id lookup runs through the same gate as
                    // every other read (GuardedRead: player-picture 404 first, then the intel
                    // stub for foreign hulls). The part name is validated BEFORE OnMain, so a
                    // typo is a 400 that offline tests can judge - the same lesson weapon_status
                    // taught when its whitelist sat behind FindUnit and turned into a 500.
                    if (op == "land")
                    {
                        if (!isGet) throw MethodNotAllowed(method, path, "GET");
                        JObject landQuery = QueryObject(query);
                        string landPart = "summary";
                        if (landQuery != null && landQuery["part"] != null)
                        {
                            landPart = ReqStr(landQuery, "part", "summary");
                            if (landPart == null) landPart = "summary";
                        }
                        if (landPart == "schema")
                        {
                            // REVIEW (round 2, important): schema returned before GuardedRead and
                            // never looked at the id, so /units/999999/land?part=schema was a 200
                            // while every other part answered 404 for the same id. The schema itself
                            // is static assembly metadata, so existence is the only question - it goes
                            // through the one gated lookup and is not redacted afterwards.
                            return OnMain(delegate
                            {
                                FindKnownUnit(id);
                                return (JToken)LandRead.Schema();
                            });
                        }
                        if (landPart != "summary" && landPart != "movement" && landPart != "damage"
                            && landPart != "systems" && landPart != "fires" && landPart != "target_points")
                            throw new ApiException(400, "bad_value",
                                "part must be one of summary, movement, damage, systems, fires, target_points or schema (got '" + landPart + "')", false);
                        string landPartFixed = landPart;
                        return OnMain(delegate
                        {
                            return GuardedRead(id, "land", delegate (int uid) { return LandOf(uid, landPartFixed); });
                        });
                    }
                    // T19.2: 'vertical' is the one path that is BOTH a read probe (GET) and a
                    // write command (POST). Putting it in the GET-only list above made the
                    // write unreachable with 405 - the dynamic suite caught that, the static
                    // checks could not, because both strings were present.
                    if (op == "vertical")
                    {
                        if (isGet) return OnMain(delegate { return GuardedRead(id, "vertical", Vertical); });
                        if (method != "POST") throw MethodNotAllowed(method, path, "GET, POST");
                        return OnMain(delegate { return UnitCommand(id, op, req); });
                    }
                    // Anything else under /units/{id}/ is a write op and must be POST.
                    if (method != "POST") throw MethodNotAllowed(method, path, "POST");
                    return OnMain(delegate { return UnitCommand(id, op, req); });
                }

                throw new ApiException(404, "not_found", "unknown units sub-path: " + path, false);
            }

            if (head == "probe")
            {
                if (!isGet) throw MethodNotAllowed(method, path, "GET");
                if (seg.Length != 2)
                    throw new ApiException(404, "not_found", "expected /probe/{ui|ui2|world|session|intel|types|members}", false);
                string what = seg[1];
                if (what == "ui") return OnMain(delegate { return ProbeUi(); });
                if (what == "ui2") return OnMain(delegate { return ProbeUi2(); });
                if (what == "world") return OnMain(delegate { return ProbeWorld(); });
                if (what == "session") return OnMain(delegate { return ProbeSession(); });
                if (what == "intel") return OnMain(delegate { return ProbeIntel(query); });
                if (what == "types") return OnMain(delegate { return ProbeTypes(query); });
            if (what == "map") return OnMain(delegate { return ProbeMap(); });
                if (what == "members") return OnMain(delegate { return ProbeTypeMembers(FilterValue(query, "type")); });
                throw new ApiException(404, "not_found", "unknown probe: " + what, false);
            }

            // ------------------------------------------------------- write routes
            if (head == "group")
            {
                if (seg.Length != 2)
                    throw new ApiException(404, "not_found", "expected /group/{op}", false);
                if (method != "POST") throw MethodNotAllowed(method, path, "POST");
                return OnMain(delegate { return GroupCommand(seg[1], req); });
            }

            if (head == "time")
            {
                if (seg.Length != 1)
                    throw new ApiException(404, "not_found", "expected /time", false);
                if (method != "POST") throw MethodNotAllowed(method, path, "POST");
                // Time acceleration changes the world every bit as much as an order does,
                // so a held fleet also stops fast-forwarding.
                if (CommsBridge.Hold)
                    throw new ApiException(409, "held",
                        "operator HOLD is on: time changes are refused until it is released",
                        true);
                return OnMain(delegate { return TimeCommand(req); });
            }

            throw new ApiException(404, "not_found", "unknown route: " + method + " " + path, false);
        }

        // Every route already runs inside Dispatch's single main-thread hop, so this is not a
        // second hop and cannot repeat work. It is an assertion: reaching a route off the main
        // thread is a bug, and a queued-and-timed-out call would hide it. The wait budget is
        // not a per-call detail - it is DeadlineMs, applied once in Dispatch.
        private static JToken OnMain(Func<object> fn)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "this route must run on the main thread; Dispatch is the only entry point",
                    false);
            object r = fn();
            return r == null ? (JToken)JValue.CreateNull() : ToToken(r);
        }

        private static JToken ToToken(object o)
        {
            if (o == null) return JValue.CreateNull();
            JToken jt = o as JToken;
            if (jt != null) return jt;
            return JToken.FromObject(o);
        }

        // ---------------------------------------------------------------- basic

        private static JObject Ping()
        {
            JObject o = new JObject();
            o["ok"] = true;
            o["mod"] = "Sea Power Agent Bridge";
            o["version"] = "0.1.0";
            o["port"] = Core.Port;
            // Cached by the main thread (see Core.ProbeCache): this handler runs on the HTTP
            // thread, and reading Application.* here is what intermittently killed the game
            // right after a fresh start with "Graphics device is null.".
            o["unity"] = Core.UnityVersion;
            o["scene"] = Core.SceneName;
            o["inGame"] = Core.InGame;
            o["cachedAt"] = "main thread (0.5 s)";
            return o;
        }

        private static JObject State()
        {
            JObject o = new JObject();
            o["paused"] = GameTime.IsPaused();
            o["timeCompression"] = GameTime.TimeCompression;
            o["missionElapsedTime"] = GameTime.missionElapsedTime;
            o["simulationTime"] = GameTime.simulationTime;
            o["simulationTick"] = GameTime.simulationTick;
            o["unityFrame"] = Time.frameCount;
            o["unityTime"] = Time.realtimeSinceStartup;
            o["inGame"] = GameMain.Instance != null;

            int total = 0, units = 0, mine = 0, enemy = 0, air = 0, surface = 0, sub = 0;
            List<ObjectBase> all = AllObjects();
            total = all.Count;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u == null) continue;
                if (!u.isUnit()) continue;
                units++;
                if (u.IsPlayerUnit) mine++;
                if (u.IsEnemyObject) enemy++;
                if (u.IsAirUnit) air++;
                if (u.IsSurfaceUnit) surface++;
                if (u.IsSubUnit) sub++;
            }
            o["objectCount"] = total;
            o["unitCount"] = units;
            o["ownUnits"] = mine;
            o["enemyUnits"] = enemy;
            o["airUnits"] = air;
            o["surfaceUnits"] = surface;
            o["subUnits"] = sub;
            return o;
        }

        // ---------------------------------------------------------------- units

        // Internal, not private: GameReflect.Units() reads the same list. Two reflection paths to
        // "which objects exist" is exactly the kind of duplicate that made /map/units return zero
        // while /units returned nineteen.

        /// <summary>Layer 1 gives the trigger registry the whole world; the policy layer above
        /// decides what a caller is allowed to see. Keeping this separate from AllObjects()
        /// means an intel-aware caller can be added later without touching the triggers.</summary>
        internal static System.Collections.IEnumerable AllObjectsForWatch()
        {
            return AllObjects();
        }

        /// <summary>World generation is what makes a trigger's answer meaningful: after a reload
        /// the unit ids mean different objects, so a rule registered before it must not fire.</summary>
        internal static string WorldGenerationForWatch()
        {
            try
            {
                Newtonsoft.Json.Linq.JObject rep = SessionRegistry.Report();
                Newtonsoft.Json.Linq.JToken g = rep == null ? null : rep["worldGeneration"];
                return g == null ? "unknown" : g.ToString();
            }
            catch (Exception) { return "unknown"; }
        }
        internal static List<ObjectBase> AllObjects()
        {
            List<ObjectBase> result = new List<ObjectBase>();
            GameMain gm = GameMain.Instance;
            if (gm == null) return result;
            if (_objectsField == null)
                _objectsField = typeof(GameMain).GetField("_objects", PrivInst);
            if (_objectsField == null) return result;
            List<ObjectBase> raw = _objectsField.GetValue(gm) as List<ObjectBase>;
            if (raw != null) result = raw;
            return result;
        }

        // Addressed-resource lookup shared by every /units/{id}/... route. The miss is a
        // request-shape problem (T01: unknown resource -> 404 not_found), so it is an
        // ApiException and not a bare Exception; the generic handler would otherwise turn a
        // typo'd id into HTTP 500 route_error.
        private static ObjectBase FindUnit(int id)
        {
            List<ObjectBase> all = AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u != null && u.UniqueID == id) return u;
            }
            throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);
        }

        /// <summary>One gated lookup for every route that addresses a unit by id.
        ///
        /// REVIEW (round 2, blocker 1/2/3/6): /plot/route, /map/select, /plot/draw and attack's
        /// target each resolved ids with GameReflect.UnitById and stopped at "does it exist". That
        /// answered three questions nobody should be able to ask: a hull's true waypoints, a hull's
        /// true name and coordinate, and - through three different 404 wordings - whether an id is
        /// merely absent or present-but-undetected. Existence and intel are one question (I8), so
        /// they get one implementation: absent and undetected answer with the same code and text.</summary>
        internal static ObjectBase FindKnownUnit(int id)
        {
            ObjectBase u = FindUnit(id);
            bool known = false;
            Exception intelError = null;
            try { known = IntelScope.PlayerKnows(u); }
            catch (Exception ex) { intelError = ex; known = false; }
            // REVIEW (round 3, residual risk 7): this used to swallow EVERY exception and answer
            // 404, so a bridge fault - an off_main_thread refusal, a reflection break, an ECall on a
            // thread that is not the render thread - was reported as "no such unit". The caller
            // cannot tell that lie from the truth, and the fault then gets diagnosed as a user
            // error. Only an answer we actually have may become a 404; a fault keeps its own code
            // so it shows up as a bridge problem instead of as a missing ship.
            if (!known)
            {
                if (intelError != null)
                    throw new ApiException(500, "intel_check_failed",
                        "could not decide whether unit " + id + " is on the player's picture: "
                        + intelError.GetType().Name + ": " + intelError.Message, true);
                throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);
            }
            return u;
        }

        private static JToken UnitList(string query)
        {
            string side = FilterValue(query, "side");
            string kind = FilterValue(query, "kind");
            // 'all' used to accept only the literal "1", so ?all=true silently kept
            // filtering out missiles / markers. Accept the usual spellings.
            string allVal = FilterValue(query, "all");
            bool wantAll = allVal == "1" || allVal == "true" || allVal == "yes";
            // P-30: ?all=1 also lists non-unit objects (missiles in flight, decoys, script
            // markers). Those are hidden objects rather than units the player's plot shows,
            // so the product entry refuses the request; diagnostics keeps the old behaviour.
            // V2 DECISION (2026-11-05, owner): no intel isolation, so the diagnostics
            // file is not a permission gate any more. ?all=1 simply lists everything.
            if (false && wantAll && !MiniHttp.DiagnosticsEnabled)
                throw new ApiException(403, "scope_denied",
                    "/units?all=1 also lists non-unit objects (missiles, decoys, markers) that the player's plot never showed. It is diagnostics-only (create diagnostics.on in the mod folder). Omit 'all' to list units only.", false);
            // 6: a typo used to be a silent no-op (kind=nonsense returned everything). "no
            // filter" and "a filter that matched nothing" must not look the same to a caller.
            if (!string.IsNullOrEmpty(kind) && kind != "air" && kind != "surface" && kind != "sub" && kind != "land")
                throw new ApiException(400, "bad_value",
                    "kind " + kind + " is not one we list; use air, surface, sub or land", false);
            bool onlyUnits = !wantAll;
            bool showDestroyed = FilterValue(query, "destroyed") == "1";
            bool reveal = IntelRevealEnabled();

            JArray arr = new JArray();
            List<ObjectBase> all = AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u == null) continue;
                if (onlyUnits && !SafeFlag(u, "unit")) continue;
                if (SafeFlag(u, "destroyed") && !showDestroyed) continue;

                // TR2-I / ISS-003: this list is the agent's picture of what the player can see,
                // not an inventory of the world. An object the player never detected used to
                // appear here with its id, class and side - that is how three enemy submarines
                // were readable while /probe/intel reported zero submarine tracks. They are now
                // skipped outright: even a COUNT of them is intel ("we know of four"), which is
                // why nothing here counts the ones it skipped. (REVIEW round 2, hint: the old
                // counter was incremented and never read, one edit away from being reported.)
                if (!IntelScope.PlayerKnows(u)) continue;

                if ((side == "mine" || side == "player" || side == "own") && !SafeFlag(u, "mine")) continue;
                if (side == "enemy" && !SafeFlag(u, "enemy")) continue;
                if (kind == "air" && !SafeFlag(u, "air")) continue;
                if (kind == "surface" && !SafeFlag(u, "surface")) continue;
                if (kind == "sub" && !SafeFlag(u, "sub")) continue;
                if (kind == "land" && !LandUnits.IsLand(u)) continue;

                JObject uj = UnitJson(u);
                // F-A: with the reveal switch on, the caller must be able to tell that what it
                // is reading came from a development switch rather than from the player's sensors.
                uj["intelPolicy"] = VisibilityOf(u);
                arr.Add(uj);
            }
            JObject o = new JObject();
            o["count"] = arr.Count;
            o["units"] = arr;
            o["intelScope"] = "player";
            o["revealSwitch"] = reveal;
            return o;
        }

        private static JToken UnitDetail(int id)
        {
            // TR2-I / ISS-003, second half: answering for an object the player never detected
            // confirms its existence, its class and its id even if every field is redacted.
            // The list route hides such objects; this route must not become the side door.
            // REVIEW (round 4): this used to be a second hand-written copy of FindUnit +
            // PlayerKnows + the 404, which is how the fault-honesty fix only ever reached one of
            // the copies. One gate, one place to fix it.
            ObjectBase u = FindKnownUnit(id);
            string vis = VisibilityOf(u);
            bool full = IntelPolicy.AllowsDetail(vis);
            JObject o = UnitJson(u);
            o["intelPolicy"] = vis;
            // F-A: the marker has to say WHY this is readable. With the reveal switch on every
            // foreign hull reports "full_by_operator_switch" (IntelPolicy's own constant) and the
            // caller can stop trusting the picture.
            o["revealSwitch"] = IntelRevealEnabled();
            // REVIEW (round 3): everything below describes a hull your side does not control, so it
            // belongs behind the same gate as the rest. className was the last survivor here - it
            // was written unconditionally, nine lines under the null that UnitJson had just put in
            // place and ten above `if (full)`, so one response could carry identityRedacted:true,
            // identityNote:"this hull's name and class are withheld" and a real className. The
            // control flags leak the same thing in weaker form: "formationWingman" tells an
            // observer how the enemy is organised. Same gate, and the withheld branch nulls them
            // explicitly rather than relying on UnitJson to have done it.
            if (full)
            {
                o["accepOrdersFromPlayer"] = u.AcceptsOrdersFromPlayer;
                o["controllable"] = u.IsControllable;
                o["hasControl"] = u.HasControl;
                o["formationLeader"] = u.IsFormationLeader;
                o["formationWingman"] = u.IsFormationWingman;
                o["className"] = SafeStr(u.ClassName);
            }
            else
            {
                o["accepOrdersFromPlayer"] = null;
                o["controllable"] = null;
                o["hasControl"] = null;
                o["formationLeader"] = null;
                o["formationWingman"] = null;
                o["className"] = null;
            }
            // REVIEW (round 2, blocker 5): maxForwardKnots / maxForwardSubmergedKnots / emcon /
            // altitude used to be written here, outside the `if (full)` gate below - so a detected
            // foreign submarine reported its ceiling, its EMCON and its real Altitude (which IS its
            // depth), and that altitude write overwrote the null UnitJson had just produced: one
            // response said positionSource:"withheld" and carried the depth. Internal state belongs
            // behind the same gate as the rest of it.
            // TR2-I: route and order intent are internal state. A player's plot shows a
            // classification and a position, never what a foreign hull is about to do next,
            // so these read null rather than being inferred from the position history.
            if (full)
            {
                o["maxForwardKnots"] = u.MaxForwardSpeedInKnots;
                o["maxForwardSubmergedKnots"] = u.MaxForwardSpeedSubmergedInKnots;
                o["emcon"] = SafeStr(Rx(u, "Emcon"));
                o["altitude"] = SafeNum(Rx(u, "Altitude"));
                o["hasWaypoints"] = u.HasWaypoints();
                o["waypointCount"] = u.GetNumberOfWaypoints();
                o["currentWaypoint"] = SafeStr(u.GetCurrentActiveWaypointName());
                o["distToWaypointNm"] = SafeNum(u.GetDistToCurrentActiveWaypointInMiles());
                o["orderText"] = SafeStr(Rx(u, "CurrentOrderText"));
                o["orderFeedback"] = SafeStr(Rx(u, "OrderFeedbackText"));
            }
            else
            {
                // Position-derived fields stay as UnitJson wrote them (null + positionSource
                // "withheld"); nothing here may put a real number back.
                o["maxForwardKnots"] = null;
                o["maxForwardSubmergedKnots"] = null;
                o["emcon"] = null;
                // altitude is re-nulled explicitly rather than trusted to survive from UnitJson:
                // the reviewer's blocker was exactly an altitude write that landed after the
                // redaction, and "the other function already nulled it" is the kind of assumption
                // that breaks silently the next time the two functions are reordered.
                o["altitude"] = null;
                o["hasWaypoints"] = null;
                o["waypointCount"] = null;
                o["currentWaypoint"] = null;
                o["distToWaypointNm"] = null;
                o["orderText"] = null;
                o["orderFeedback"] = null;
                o["orderRedacted"] = true;
            }
            return o;
        }

        private static JObject UnitJson(ObjectBase u)
        {
            JObject o = new JObject();
            o["id"] = u.UniqueID;
            // ?all=1 exposes missiles / markers / projectiles whose DisplayNameFull and
            // ClassName can throw rather than return null, so go through Rx (which catches)
            // and mark what kind of object this actually is.
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            o["className"] = SafeStr(Rx(u, "ClassName"));
            o["nation"] = SafeStr(Rx(u, "Nation"));
            o["isUnit"] = SafeFlag(u, "unit");
            o["mine"] = SafeFlag(u, "mine");
            o["enemy"] = SafeFlag(u, "enemy");
            o["destroyed"] = SafeFlag(u, "destroyed");
            o["air"] = SafeFlag(u, "air");
            o["surface"] = SafeFlag(u, "surface");
            o["sub"] = SafeFlag(u, "sub");
            // LAND-1: get_IsSurfaceUnit() is `is Vessel || is LandUnit`, so a coastal battery
            // arrives here as surface:true with nothing else to go on. isLand/landSubType are the
            // two facts that let a caller tell a SAM site from a destroyer without guessing.
            bool isLand = false;
            try { isLand = LandUnits.IsLand(u); } catch (Exception) { isLand = false; }
            o["isLand"] = isLand;
            o["landSubType"] = isLand ? (JToken)new JValue(LandUnits.SubTypeName(u)) : JValue.CreateNull();

            object posObj = Rx(u, "Position");
            if (posObj is GeoPosition)
            {
                GeoPosition gp = (GeoPosition)posObj;
                o["lat"] = Math.Round(gp.Latitude, 6);
                o["lon"] = Math.Round(gp.Longitude, 6);
            }
            o["heading"] = SafeNum(Rx(u, "Heading"));
            o["speedKts"] = SafeNum(Rx(u, "Velocity"));
            o["altitude"] = SafeNum(Rx(u, "Altitude"));
            // ISS-003 (second half), applied in the builder every read route shares: existence is
            // decided by IntelScope before we get here; the question left is WHERE. A detected
            // foreign hull's coordinates are still intel, so the row stays and the position goes -
            // the same rule /map/units applies. Without this, /units answered a 6-decimal truth
            // position for every contact on the plot, which is exactly what ISS-003 reported.
            bool posAllowed;
            try { posAllowed = IntelPolicy.AllowsDetail(VisibilityOf(u)); }
            catch (Exception) { posAllowed = false; }
            if (!posAllowed)
            {
                o["lat"] = JValue.CreateNull();
                o["lon"] = JValue.CreateNull();
                o["heading"] = JValue.CreateNull();
                // OI-4: altitude was written before this gate and survived it. A withheld row
                // must not carry a true altitude any more than a true longitude.
                o["altitude"] = JValue.CreateNull();
                o["speedKts"] = JValue.CreateNull();
                o["positionSource"] = "withheld";
                o["positionNote"] = "contact only: this hull is not on your own side and the " + IntelPolicy.RevealSwitchFile
                    + " switch is off, so its position is withheld. /tracks reports the plotted estimate instead.";
                // REVIEW (round 2, important 8): the object layer used to name a detected foreign
                // hull while /tracks reported Unknown for the same contact - two routes answering the
                // same question differently, which is the ISS-004 defect the track layer was built to
                // fix. Identity now follows the same answer as position: the row stays, the name goes,
                // and /tracks remains the route that says what the player has identified.
                o["name"] = JValue.CreateNull();
                o["className"] = JValue.CreateNull();
                o["nation"] = JValue.CreateNull();
                o["identityRedacted"] = true;
                o["identityNote"] = "a contact is not an identification: this hull's name and class are"
                    + " withheld until your side identifies it. GET /tracks reports the classification"
                    + " the plot actually shows.";
            }
            else
            {
                o["positionSource"] = "truth";
            }
            return o;
        }

        // B14: InsertEngageTask returning normally does NOT mean the game fired anything.
        // A torpedo aimed at a surface hull is dropped silently; the magazine never moves.
        // The only local proof that a shot left the tube is the round count going down,
        // so snapshot it around the call.
        private static int AmmoCount(ObjectBase u, string ammoId)
        {
            if (u == null || ammoId == null) return -1;
            try
            {
                UniRx.ReactiveDictionary<string, int> dict = u.AmmunitionAmountDictionary;
                if (dict == null) return -1;
                foreach (KeyValuePair<string, int> kv in dict)
                {
                    if (kv.Key == ammoId) return kv.Value;
                }
                return 0;
            }
            catch (Exception) { return -1; }
        }

        // The flag properties (IsPlayerUnit / IsEnemyObject / IsAirUnit / IsSurfaceUnit /
        // IsSubUnit / IsDestroyed) are fine on a real unit but throw on the non-unit objects
        // that ?all=1 exposes (missiles, markers, projectiles). Reading them unguarded is what
        // made GET /units?all=1 return a NullReferenceException while plain GET /units worked.
        internal static bool SafeFlag(ObjectBase u, string which)
        {
            if (u == null) return false;
            try
            {
                if (which == "mine") return u.IsPlayerUnit;
                if (which == "enemy") return u.IsEnemyObject;
                if (which == "air") return u.IsAirUnit;
                if (which == "surface") return u.IsSurfaceUnit;
                if (which == "sub") return u.IsSubUnit;
                if (which == "destroyed") return u.IsDestroyed;
                if (which == "unit") return u.isUnit();
            }
            catch (Exception) { }
            return false;
        }

        private static JToken Ammo(int id)
        {
            ObjectBase u = FindUnit(id);
            JObject o = new JObject();
            o["id"] = u.UniqueID;
            JObject ammo = new JObject();
            UniRx.ReactiveDictionary<string, int> dict = u.AmmunitionAmountDictionary;
            if (dict != null)
            {
                foreach (KeyValuePair<string, int> kv in dict)
                {
                    if (kv.Key == null) continue;
                    ammo[kv.Key] = kv.Value;
                }
            }
            o["ammo"] = ammo;
            return o;
        }

        private static JToken Waypoints(int id)
        {
            ObjectBase u = FindUnit(id);
            JArray arr = new JArray();
            try
            {
                object raw = u.GetAllWaypointsGeoPositions(false);
                System.Collections.IList list = raw as System.Collections.IList;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object item = list[i];
                        object gpObj = Member(item, "Item1");
                        object hsObj = Member(item, "Item2");
                        JObject w = new JObject();
                        w["index"] = i;
                        if (gpObj is GeoPosition)
                        {
                            GeoPosition gp = (GeoPosition)gpObj;
                            w["lat"] = Math.Round(gp.Latitude, 6);
                            w["lon"] = Math.Round(gp.Longitude, 6);
                        }
                        w["heightState"] = SafeStr(hsObj);
                        arr.Add(w);
                    }
                }
            }
            catch (Exception) { arr = new JArray(); }
            JObject o = new JObject();
            o["id"] = u.UniqueID;
            o["count"] = arr.Count;
            o["waypoints"] = arr;
            return o;
        }

        // ---------------------------------------------------------------- commands

        // A refused engagement used to surface as ok:true once it passed through the group
        // layer, so a caller saw success for a shot the game never took. Detect the flag.
                /// <summary>
        /// How many engage tasks are queued on a hull, or -1 when the list cannot be read.
        /// -1 is deliberately distinguishable from 0: "nothing queued" and "cannot tell" are
        /// different answers and the caller must not have to guess which one it got.
        /// </summary>
        private static int TaskCount(ObjectBase u)
        {
            try
            {
                System.Collections.IList list = Member(u, "_currentEngageTasks") as System.Collections.IList;
                if (list == null) return -1;
                return list.Count;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// D1: the proof that an engage order became a task. InsertEngageTask returns the task
        /// it queued, and EngageTask keeps the request in _ammoId / _shotsToFire / _targetObject,
        /// so the reply can show what the game actually built instead of only that the method
        /// was called.
        /// </summary>
        private static JObject EngageProof(ObjectBase u, object task, string ammoId, int shots)
        {
            JObject p = new JObject();
            p["declaredType"] = task == null ? null : task.GetType().FullName;
            object rawAmmo = Member(task, "_ammoId");
            string gotAmmo = rawAmmo == null ? null : Convert.ToString(rawAmmo);
            object rawShots = Member(task, "_shotsToFire");
            int gotShots = rawShots == null ? -1 : Convert.ToInt32(rawShots);
            object tgt = Member(task, "_targetObject");
            p["ammoId"] = gotAmmo;
            p["shotsToFire"] = gotShots;
            object rawTargetId = tgt == null ? null : Member(tgt, "UniqueID");
            p["targetObjectId"] = rawTargetId == null
                ? JValue.CreateNull()
                : (JToken)new JValue(Convert.ToInt32(rawTargetId));
            p["ammoMatches"] = gotAmmo != null && gotAmmo.Equals(ammoId, StringComparison.Ordinal);
            // shots = -1 means "let the game decide", so comparing it to -1 would be a false
            // alarm; only an explicit count is checked.
            p["shotsMatch"] = shots < 0 ? JValue.CreateNull() : (JToken)new JValue(gotShots == shots);
            p["queuedAfter"] = TaskCount(u);
            return p;
        }

private static bool IsRefused(JToken result)
        {
            JObject o = result as JObject;
            if (o == null) return false;
            JToken r = o["refused"];
            return r != null && r.Type == JTokenType.Boolean && (bool)r;
        }

        // SeaPower.Taskforce.RelationshipTo is the game's own friend-or-foe answer.
        // SeaPower.RelationsState = Unknown, Hostile, Neutral, Friendly.
        /// <summary>
        /// Collapses whatever RelationshipOf produced -- including null, because a unit had no
        /// taskforce or the reflection threw -- into the four cases the decision table knows.
        /// Anything unrecognised becomes Unknown, which refuses by default.
        /// </summary>
        public static string NormalizeRelation(string relation)
        {
            if (relation == "Friendly" || relation == "Self") return "Friendly";
            if (relation == "Hostile") return "Hostile";
            if (relation == "Neutral") return "Neutral";
            return "Unknown";
        }

        /// <summary>
        /// Pure decision table: no game objects, so every cell can be tested offline.
        /// Returns null when the shot is allowed, otherwise the refusal reason with a
        /// {side} placeholder the caller fills in. friendlyHardStop reports the one case
        /// force must never override.
        /// </summary>
        public static string RelationVerdict(string relation, bool allowNeutral, bool force, out bool friendlyHardStop)
        {
            friendlyHardStop = false;
            string relNorm = NormalizeRelation(relation);
            if (relNorm == "Friendly")
            {
                friendlyHardStop = true;
                return "target is friendly ({side}); the game will not engage its own side";
            }
            if (relNorm == "Neutral" && !allowNeutral)
                return "target is neutral ({side}); pass allow_neutral=true to engage a neutral contact on purpose";
            if (relNorm == "Unknown" && !force)
                return "target relationship is unknown; pass force=true to engage an unclassified contact";
            return null;
        }
        private static string RelationshipOf(ObjectBase a, ObjectBase b)
        {
            try
            {
                SeaPower.Taskforce ta = Member(a, "_taskforce") as SeaPower.Taskforce;
                SeaPower.Taskforce tb = Member(b, "_taskforce") as SeaPower.Taskforce;
                if (ta == null || tb == null) return null;
                return Convert.ToString(ta.RelationshipTo(tb));
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------- TR2-I: observer scoped reads
        // P-29/P-38: GET /units/{id}/ammo and /sub answered with an enemy hull's magazine
        // and battery. Nothing in the game's own plot shows those, so handing them to an
        // agent turns a prompt injection into a force multiplier. Every read of *internal*
        // state goes through VisibilityOf, and the only thing that can lift it is a file an
        // operator creates - not a flag the caller can pass.
        internal static bool IntelRevealEnabled()
        {
            try
            {
                return System.IO.File.Exists(System.IO.Path.Combine(Core.ModDir(), IntelPolicy.RevealSwitchFile));
            }
            catch (Exception) { return false; }
        }

        private static ObjectBase FirstPlayerUnit()
        {
            System.Collections.Generic.List<ObjectBase> all = AllObjects();
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u == null) continue;
                try { if (u.isUnit() && u.IsPlayerUnit) return u; } catch (Exception) { }
            }
            return null;
        }

        internal static string RelationToPlayer(ObjectBase u)
        {
            if (u == null) return null;
            if (SafeFlag(u, "mine")) return "Friendly";
            ObjectBase me = FirstPlayerUnit();
            if (me == null) return null;
            return RelationshipOf(u, me);
        }

        internal static string VisibilityOf(ObjectBase u)
        {
            if (u == null) return IntelPolicy.Redacted;
            return IntelPolicy.Decide(SafeFlag(u, "mine"), RelationToPlayer(u), IntelRevealEnabled());
        }

        /// <summary>
        /// Wraps a read that exposes internal state. Own and allied hulls read normally;
        /// everything else gets identity plus a statement of what was withheld.
        /// </summary>
        private static JToken GuardedRead(int id, string what, Func<int, JToken> read)
        {
            // TR2-I / ISS-003: a redacted stub still answers "this id exists and this is its
            // class". For an object the player never detected that answer is itself the leak, so
            // unknown objects get the same 404 as the detail route instead of a stub. I8: same
            // answer as a missing id - the redaction itself must not become the oracle that says
            // "this id is real". REVIEW (round 4): one gate, shared with UnitDetail.
            ObjectBase u = FindKnownUnit(id);
            string vis = VisibilityOf(u);
            if (IntelPolicy.AllowsDetail(vis)) return read(id);
            return IntelPolicy.RedactedStub(UnitJson(u), what, vis);
        }

        /// <summary>
        /// The ground-unit read parts. Reached only through GuardedRead, so existence and the
        /// player-picture gate have already run; the part name was validated at the route.
        /// </summary>
        private static JToken LandOf(int id, string part)
        {
            ObjectBase u = FindUnit(id);
            if (part == "movement") return LandRead.Movement(u);
            if (part == "damage") return LandRead.Damage(u);
            if (part == "systems") return LandRead.Systems(u);
            if (part == "fires") return LandRead.Fires(u);
            if (part == "target_points") return LandRead.TargetPoints(u);
            return LandRead.Summary(u);
        }

        // Fan one command out over a set of units: explicit ids, a taskforce, or a side.
        // Lets an agent order a whole formation instead of poking one hull at a time.
        private static JToken GroupCommand(string op, JObject req)
        {
            // "assign" mode: instead of every unit firing at one shared target, hand each
            // unit its own target. This is what real fire distribution looks like.
            //   {"op":"attack","assign":[{"unit":9,"target":52},{"unit":1,"target":53}]}
            JArray assign = req["assign"] as JArray;
            if (assign != null)
            {
                // assign used to be honoured whatever op was asked for, so a stray
                // POST /group/speed carrying an assign array still opened fire. Gate it.
                if (op != "attack")
                    throw new ApiException(400, "bad_op",
                        "assign is only valid for op=attack, got '" + op + "'", false);

                // R1: the WHOLE batch is validated before a single native call runs.
                // A malformed entry used to be skipped (pair == null -> continue) or
                // silently coerced, so half the fleet could fire while the caller thought
                // it had asked for a dry run.
                if (assign.Count == 0)
                    throw new ApiException(400, "empty_assign",
                        "assign was provided but is empty; refusing to fan out over nothing", false);

                bool batchValidateOnly = ReqBool(req, "validate_only", false);

                // Rework 2.2: a batch-level dry run DOMINATES. A per-entry validate_only:false
                // used to downgrade that entry into live fire while the batch still reported
                // validate_only:true - the caller asked for a dry run and got a shot away.
                // Rework 2.3: build and structurally validate EVERY child request here, not
                // just unit/target. Validating lazily let entry #1 fire before entry #2 was
                // found to have a bad shots type.
                string[] pairAllowed = new string[] {
                    "unit", "target", "ammo", "shots", "allow_neutral", "auto_ammo", "force", "validate_only"
                };
                string[] subAllowed = AllowedFieldsFor("attack");
                List<JObject> subs = new List<JObject>();
                List<int> subUnits = new List<int>();
                List<int> subTargets = new List<int>();
                for (int i = 0; i < assign.Count; i++)
                {
                    JObject pv = assign[i] as JObject;
                    if (pv == null)
                        throw new ApiException(400, "bad_assign",
                            "assign[" + i + "] is not an object", false);

                    // Unknown keys in an entry are an error: they used to be dropped while the
                    // child request was rebuilt, so a typo looked like a successful command.
                    foreach (JProperty prop in pv.Properties())
                    {
                        if (Array.IndexOf(pairAllowed, prop.Name) >= 0) continue;
                        throw new ApiException(400, "unknown_field",
                            "assign[" + i + "] has unknown field '" + prop.Name + "'", false);
                    }

                    if (pv["unit"] == null || pv["unit"].Type != JTokenType.Integer)
                        throw new ApiException(400, "bad_assign",
                            "assign[" + i + "].unit must be an integer", false);
                    if (pv["target"] == null || pv["target"].Type != JTokenType.Integer)
                        throw new ApiException(400, "bad_assign",
                            "assign[" + i + "].target must be an integer", false);
                    int probeUnit = (int)pv["unit"];
                    JObject child = new JObject();
                    child["target"] = pv["target"];
                    CopyOpt(pv, req, child, "ammo");
                    CopyOpt(pv, req, child, "shots");
                    CopyOpt(pv, req, child, "allow_neutral");
                    CopyOpt(pv, req, child, "auto_ammo");
                    CopyOpt(pv, req, child, "force");

                    bool subValidateOnly;
                    if (batchValidateOnly)
                    {
                        JToken own = pv["validate_only"];
                        if (own != null && own.Type == JTokenType.Boolean && !(bool)own)
                            throw new ApiException(400, "assign_validate_conflict",
                                "assign[" + i + "].validate_only=false conflicts with the batch-level validate_only=true; "
                                + "a batch is either a dry run or it is not", false);
                        subValidateOnly = true;
                    }
                    else
                    {
                        subValidateOnly = ReqBool(pv, "validate_only", false);
                    }
                    child["validate_only"] = subValidateOnly;

                    // Runs the same shape checks UnitCommand would run, but before anything is
                    // allowed to touch the game.
                    RejectUnknownFields(child, "attack", subAllowed);
                    RequireField(child, "attack", "target");

                    // B1 (round 3 review): building a complete subs list is NOT the same as
                    // validating every child. Field TYPE/RANGE checks live in
                    // ValidateUnitCommandFields and used to run only inside UnitCommand, i.e.
                    // during the execution loop - so entry #2 with shots:"oops" was caught
                    // after entry #1 had already fired real ordnance. Run the exact same
                    // validator here, while the batch is still pure data.
                    ValidateUnitCommandFields("attack", child);

                    subs.Add(child);
                    subUnits.Add(probeUnit);
                    subTargets.Add((int)pv["target"]);
                }

                // Phase 2: object existence. Deliberately OUTSIDE the build loop so every
                // pure-data check (unknown field / type / range / cross-field conflict) has
                // already run to completion. Only a batch with zero data errors gets this far,
                // which is why a malformed batch never reaches the game object layer at all.
                for (int i = 0; i < subUnits.Count; i++)
                {
                    // REVIEW (round 3, important 2 / hint 12): the null test could never fire
                    // because FindUnit throws instead of returning null; what it DID do was answer
                    // the existence question before the ownership question. Same gate, same
                    // wording family as every other read, so an undetected hull reads as absent.
                    FindKnownUnit(subUnits[i]);
                }

                JArray ares = new JArray();
                int aok = 0;
                for (int i = 0; i < subs.Count; i++)
                {
                    // Test seam: the offline harness asserts this stays 0 when any child is
                    // malformed, which is the only way to prove "rejected before execution"
                    // without firing real ordnance at a live game.
                    System.Threading.Interlocked.Increment(ref AssignExecuteCount);
                    int uid = subUnits[i];
                    int tid = subTargets[i];
                    JObject item = new JObject();
                    item["unit"] = uid;
                    item["target"] = tid;
                    try
                    {
                        JToken subRes = UnitCommand(uid, "attack", subs[i]);
                        item["result"] = subRes;
                        bool subOk = !IsRefused(subRes);
                        item["ok"] = subOk;
                        if (subOk) aok++;
                    }
                    catch (ApiException ae)
                    {
                        // A batch must report partial success, but it must not swallow WHY:
                        // "held" and "wrong_unit_type" are different answers to different
                        // questions, and a caller that only sees ok:false cannot tell them
                        // apart (or tell an operator HOLD from a bad hull).
                        item["ok"] = false;
                        item["code"] = ae.Code;
                        item["error"] = ae.Message;
                    }
                    catch (Exception ex)
                    {
                        item["ok"] = false;
                        item["error"] = ex.Message;
                    }
                    ares.Add(item);
                }
                JObject aout = new JObject();
                aout["action"] = "group";
                aout["op"] = "attack";
                aout["mode"] = "assign";
                aout["validate_only"] = batchValidateOnly;
                aout["count"] = ares.Count;
                aout["succeeded"] = aok;
                aout["failed"] = ares.Count - aok;
                aout["results"] = ares;
                return aout;
            }

            List<ObjectBase> targets = new List<ObjectBase>();
            // declared here, not inside one scope branch: both branches report the same skipped
            // bucket, so the response shape cannot depend on which scope was asked for.
            int skippedUnknown = 0;

            JArray ids = req["ids"] as JArray;
            if (ids != null)
            {
                // An empty ids list used to fall through to the side branch and fan the
                // command out over the whole fleet. Treat it as a caller mistake instead.
                if (ids.Count == 0)
                    throw new ApiException(400, "empty_scope",
                        "ids was provided but is empty; refusing to widen the scope", false);
                for (int i = 0; i < ids.Count; i++)
                {
                    int one = -1;
                    try { one = (int)ids[i]; }
                    catch (Exception)
                    {
                        throw new ApiException(400, "bad_scope",
                            "ids[" + i + "] is not an integer", false);
                    }
                    // I8 again, and this one was missed in round 2: FindUnit distinguished
                    // "no such unit" from "exists but you have not detected it", so
                    // {"ids":[999999]} and {"ids":[<a real undetected enemy>]} answered
                    // differently - an id oracle that survives every route-level gate. Both
                    // land in the same skipped bucket the plain branch already reports.
                    ObjectBase found = null;
                    try { found = FindKnownUnit(one); }
                    catch (ApiException) { found = null; }
                    if (found == null) { skippedUnknown++; continue; }
                    targets.Add(found);
                }
            }
            else
            {
                string tfName = ReqStr(req, "tf", null);
                string side = ReqStr(req, "side", null);
                // No default: silently assuming "mine" hid a caller that forgot the scope.
                if (tfName == null && side == null)
                    throw new ApiException(400, "missing_scope",
                        "group command needs one of ids / tf / side", false);
                List<ObjectBase> all = AllObjects();
                for (int i = 0; i < all.Count; i++)
                {
                    ObjectBase u = all[i];
                    if (u == null || !u.isUnit() || u.IsDestroyed) continue;
                    if (tfName != null)
                    {
                        string n = TaskforceName(Member(u, "_taskforce"));
                        if (n == null || n != tfName) continue;
                    }
                    else if (side == "mine" || side == "player" || side == "own")
                    {
                        if (!u.IsPlayerUnit) continue;
                    }
                    else if (side == "enemy")
                    {
                        if (!u.IsEnemyObject) continue;
                    }
                    else if (side == "all")
                    {
                        // explicit opt-in to everything
                    }
                    else
                    {
                        throw new ApiException(400, "bad_scope",
                            "unknown side '" + side + "'; use mine / enemy / all", false);
                    }
                    targets.Add(u);
                }
                if (targets.Count == 0)
                    throw new ApiException(409, "empty_scope",
                        "the scope matched no live units", true);
            }

            // Rework 3: UnitCommand applies a per-op field whitelist, so the scope keys must
            // not travel with the per-unit payload. Handing it the raw request made every
            // legitimate batch command fail with unknown_field ({"ids":[1],"knots":5} was
            // rejected because "ids" is not a speed parameter).
            JObject unitReq = new JObject();
            foreach (JProperty prop in req.Properties())
            {
                if (prop.Name == "ids" || prop.Name == "tf" || prop.Name == "side" || prop.Name == "assign") continue;
                unitReq[prop.Name] = prop.Value;
            }

            JArray results = new JArray();
            int okCount = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                ObjectBase u = targets[i];
                // REVIEW (round 2, blocker 4): the receipt used to lead with the hull's unique id
                // and its real name and only then discover that the command was not allowed. That
                // turned POST /group/speed {"side":"enemy"} into a full enemy order of battle -
                // unique ids, real names, one line each - without ever touching IntelScope.
                // A batch that writes can only ever succeed on the player's own units, so anything
                // the player cannot see is not a target and does not get a row.
                bool known = false;
                try { known = IntelScope.PlayerKnows(u); }
                catch (Exception) { known = false; }
                if (!known)
                {
                    skippedUnknown++;
                    continue;
                }
                JObject item = new JObject();
                item["id"] = u.UniqueID;
                string itemVis = VisibilityOf(u);
                // Identity follows the same policy /tracks uses: a detected hull the player has not
                // identified reports no name, rather than a name on the /units side and Unknown on
                // the track side for the same contact (ISS-004).
                if (IntelPolicy.AllowsDetail(itemVis))
                {
                    item["name"] = SafeStr(Member(u, "Name"));
                }
                else
                {
                    item["name"] = JValue.CreateNull();
                    item["intelPolicy"] = itemVis;
                }
                try
                {
                    JToken res = UnitCommand(u.UniqueID, op, unitReq);
                    item["result"] = res;
                    bool thisOk = !IsRefused(res);
                    item["ok"] = thisOk;
                    if (thisOk) okCount++;
                }
                catch (ApiException ae)
                {
                    item["ok"] = false;
                    item["code"] = ae.Code;
                    item["error"] = ae.Message;
                }
                catch (Exception ex)
                {
                    item["ok"] = false;
                    item["error"] = ex.Message;
                }
                results.Add(item);
            }

            JObject o = new JObject();
            o["action"] = "group";
            o["op"] = op;
            // REVIEW (round 3, residual risk 8): "count" next to "matched" invited the reading
            // "count is how many I asked for". It never was - it is how many rows were built.
            // Three names, three numbers, no overlap: matched (the scope selected this many),
            // attempted (a row was built for this many), succeeded (the command was accepted).
            o["matched"] = targets.Count;
            o["attempted"] = results.Count;
            // Rework 3: the plain branch returned no failed count, so BusinessOk saw a batch
            // where every unit had errored as a success. (This assignment used to appear twice.)
            o["succeeded"] = okCount;
            o["failed"] = results.Count - okCount;
            if (skippedUnknown > 0)
                o["skippedUndetected"] = skippedUnknown;
            o["results"] = results;
            return o;
        }

        // R2: per-op field whitelist. The MCP layer sends exactly these names; anything
        // else is a caller mistake and must not reach a native call.
        private static string[] AllowedFieldsFor(string op)
        {
            if (op == "speed") return new string[] { "knots" };
            // PR3: telegraph now runs through UnitControl, which supports validate_only. The old
            // single-field entry would have rejected the dry run at the shared gate.
            if (op == "telegraph") return new string[] { "telegraph", "validate_only" };
            if (op == "course") return new string[] { "degrees", "nm", "height_state" };
            if (op == "move") return new string[] { "lat", "lon", "height_state" };
            if (op == "vertical") return new string[] { "mode", "meters", "preset", "update_waypoints", "index", "allow_beyond_measured" };
            if (op == "attack") return new string[] { "target", "ammo", "shots", "force", "allow_neutral", "auto_ammo", "validate_only" };
            // Position fire. No target id: the round goes at a place, which is what a player does
            // by clicking the plot when there is no firing solution yet.
            if (op == "fire") return new string[] { "lat", "lon", "ammo", "shots", "priority", "validate_only" };
            // Sonobuoy drop. Same Vector3 lane as fire, but the game's own AI drops buoys with
            // priority 1000 and autoAttack false, one round per task - see Sonobuoy.cs.
            if (op == "sonobuoy") return new string[] { "lat", "lon", "ammo", "count", "strategy", "depth", "validate_only" };
            if (op == "weapon_status") return new string[] { "status", "formation", "validate_only" };
            // PR3: the six ops added for the capability backlog. Their field tables also live in
            // Weapons.cs / Control.cs, which reject unknown fields themselves - this entry is
            // defence in depth, and it is what makes the rejection a 400 BEFORE any game call
            // instead of after FindUnit (the weapon_status lesson). 'unit' is absent on purpose:
            // the path id is the only authorised address.
            if (op == "attack_waypoint") return new string[] { "ammo", "lat", "lon", "waypoint_lat", "waypoint_lon", "target_unit", "salvo", "salvo_type", "area_radius", "formation_attack", "attack_only_detected", "validate_only" };
            if (op == "countermeasures") return new string[] { "kind", "ammo", "message", "validate_only" };
            if (op == "radars") return new string[] { "role", "state", "formation", "sync_flag", "validate_only" };
            if (op == "rudder") return new string[] { "angle", "validate_only" };
            if (op == "heading") return new string[] { "heading", "max_rudder", "smoothness", "validate_only" };
            if (op == "identify") return new string[] { "target" };
            if (op == "emcon" || op == "radar" || op == "sonar") return new string[] { "on" };
            if (op == "launch") return new string[] { "aircraft", "loadout", "squadron", "require_exact_loadout", "launch_count", "mission" };
            if (op == "recover") return new string[] { "aircraft", "all", "fallback_all" };
            if (op == "abort") return new string[] { "task", "all", "delayed", "unsafe_immediate" };
            if (op == "rearm") return new string[] { "reload_broken" };
            if (op == "ceasefire") return new string[] { "report", "clear_order", "formation_check", "stop_jamming", "air_formation_check", "clear_attack_tasks" };
            if (op == "altitude") return new string[] { "feet", "meters" };
            if (op == "rtb") return new string[0];
            if (op == "waypointClear") return new string[0];
            return null; // unknown op - the dispatch switch reports it
        }

        // The module-side tables used to repeat the list above plus "unit". They are derived now,
        // so a new field is declared once. "unit" stays module-only on purpose: the router injects
        // the path id after the shared gate, and a body-level address is still a 400.
        internal static string[] ModuleAllowedFieldsFor(string op)
        {
            string[] fields = AllowedFieldsFor(op);
            if (fields == null)
                return null;
            string[] withUnit = new string[fields.Length + 1];
            withUnit[0] = "unit";   // first, so "accepted: unit, ..." reads as it always has
            Array.Copy(fields, 0, withUnit, 1, fields.Length);
            return withUnit;
        }

        // S07: the only shape of request that survives a HOLD is a dry run, because
        // validate_only commands nothing and every handler that accepts it returns before it
        // touches the game. weapon_status is not special-cased: a plain call writes
        // _weaponStatus (tight/free/hold) on the hull, which HOLD must stop, while its
        // validate_only form is a dry run like any other and cannot write. An operator who
        // wants to inspect under HOLD uses the GET routes; releasing HOLD allows a doctrine
        // change. (This used to take `op`; no rule needed it, so the parameter is gone.)
        private static bool HoldExempt(JObject req)
        {
            return ReqBool(req, "validate_only", false);
        }

        // R2: a required field that is absent is a 400, not a silent default 0.
        private static void RequireField(JObject req, string op, string key)
        {
            if (req == null || req[key] == null || req[key].Type == JTokenType.Null)
                throw new ApiException(400, "missing_field",
                    "op '" + op + "' requires field '" + key + "'", false);
        }

        /// <summary>
        /// A field that must be present AND an integer. RequireField only checks presence, and
        /// ReqInt takes a default, so neither can express "required integer with no fallback".
        /// </summary>
        private static void RequireIntField(JObject req, string op, string key)
        {
            RequireField(req, op, key);
            JToken t = req[key];
            if (t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be an integer, got " + t.Type.ToString(), false);
        }

        /// <summary>
        /// T19.2: 'height_state' must name a real WaypointHeightState. Anything else is a 400
        /// before the request reaches the game, because the alternative is an enum cast that
        /// silently becomes member 0 (Number) and quietly changes the unit's altitude.
        /// </summary>
        private static void ValidateHeightState(JObject req, string key)
        {
            JToken t;
            if (!req.TryGetValue(key, out t)) return;
            if (t == null || t.Type == JTokenType.Null) return;   // explicit null = keep current
            if (t.Type != JTokenType.String)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be a string naming a WaypointHeightState, got " + t.Type.ToString(), false);
            string name = (string)t;
            if (FindHeightState(name) < 0)
                throw new ApiException(400, "bad_value",
                    "unknown height_state '" + name + "'; expected one of " + JoinNames(HeightStateNames()), false);
        }

        internal static string[] HeightStateNames()
        {
            return Enum.GetNames(typeof(SeaPower.WaypointData.WaypointHeightState));
        }

        /// <summary>Index of the named state (case-insensitive), or -1.</summary>
        private static int FindHeightState(string name)
        {
            string[] names = HeightStateNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>
        /// The state to hand to setWaypointTask. Absent or null keeps the historical behaviour
        /// exactly: NoChange, which is what every earlier build hardcoded.
        /// </summary>
        private static SeaPower.WaypointData.WaypointHeightState HeightStateFrom(JObject req)
        {
            JToken t;
            if (req == null || !req.TryGetValue("height_state", out t)) return SeaPower.WaypointData.WaypointHeightState.NoChange;
            if (t == null || t.Type != JTokenType.String) return SeaPower.WaypointData.WaypointHeightState.NoChange;
            int idx = FindHeightState((string)t);
            if (idx < 0) return SeaPower.WaypointData.WaypointHeightState.NoChange;
            return (SeaPower.WaypointData.WaypointHeightState)Enum.GetValues(typeof(SeaPower.WaypointData.WaypointHeightState)).GetValue(idx);
        }

        internal static string JoinNames(string[] names)
        {
            if (names == null || names.Length == 0) return "(none)";
            string s = "";
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) s += ", ";
                s += names[i];
            }
            return s;
        }

        /// <summary>
        /// Copies a field from a per-entry object when present, otherwise from the batch-level
        /// object. An explicit JSON null counts as absent - the child then uses its own default
        /// rather than carrying a null through to a type check.
        /// </summary>
        private static void CopyOpt(JObject from, JObject fallback, JObject to, string key)
        {
            JToken v = from != null ? from[key] : null;
            if (v == null || v.Type == JTokenType.Null)
                v = fallback != null ? fallback[key] : null;
            if (v == null || v.Type == JTokenType.Null) return;
            to[key] = v;
        }

        /// <summary>
        /// R2: every request-level check that does not need the game object runs here, before
        /// FindUnit touches the game. A malformed or out-of-range parameter must be rejected
        /// while the process is still in a pure-request state, otherwise the same bug would
        /// surface as a partially applied command.
        /// </summary>
        /// <summary>
        /// Test seam (round 3 review explicitly allows a small execution counter): number of
        /// assign children that reached the execution loop. A malformed batch must leave this
        /// at zero, which proves the rejection happened before any native call.
        /// </summary>
        public static int AssignExecuteCount;

        private static void ValidateUnitCommandFields(string op, JObject req)
        {
            if (op == "speed")
            {
                RequireField(req, "speed", "knots");
                // A ship cannot make 500 knots and cannot make negative way in reverse.
                ReqFloatIn(req, "knots", 0f, -10f, 80f);
            }
            else if (op == "course")
            {
                RequireField(req, "course", "degrees");
                ReqFloat(req, "degrees", 0f);              // any heading is valid, it wraps
                ReqFloatIn(req, "nm", 60f, 0.01f, 10000f);
                ValidateHeightState(req, "height_state");
            }
            else if (op == "telegraph")
            {
                RequireField(req, "telegraph", "telegraph");
                // -1..5 are the real notches; 6 used to stop the ship dead via an index wrap.
                ReqIntIn(req, "telegraph", 0, -1, 5);
            }
            else if (op == "move")
            {
                RequireField(req, "move", "lat");
                RequireField(req, "move", "lon");
                ReqFloatIn(req, "lat", 0f, -90f, 90f);
                ReqFloatIn(req, "lon", 0f, -180f, 180f);
                ValidateHeightState(req, "height_state");
            }
            else if (op == "vertical")
            {
                // T19.2: one op, one mode field. The mode decides which of the numeric fields
                // is required, so all of that is still pure-data validation.
                RequireField(req, "vertical", "mode");
                string mode = ReqStr(req, "mode", "");
                if (mode == "depth")
                {
                    RequireField(req, "vertical", "meters");
                    ReqFloatIn(req, "meters", 0f, 0f, 2000f);   // positive = deeper, per setDepth IL
                }
                else if (mode == "preset_depth" || mode == "preset_height")
                {
                    RequireField(req, "vertical", "preset");
                    ReqIntIn(req, "preset", 0, 0, 50);
                    ReqBool(req, "update_waypoints", false);
                }
                else if (mode == "push_waypoints")
                {
                    RequireField(req, "vertical", "index");
                    ReqIntIn(req, "index", 0, 0, 7);
                }
                else if (mode != "periscope")
                {
                    throw new ApiException(400, "bad_value",
                        "unknown vertical mode '" + mode + "'; expected depth, periscope, preset_depth, preset_height or push_waypoints", false);
                }
            }
            else if (op == "attack")
            {
                RequireField(req, "attack", "target");
                // Rework 3: the target id was only checked for PRESENCE here, so a string
                // target reached the cast in the execution branch. Type-check it while the
                // request is still pure data.
                RequireIntField(req, "attack", "target");
                // Type-check the switches here as well: they gate destructive behaviour, so a
                // typo like validate_only:"typo" must never silently become false or true.
                ReqBool(req, "force", false);
                ReqBool(req, "allow_neutral", false);
                ReqBool(req, "auto_ammo", false);
                ReqBool(req, "validate_only", false);
                ReqIntIn(req, "shots", 1, -1, 1000);
                ReqStr(req, "ammo", null);
            }
            else if (op == "fire")
            {
                // A place, not a unit. ammo is REQUIRED here: GetDefaultAmmunitionForEngage needs
                // a target object to choose from, and guessing a weapon for an area shot is how a
                // 127 mm gun ends up in an anti-submarine mission.
                RequireField(req, "fire", "lat");
                RequireField(req, "fire", "lon");
                RequireField(req, "fire", "ammo");
                ReqFloat(req, "lat", float.NaN);
                ReqFloat(req, "lon", float.NaN);
                ReqStr(req, "ammo", null);
                ReqBool(req, "validate_only", false);
                ReqIntIn(req, "shots", 1, -1, 1000);
                ReqIntIn(req, "priority", 1, 1, 100);
            }
            else if (op == "sonobuoy")
            {
                // 'unit' is deliberately absent: the path id is the only authority, and letting the
                // body carry a second id is how a request ends up dropping a buoy from another hull.
                RequireField(req, "sonobuoy", "lat");
                RequireField(req, "sonobuoy", "lon");
                ReqFloat(req, "lat", float.NaN);
                ReqFloat(req, "lon", float.NaN);
                ReqStr(req, "ammo", null);
                ReqStr(req, "strategy", null);
                ReqStr(req, "depth", null);
                ReqIntIn(req, "count", 1, 1, 32);
                ReqBool(req, "validate_only", false);
            }
            else if (op == "weapon_status")
            {
                // Tight / Free / Hold is a weapons-control doctrine, not a target order: it takes
                // no target, fires nothing and has no timer. The game writes _weaponStatus (IL:
                // stfld ObjectBase::_weaponStatus) and, when formationCheck is set AND this hull
                // follows a formation AND (SelectedControlMode==2 or ==1 with IsFormationLeader),
                // copies it onto every Station.UnitObject instead of its own field.
                RequireField(req, "weapon_status", "status");
                string wsWant = ReqStr(req, "status", null);
                ReqBool(req, "formation", false);
                ReqBool(req, "validate_only", false);
                // The VALUE check belongs here, with every other value check, not in the handler:
                // this runs before FindUnit, so a typo is refused without touching the world - and
                // therefore it is decidable on a host with no Unity at all (ACT-013 asserts it).
                string wsNorm = wsWant == null ? "" : wsWant.Trim().ToLowerInvariant();
                if (wsNorm != "tight" && wsNorm != "free" && wsNorm != "hold")
                    throw new ApiException(400, "bad_value",
                        "status must be tight, free or hold (got " + wsWant + ")", false);
            }
            else if (op == "identify")
            {
                RequireField(req, "identify", "target");
                RequireIntField(req, "identify", "target");
            }
            else if (op == "emcon" || op == "radar" || op == "sonar")
            {
                RequireField(req, op, "on");
                ReqBool(req, "on", false);
            }
            else if (op == "ceasefire")
            {
                // Presence is optional (defaults are the historical all-true call), but a
                // wrong TYPE must not fall back to the default - that is how a caller ends
                // up believing it suppressed something it did not.
                ReqBool(req, "report", true);
                ReqBool(req, "clear_order", true);
                ReqBool(req, "formation_check", true);
                ReqBool(req, "stop_jamming", true);
                ReqBool(req, "air_formation_check", true);
                ReqBool(req, "clear_attack_tasks", true);
            }
            else if (op == "rearm")
            {
                ReqBool(req, "reload_broken", false);
            }
            else if (op == "launch")
            {
                // T19.5: 'aircraft' used to be read and then silently discarded. It now selects
                // a specific type through the 5-argument FlightDeckLaunchUnit overload. Leaving
                // it out keeps the legacy type+role path, so existing callers still work.
                ReqStr(req, "aircraft", null);
                ReqStr(req, "loadout", null);
                ReqStr(req, "squadron", null);
                ReqBool(req, "require_exact_loadout", false);
                ReqIntIn(req, "launch_count", 0, 0, 64);
                string missionName = ReqStr(req, "mission", null);
                if (missionName != null && FindMissionType(missionName) < 0)
                    throw new ApiException(400, "bad_value",
                        "unknown mission '" + missionName + "'; expected one of " + JoinNames(MissionTypeNames()), false);
            }
            else if (op == "recover")
            {
                // Same rule as abort: exactly one target. Neither and both are 400s, so a
                // half-specified recovery can never silently pick a scope.
                bool recAll = ReqBool(req, "all", false);
                bool haveAircraft = req != null && req["aircraft"] != null && req["aircraft"].Type != JTokenType.Null;
                if (haveAircraft && recAll)
                    throw new ApiException(400, "bad_value",
                        "recover takes 'aircraft' or 'all', not both", false);
                if (!haveAircraft && !recAll)
                    throw new ApiException(400, "missing_field",
                        "recover requires either 'aircraft' (a unit id from GET /units/{id}/airborne) or 'all': true", false);
                if (haveAircraft) RequireIntField(req, "recover", "aircraft");
                // fallback_all only makes sense for a single aircraft: with all:true the
                // whole airgroup is already being recalled.
                if (recAll && req["fallback_all"] != null && req["fallback_all"].Type != JTokenType.Null)
                    throw new ApiException(400, "bad_value",
                        "fallback_all is only meaningful together with aircraft, not with all", false);
                if (req["fallback_all"] != null && req["fallback_all"].Type != JTokenType.Null)
                    ReqBool(req, "fallback_all", false);
            }
            else if (op == "abort")
            {
                // Exactly one target, on purpose: "abort nothing" is a 400 rather than a silent
                // no-op, and "abort this one AND everything" is ambiguous, so it is refused too.
                string taskUid = ReqStr(req, "task", null);
                bool abortAll = ReqBool(req, "all", false);
                if (taskUid == null && !abortAll)
                    throw new ApiException(400, "missing_field",
                        "abort requires either 'task' (a uid from GET /units/{id}/decktasks) or 'all': true", false);
                if (taskUid != null && abortAll)
                    throw new ApiException(400, "bad_value",
                        "abort takes 'task' or 'all', not both", false);
                if (taskUid != null && !IsGuid(taskUid))
                    throw new ApiException(400, "bad_value",
                        "'task' must be a task uid (GUID) as reported by GET /units/{id}/decktasks, got '" + taskUid + "'", false);
                // B27. The consent check lives here, with the other pure-data checks, so a caller
                // that asks for the unsafe form is refused before any game object is touched and
                // the rule is testable offline. See the abort branch for what it protects.
                if (!ReqBool(req, "delayed", true) && !ReqBool(req, "unsafe_immediate", false))
                    throw new ApiException(400, "unsafe_abort",
                        "delayed:false cancels a launch task on the spot. A live run proved that doing this "
                        + "while the aircraft was already taxiing leaves the deck ticking the cancelled task "
                        + "on a destroyed transform: 35,032 NullReferenceExceptions in "
                        + "GroundTaxiPhysics.get_PositionError, once per FixedUpdate, until the process ended. "
                        + "Leave delayed at its default true, or send unsafe_immediate:true when you really "
                        + "mean the immediate form.", false);
            }
        }

        private static JToken UnitCommand(int id, string op, JObject req)
        {
            // R2: unknown fields and missing required arguments are rejected before any
            // game object is touched, so a malformed request can never issue a command.
            string[] allowedFields = AllowedFieldsFor(op);
            if (allowedFields != null) RejectUnknownFields(req, op, allowedFields);
            ValidateUnitCommandFields(op, req);

            // S07: the operator's HOLD is a stop on the write path, not a lamp the
            // service happens to read. A typo is still a typo, so this sits after the
            // malformed-request checks and before any unit lookup or game call: no
            // caller (MCP tool, service, direct HTTP) can order a hull while it is on.
            // A dry run commands nothing, so it stays usable: an operator holding the fleet
            // still needs to ask "what would this do". weapon_status is NOT part of that
            // exemption - despite the name it writes the fire discipline (see HoldExempt).
            if (CommsBridge.Hold && !HoldExempt(req))
                throw new ApiException(409, "held",
                    "operator HOLD is on: no unit command is accepted until it is released",
                    true);

            // Sonobuoy runs its own geometry (one task per buoy, priority 1000) and must not go
            // through the shared unit lookups below, so it is handed off here, after validation.
            // OI-4: these used to be handed off here, BEFORE FindUnit, which meant they skipped the
            // existence gate - a hull the player never detected could be commanded. They now run
            // after the same gates as every other write: exists, visible, yours, right type.

            ObjectBase u = FindUnit(id);

            // TR2-I / ISS-003: an order to a hull the player has not detected is not "unusual",
            // it is impossible from the player's seat - there is nothing to select in the UI.
            if (!IntelScope.PlayerKnows(u))
                // I8: an order to an undetected object answers exactly like an order to a
                // nonexistent one. Different codes here would let a caller map the world by
                // trying to command it.
                throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);

            // ISS-002 / TR2-M: an op that cannot apply to this kind of unit is refused before any
            // game call, so a wrong unit type costs a 409 instead of a silent no-op. The gate only
            // answers "does this op make sense for this hull", never "are the arguments valid".
            string typeRefusal = UnitTypeGate.Refuse(op, u);
            if (typeRefusal != null)
                throw new ApiException(409, "wrong_unit_type", typeRefusal, false);

            // OI-4: a write is an order from the player's seat, and the player cannot order the
            // other side. The engine would accept it - that is a capability of the game, not a
            // permission of this bridge. Reads stay open to detected contacts (redacted); writes
            // stop at the player's own hulls. This also closes the receipt leak: ammoBefore and
            // ammoAfter describe the addressed hull's magazine.
            if (!SafeFlag(u, "mine"))
                throw new ApiException(409, "not_your_unit",
                    "this bridge only writes to the player's own units: /units/" + id + "/" + op
                    + " addressed a hull on the other side. Reading a detected contact is fine;"
                    + " ordering it is not, and returning its magazine in the receipt would leak it",
                    false);

            // Sonobuoys run their own geometry (one task per buoy, priority 1000), so the hand-off
            // happens here - after the gates above, never before them.
            if (op == "sonobuoy")
            {
                JObject sreq = req == null ? new JObject() : (JObject)req.DeepClone();
                sreq["unit"] = id;
                return Sonobuoy.Drop(sreq);
            }
            if (op == "weapon_status")
                return WeaponStatusCommand(id, req);
            if (op == "attack_waypoint" || op == "countermeasures" || op == "radars"
                || op == "rudder" || op == "telegraph" || op == "heading")
            {
                JObject wreq = req == null ? new JObject() : (JObject)req.DeepClone();
                wreq["unit"] = id;
                if (op == "attack_waypoint") return SeaPowerAgentBridge.Weapons.AttackWaypoint(wreq);
                if (op == "countermeasures") return SeaPowerAgentBridge.Weapons.Countermeasures(wreq);
                if (op == "radars") return SeaPowerAgentBridge.Weapons.Radars(wreq);
                if (op == "rudder") return UnitControl.Rudder(wreq);
                if (op == "telegraph") return UnitControl.Telegraph(wreq);
                return UnitControl.Heading(wreq);
            }

            // LAND-2: a coastal battery or an oil rig is a SeaPower.LandUnit that inherits the
            // movement ops. ApplyMovement force-zeroes their target velocity, so "move" on a fixed
            // site is a silent no-op rather than an error - refuse it here and say why. A non-land
            // unit gets null from this gate (it asserts nothing about hulls).
            if (op == "move" || op == "course" || op == "speed" || op == "waypoint" || op == "waypointClear")
            {
                string landReason = LandUnits.RefuseMoveReason(u);
                if (landReason != null)
                    throw new ApiException(409, "wrong_unit_type",
                        "this land unit cannot take a movement order: " + landReason
                        + " (a fixed installation or an immobile site has nothing to move)", false);
            }

            JObject o = new JObject();
            o["id"] = u.UniqueID;

            if (op == "speed")
            {
                RequireField(req, "speed", "knots");
                float knots = ReqFloat(req, "knots", 0f);
                u.SetSpeedCommand(new ConstantSpeed(knots, u));
                o["action"] = "speed";
                o["knots"] = knots;
            }
            else if (op == "course")
            {
                RequireField(req, "course", "degrees");
                float deg = ReqFloat(req, "degrees", 0f);
                float nm = ReqFloat(req, "nm", 60f);
                GeoPosition here = GetGeo(u);
                GeoPosition dest = here.GeoOffsetByDistanceOnHeading(nm, deg);
                // setOrder() only mutates CurrentOrder (display) and never reaches
                // UserRootNode.AddTask. setWaypointTask() is the real entry point.
                // Do NOT follow it with setWaypointTaskParameters(): a default
                // WaypointData carries _setTelegraph=0, which is interpreted as
                // AllStop and silently invalidates the freshly queued task
                // (verified in game: waypointCount stays 0).
                u.setWaypointTask(dest, "agent", HeightStateFrom(req));
                o["action"] = "course";
                o["degrees"] = deg;
                o["nm"] = nm;
                o["dest"] = dest.Latitude + "," + dest.Longitude;
                o["heightState"] = HeightStateFrom(req).ToString();
            }
            else if (op == "move")
            {
                RequireField(req, "move", "lat");
                RequireField(req, "move", "lon");
                float lat = ReqFloat(req, "lat", float.NaN);
                float lon = ReqFloat(req, "lon", float.NaN);
                if (float.IsNaN(lat) || float.IsNaN(lon)) throw new ApiException(400, "bad_number", "move requires finite lat and lon", false);
                GeoPosition dest = new GeoPosition((double)lat, (double)lon, 0.0);
                u.setWaypointTask(dest, "agent", HeightStateFrom(req));
                o["action"] = "move";
                o["lat"] = lat;
                o["lon"] = lon;
                o["heightState"] = HeightStateFrom(req).ToString();
            }
            else if (op == "attack")
            {
                RequireField(req, "attack", "target");
                int targetId = ReqInt(req, "target", -1);
                // REVIEW (round 2, blocker 6): the target was resolved with FindUnit and nothing
                // else, so naming a hull the player never detected produced a receipt carrying its
                // side, its relationship and the true distance between the shooter and it - two of
                // your own hulls cross-fixed any undetected target by range alone. An undetected
                // target now answers exactly like an absent one (I8), before any distance is
                // computed. The old `if (target == null)` was dead code (FindUnit throws).
                ObjectBase target = FindKnownUnit(targetId);

                // The game silently ignores an engage order when the target is friendly,
                // not detected, or out of range, so check those preconditions up front and
                // report a reason instead of handing back a hollow ok:true.
                bool force = ReqBool(req, "force", false);
                List<string> blocked = new List<string>();

                double rangeNm = -1.0;
                try { rangeNm = GetGeo(u).getDistanceInMiles(GetGeo(target), true); }
                catch (Exception) { }

                string sideU = SideName(Member(u, "_taskforce"));
                string sideT = SideName(Member(target, "_taskforce"));
                // TfType only has Player/Ally/Enemy/Neutral, and comparing those strings made
                // Player-vs-Ally look hostile, so the bridge would happily engage its own
                // allies. Taskforce.RelationshipTo is the game's own answer.
                string relation = RelationshipOf(u, target);

                // R4: normalize exactly once. Anything that is not one of the four
                // RelationsState values -- including null because the lookup threw or one of
                // the units has no taskforce -- becomes Unknown, which refuses by default.
                // Previously a null relation fell through to the side comparison and then
                // straight into the fire path.
                string relNorm = NormalizeRelation(relation);

                o["mySide"] = sideU;
                o["targetSide"] = sideT;
                o["relationship"] = relation;
                o["relationshipNormalized"] = relNorm;

                bool friendly = relNorm == "Friendly";
                bool allowNeutral = ReqBool(req, "allow_neutral", false);
                // Friendly is a hard stop: force must never be able to override it.
                bool hardBlocked = false;

                // R4 decision table lives in RelationVerdict so it can be unit tested offline
                // against every relation x switch combination.
                //   Friendly/Self -> always refused (force included)
                //   Hostile       -> allowed
                //   Neutral       -> refused unless allow_neutral=true
                //   Unknown       -> refused unless force=true
                bool verdictHardStop;
                string verdict = RelationVerdict(relation, allowNeutral, force, out verdictHardStop);
                if (verdict != null)
                {
                    string label = sideT != null ? sideT : relNorm;
                    blocked.Add(verdict.Replace("{side}", label));
                    if (verdictHardStop) hardBlocked = true;
                }

                // isObjectDetectedByTaskforce is the game's own answer, so prefer it and keep
                // the plotting-table lookup as a fallback and as the source of the track id.
                bool detected = false;
                try { detected = Convert.ToBoolean(u.isObjectDetectedByTaskforce(target)); }
                catch (Exception) { }

                int trackId = -1;
                try
                {
                    PlottingTable pt = u.PlottingTable;
                    if (pt != null)
                    {
                        Vehicle tv = pt.VehicleForObject(target);
                        if (tv != null) trackId = tv.Id;
                    }
                }
                catch (Exception) { }

                if (trackId > 0) detected = true;
                o["detected"] = detected;
                if (trackId > 0) o["targetTrackId"] = trackId;

                if (!friendly && !detected)
                    blocked.Add("target is not detected by any of our sensors");

                // Launch-range envelope. AmmunitionParameters hides these as private fields
                // and the game silently drops a shot that is too close, so compare them here.
                // The chosen id is resolved ONCE and reused for the actual launch below;
                // re-reading req["ammo"] there used to drop the auto-picked weapon entirely,
                // which is why a hull with no explicit ammo never fired anything.
                string ammoId = ReqStr(req, "ammo", null);
                // PickAmmo prefers the longest reach, which is not the same as the right
                // weapon: it once handed a 127 mm gun to an anti-submarine engagement. Keep
                // it opt-in and fall back to the game's own default otherwise.
                // The game's own picker knows what the target IS; PickAmmo only sorts by reach.
                // Order matters: auto_ammo used to run first and fired an anti-air missile at a
                // submarine in the 2026-11-05 live test. The game default now wins, PickAmmo is
                // the last resort only.
                if (ammoId == null)
                {
                    try { ammoId = u.GetDefaultAmmunitionForEngage(target, false); }
                    catch (Exception) { }
                }
                if (ammoId == null && ReqBool(req, "auto_ammo", false))
                    ammoId = PickAmmo(u, rangeNm);
                if (ammoId == null && blocked.Count == 0)
                    blocked.Add("no usable ammunition for this target: pass ammo explicitly, or auto_ammo=true to let the bridge guess");

                double minMi = 0.0;
                double maxMi = 0.0;
                try
                {
                    Ammunition am = ammoId == null ? null : u.getAmmunitionByName(ammoId);
                    if (am != null)
                    {
                        object ap = Member(am, "_ap");
                        if (ap != null)
                        {
                            object mn = Member(ap, "_minLaunchRangeInMiles");
                            object mx = Member(ap, "_maxLaunchRangeInMiles");
                            if (mn != null) minMi = Convert.ToDouble(mn);
                            if (mx != null) maxMi = Convert.ToDouble(mx);
                        }
                    }
                }
                catch (Exception) { }

                o["ammoCandidate"] = ammoId;

                o["minRangeMi"] = minMi;
                o["maxRangeMi"] = maxMi;
                // The envelope belongs to the AMMUNITION, not to the bridge. A free-fall bomb
                // has to be released almost over the target; a 100 mm gun cannot shoot past
                // 2.2 nmi; a standoff missile carries a much larger maxRangeMi and can be
                // ordered from far away. Callers who read the refusal as a bridge limit keep
                // asking for the wrong thing, so say whose number this is, and let
                // GET /units/{id}/weapons be the place that answers "which weapon reaches".
                string ammoLabel = ammoId == null ? "(default)" : ammoId;
                if (minMi > 0 || maxMi > 0)
                {
                    o["ammoEnvelope"] = new JObject
                    {
                        { "ammo", (JToken)new JValue(ammoLabel) },
                        { "minRangeMi", minMi },
                        { "maxRangeMi", maxMi },
                        { "owner", "the ammunition, not the bridge" },
                        { "advice", "this is the weapon's own launch envelope. Guns and free-fall bombs only leave the rail inside it, "
                            + "so an aircraft carrying Mk-82 has to be within maxRangeMi before /attack. Guided standoff weapons "
                            + "(anti-ship and cruise missiles) carry a much larger maxRangeMi and can be ordered from far away. "
                            + "GET /units/{id}/weapons lists every ammunition with its own min and max." }
                    };
                }
                if (minMi > 0 && rangeNm >= 0 && rangeNm < minMi)
                    blocked.Add("target is inside the launch envelope of '" + ammoLabel + "' (minRangeMi " + minMi.ToString("0.0")
                        + " nmi) - the ammunition, not the bridge, sets this floor: back the shooter off, or pick a weapon whose minimum is shorter");
                if (maxMi > 0 && rangeNm >= 0 && rangeNm > maxMi)
                    blocked.Add("target is beyond the launch envelope of '" + ammoLabel + "' (maxRangeMi " + maxMi.ToString("0.0")
                        + " nmi) - unguided ordnance (guns, free-fall bombs, rockets) must be released inside it, so close the range first; "
                        + "guided standoff weapons have a much larger maxRangeMi and can be launched from here");

                o["action"] = "attack";
                o["target"] = targetId;
                // D4: a distance is a position witness. Twelve observers of one contact gave
                // least-squares residuals up to 6.13 nmi (the value tracks the ammunition's
                // chosen aim point rather than the hull), so it cannot be inverted - but it is
                // still a range to a contact the player has not identified, and it was published
                // anyway. The value is still used internally for the launch envelope; only the
                // reply is gated, and the bound a caller could already infer from its own
                // ammunition's min/max range is not new information.
                string targetVis = SafeStr(VisibilityOf(target));
                bool rangePublishable = IntelPolicy.AllowsDetail(targetVis);
                o["rangeNm"] = rangePublishable ? (JToken)new JValue(rangeNm) : JValue.CreateNull();
                if (!rangePublishable)
                {
                    o["rangeWithheld"] = "distance to a contact the player has not identified";
                    o["intelPolicy"] = targetVis;
                }
                o["targetSide"] = sideT;
                if (trackId > 0) o["targetTrackId"] = trackId;

                // validate_only runs every precondition but hands nothing to the game.
                // force only skips the refusals; it is NOT a dry-run flag.
                bool validateOnly = ReqBool(req, "validate_only", false);
                o["validated"] = blocked.Count == 0;
                o["executed"] = false;

                if (hardBlocked || (blocked.Count > 0 && !force))
                {
                    // Not a success: report it as a refusal so callers cannot mistake an
                    // un-fired shot for a queued fire mission.
                    o["refused"] = true;
                    o["ok"] = false;
                    o["reasons"] = JArray.FromObject(blocked);
                    // R4: force is scoped to the Unknown relation and to the soft gates. It
                    // must never be able to talk the bridge into shooting its own side.
                    if (hardBlocked) o["forceIgnored"] = "friendly targets stay refused even with force=true";
                }
                else if (validateOnly)
                {
                    o["ok"] = true;
                    o["wouldFire"] = ammoId;
                }
                else
                {
                    // shots = -1 behaves as a single shot for missile-type weapons, so make
                    // the default explicit instead of passing a magic negative through.
                    int shots = ReqInt(req, "shots", 1);
                    // Order.Type.Attack only sets display text; InsertEngageTask is the
                    // real task-system entry point and returns the created EngageTask.
                    int ammoBefore = AmmoCount(u, ammoId);
                    // D1: InsertEngageTask returns the queued EngageTask and null when the game
                    // declines. The return value used to be discarded, so "executed" meant only
                    // "the method was called" and a silently dropped order looked exactly like a
                    // queued one (B14: a torpedo order at a sunk target; round 5: four attack
                    // calls that never fired and could not say why).
                    object engageTask = u.InsertEngageTask(ammoId, target, target.transform.position,
                        shots, 1, true, false, false);
                    int ammoAfter = AmmoCount(u, ammoId);
                    o["executed"] = engageTask != null;
                    o["taskQueued"] = engageTask != null;
                    if (engageTask == null)
                    {
                        o["ok"] = false;
                        o["refused"] = true;
                        o["refuseCode"] = "task_refused";
                        o["reasons"] = JArray.FromObject(new string[] {
                            "ObjectBase.InsertEngageTask returned null instead of a task, so nothing was"
                            + " queued. The game returns null when the hull has no task graph (_userRoot),"
                            + " when the target object is gone, or when this hull cannot take engage tasks."
                            + " Request was ammo='" + ammoId + "', shots=" + shots + ", target=" + targetId + "."
                        });
                    }
                    else
                    {
                        o["task"] = EngageProof(u, engageTask, ammoId, shots);
                    }
                    // 'executed' historically meant "we called InsertEngageTask". B14 showed
                    // that is not the same as "the game fired": a SET-65 torpedo ordered at a
                    // surface hull came back executed:true with the magazine untouched at 10.
                    // submitted = handed to the game. confirmed = a round actually left.
                    o["submitted"] = engageTask != null;
                    o["ammo"] = ammoId;
                    o["shots"] = shots;
                    o["ammoBefore"] = ammoBefore;
                    o["ammoAfter"] = ammoAfter;
                    // The magazine does NOT move on the same frame. Measured in game: a 37 mm
                    // engagement read 12000 -> 12000 right after the call, yet by t+3s it was
                    // 11860. So an unchanged count here proves nothing; it only means "not
                    // observed yet". Never report it as a failure.
                    bool fired = ammoBefore >= 0 && ammoAfter >= 0 && ammoAfter < ammoBefore;
                    o["confirmed"] = fired;
                    o["confirmation"] = engageTask == null
                        ? "not_submitted"
                        : (ammoBefore < 0 || ammoAfter < 0
                            ? "unavailable"
                            : (fired ? "fired" : "unverified"));
                    if (!fired)
                    {
                        o["confirmNote"] = engageTask == null
                            ? "no task was queued (InsertEngageTask returned null), so no round can leave from this call"
                            : ammoBefore < 0 || ammoAfter < 0
                            ? "ammunition count unavailable for this weapon; cannot confirm the shot"
                            : "the magazine still read " + ammoBefore + " immediately after submitting. This is NORMAL - rounds are deducted a few seconds later (a real 37 mm run went 12000 -> 11860 within 3 s). Re-read /units/" + u.UniqueID + "/ammo and " + u.UniqueID + " orderText after a few seconds. Only if BOTH stay unchanged is the order being dropped (that happens when the target is already sunk).";
                    }
                    if (force && blocked.Count > 0) o["forced"] = true;
                    // Read back what the game actually queued. The internal order text carries
                    // the REAL track number, which drifts from the one we saw. It lives on the
                    // CurrentOrderText reactive property - there is no field called orderText,
                    // which is why this used to come back empty.
                    try
                    {
                        o["orderText"] = SafeStr(Rx(u, "CurrentOrderText"));
                        o["orderFeedback"] = SafeStr(Rx(u, "OrderFeedbackText"));
                    }
                    catch (Exception) { }
                }
            }
            else if (op == "fire")
            {
                // Area fire at a place, the thing a player does by clicking the plot when there is
                // no track yet. InsertEngageTask has two constructors behind one branch:
                //     if (targetObject != null)  new EngageTask(ammo, targetObject, this, shots, ...)
                //     else                       new EngageTask(ammo, targetPosition, this, shots, ...)
                // so a null target plus a Vector3 IS the plot-click path. Deliberately no friendly
                // and no detection gate: those two check a target, and there is no target. The
                // range envelope still applies, because the game drops an out-of-envelope shot in
                // silence and a hollow executed:true is worse than a refusal.
                float flat = ReqFloat(req, "lat", float.NaN);
                float flon = ReqFloat(req, "lon", float.NaN);
                if (float.IsNaN(flat) || float.IsNaN(flon) || float.IsInfinity(flat) || float.IsInfinity(flon))
                    throw new ApiException(400, "bad_number", "fire requires finite lat and lon", false);
                if (flat < -90f || flat > 90f || flon < -180f || flon > 180f)
                    throw new ApiException(400, "bad_value",
                        "lat must be -90..90 and lon -180..180 (got " + flat + "," + flon + ")", false);

                string fammo = ReqStr(req, "ammo", null);
                if (fammo == null)
                    throw new ApiException(400, "missing_field",
                        "fire requires 'ammo': a position has no default weapon to fall back on", false);
                Ammunition fam = null;
                try { fam = u.getAmmunitionByName(fammo); } catch (Exception) { }
                if (fam == null)
                    throw new ApiException(404, "ammo_not_found", "this unit has no ammunition called '" + fammo + "'", false);

                // Ask the GAME whether this ammunition needs an object target. A null-target
                // EngageTask is only a real firing path for weapons the game will launch at a
                // point; DoesEngageRequiresTarget(AmmunitionParameters) reads _type==1,
                // _targetRequired, _guidanceType 2/5/0/6, _midCourseCorrection, _hasSubmunition
                // and _targetType/_secondaryTargetType, and returns true for everything that
                // needs something to shoot at. Without this check /fire on a SAM reported
                // executed:true while the game quietly discarded the task - the same silent drop
                // that once made /attack look successful against an already sunk hull.
                object fapObj = null;
                try { fapObj = Member(fam, "_ap"); } catch (Exception) { }
                SeaPower.AmmunitionParameters fapTyped = fapObj as SeaPower.AmmunitionParameters;
                bool fammoNeedsTarget = false;
                string fammoCheck = "unavailable";
                if (fapTyped != null)
                {
                    try
                    {
                        fammoNeedsTarget = u.DoesEngageRequiresTarget(fapTyped);
                        fammoCheck = "checked";
                    }
                    catch (Exception) { fammoCheck = "threw"; }
                }

                GeoPosition fgeo = new GeoPosition((double)flat, (double)flon, 0.0);
                double frange = -1.0;
                try { frange = GetGeo(u).getDistanceInMiles(fgeo, true); }
                catch (Exception) { }

                double fmin = 0.0;
                double fmax = 0.0;
                try
                {
                    if (fapObj != null)
                    {
                        object fmn = Member(fapObj, "_minLaunchRangeInMiles");
                        object fmx = Member(fapObj, "_maxLaunchRangeInMiles");
                        if (fmn != null) fmin = Convert.ToDouble(fmn);
                        if (fmx != null) fmax = Convert.ToDouble(fmx);
                    }
                }
                catch (Exception) { }

                Vector3 fworld;
                string fsource;
                if (!MapUnits.GeoToWorld((double)flat, (double)flon, 0.0, out fworld, out fsource))
                    throw new ApiException(409, "no_world_frame",
                        "cannot convert a geographic point to world space yet: the game has not published a centre tile", false);

                List<string> fblocked = new List<string>();
                string fcode = null;
                if (fam != null && fammoNeedsTarget)
                {
                    // A weapon that needs an object target cannot be area-fired, whatever the
                    // range envelope says. Saying so here is the difference between "refused,
                    // use /attack" and "executed:true, nothing happened".
                    fcode = "ammo_requires_target";
                    // Not a /fire limitation: a gun cannot be pointed at a coordinate because the
                    // game needs a firing solution against a target object. Missiles and bombs
                    // that fly to a point pass this check. Naming the physics stops a caller from
                    // retrying /fire on a 100 mm gun and concluding the op is broken.
                    fblocked.Add("'" + fammo + "' needs a firing solution against a target object "
                        + "(DoesEngageRequiresTarget == true) - that is what guns and unguided artillery are: they are aimed at "
                        + "something, so they cannot be shot at bare coordinates. This is a property of the ammunition, not a /fire "
                        + "limitation: cruise missiles, anti-ship missiles and bombs fly to a point and /fire works for those. "
                        + "For this weapon use POST /units/" + u.UniqueID + "/attack with a target object id, or "
                        + "POST /units/" + u.UniqueID + "/attack_waypoint for area suppression");
                }
                if (fmin > 0 && frange >= 0 && frange < fmin)
                    fblocked.Add("the point is inside the minimum launch range (" + fmin.ToString("0.0") + " nmi)");
                if (fmax > 0 && frange >= 0 && frange > fmax)
                    fblocked.Add("the point is beyond the maximum launch range (" + fmax.ToString("0.0") + " nmi)");
                if (fcode == null && fblocked.Count > 0) fcode = "range_envelope";

                bool fvalidate = ReqBool(req, "validate_only", false);
                int fshots = ReqInt(req, "shots", 1);
                int fpriority = ReqInt(req, "priority", 1);

                o["action"] = "fire";
                o["mode"] = "position";
                o["lat"] = flat;
                o["lon"] = flon;
                o["world"] = new JObject { { "x", fworld.x }, { "y", fworld.y }, { "z", fworld.z } };
                o["worldSource"] = fsource;
                // The point is given at sea level. That is enough for an anti-ship shot and NOT
                // enough for a land target on high ground: the game has a terrain elevation model
                // we do not read yet. Do not let this field look like it knows the height.
                o["heightSource"] = "zero (no terrain elevation model yet)";
                o["ammo"] = fammo;
                o["ammoRequiresTarget"] = fam != null && fammoNeedsTarget;
                o["requiresSolution"] = fam != null && fammoNeedsTarget;
                if (fam != null && fammoNeedsTarget)
                    o["solutionHint"] = "guns need an object to aim at; the ammunition that can be fired at a point are the ones "
                        + "GET /units/{id}/weapons reports with fireAtPoint true";
                o["ammoCheck"] = fammoCheck;
                o["shots"] = fshots;
                o["rangeNm"] = frange;
                o["minRangeMi"] = fmin;
                o["maxRangeMi"] = fmax;
                o["validated"] = fblocked.Count == 0;
                o["executed"] = false;

                if (fblocked.Count > 0)
                {
                    o["refused"] = true;
                    o["refuseCode"] = fcode;
                    o["ok"] = false;
                    o["reasons"] = JArray.FromObject(fblocked);
                    // No force flag on purpose: force exists to bypass a relationship verdict, and
                    // neither an envelope miss nor a target-required weapon is a policy.
                }
                else if (fvalidate)
                {
                    o["ok"] = true;
                    o["wouldFire"] = fammo;
                }
                else
                {
                    int fammoBefore = AmmoCount(u, fammo);
                    // D1 tail: keep the task the game hands back. InsertEngageTask returns null
                    // when the hull has no task graph (_userRoot), when the point cannot be used,
                    // or when this ammunition will not be delivered at a position - and until now
                    // that answer was thrown away, so "queued" and "silently dropped" were
                    // indistinguishable. /attack has carried this evidence since round 6.
                    object fEngageTask = u.InsertEngageTask(fammo, null, fworld, fshots, fpriority, true, false, false);
                    int fammoAfter = AmmoCount(u, fammo);
                    o["executed"] = fEngageTask != null;
                    o["taskQueued"] = fEngageTask != null;
                    o["submitted"] = fEngageTask != null;
                    if (fEngageTask != null)
                        o["task"] = EngageProof(u, fEngageTask, fammo, fshots);
                    else
                    {
                        o["ok"] = false;
                        o["refused"] = true;
                        o["refuseCode"] = "task_refused";
                        o["reasons"] = JArray.FromObject(new string[] {
                            "the game returned no EngageTask for this position shot, so nothing was queued: "
                            + "InsertEngageTask returns null when the hull has no task graph (_userRoot), when the "
                            + "point cannot be used, or when this ammunition cannot be delivered at a position. "
                            + "Request was ammo='" + fammo + "', shots=" + fshots + ", lat=" + flat + ", lon=" + flon + "."
                        });
                    }
                    o["ammoBefore"] = fammoBefore;
                    o["ammoAfter"] = fammoAfter;
                    bool ffired = fammoBefore >= 0 && fammoAfter >= 0 && fammoAfter < fammoBefore;
                    o["confirmed"] = ffired;
                    o["confirmation"] = fEngageTask == null
                        ? "not_submitted"
                        : (fammoBefore < 0 || fammoAfter < 0
                            ? "unavailable"
                            : (ffired ? "fired" : "unverified"));
                    if (fEngageTask == null)
                        o["confirmNote"] = "no task was queued (InsertEngageTask returned null), so no round can leave from this call";
                    else if (!ffired)
                        o["confirmNote"] = fammoBefore < 0 || fammoAfter < 0
                            ? "ammunition count unavailable for this weapon; cannot confirm the shot"
                            : "the magazine still read " + fammoBefore + " immediately after submitting. Area fire still needs a weapon the platform can actually deliver, so re-read /units/" + u.UniqueID + "/ammo and orderText after a few seconds before calling it dropped.";
                    try
                    {
                        o["orderText"] = SafeStr(Rx(u, "CurrentOrderText"));
                    }
                    catch (Exception) { }
                }
            }
            else if (op == "identify")
            {
                int targetId = ReqInt(req, "target", -1);
                // REVIEW (round 3, important 10): ordering a hull to identify a contact the
                // player cannot see answered differently for an id that exists and one that does
                // not - an existence oracle wearing an order's clothes. Same gate as /units.
                ObjectBase target = FindKnownUnit(targetId);
                u.setOrder(Order.Type.Identify, target, true);
                o["action"] = "identify";
                o["target"] = targetId;
            }
            else if (op == "altitude")
            {
                // NEW (2026-11-05): an absolute altitude channel. Waypoint height_state is a
                // band preset, and measured live on two F-4J it moved the altitude by exactly
                // nothing (Low, Medium and VeryHigh all left them at ~730-975). ObjectBase
                // carries the pair the game itself writes when an order changes altitude:
                // _desiredAltitudeToSet (Single, meters) and _setDesiredAltitude (Boolean).
                int feetReq = req["feet"] != null ? ReqInt(req, "feet", 0) : -1;
                int metersReq = req["meters"] != null ? ReqInt(req, "meters", 0) : -1;
                if (feetReq < 0 && metersReq < 0)
                    throw new ApiException(400, "bad_request",
                        "give 'feet' or 'meters' (absolute altitude, above sea level)", false);
                double metersWanted = feetReq >= 0 ? feetReq / 3.280839895013123 : metersReq;
                bool wroteValue = SetMember(u, "_desiredAltitudeToSet", (float)metersWanted);
                bool wroteFlag = SetMember(u, "_setDesiredAltitude", true);
                o["action"] = "altitude";
                o["metersRequested"] = Math.Round(metersWanted, 1);
                o["feetRequested"] = Math.Round(metersWanted * 3.280839895013123, 0);
                o["desiredAltitudeWritten"] = wroteValue;
                o["applyFlagWritten"] = wroteFlag;
                o["altitudeNowFt"] = SafeNum(Rx(u, "HeightInFeet"));
                o["note"] = "the game applies a desired altitude on its own tick; read heightInFeet back after a second or two";
            }
            else if (op == "rtb")
            {
                // m04026: with a null base ReturnToBase was a display order only. The
                // aircraft kept circling the carrier (landingInProgress stayed false) and
                // the reply still claimed a return. Resolve the hull's own home base; if
                // there is none, refuse instead of pretending.
                object hbRaw = null;
                try { InvokeN(u, "SearchForHomeBase", new Type[0], new object[0]); }
                catch (Exception) { }
                hbRaw = Member(u, "_homeBase");
                if (hbRaw == null)
                {
                    try { hbRaw = InvokeN(u, "getHomeBase", new Type[0], new object[0]); }
                    catch (Exception) { hbRaw = null; }
                }
                ObjectBase homeBase = hbRaw as ObjectBase;
                if (homeBase == null)
                    throw new ApiException(409, "no_home_base",
                        "no home base is known for this unit, so ReturnToBase would be a display order only and"
                        + " nothing was ordered. A carrier air group is recalled with POST /units/<carrier>/recover.",
                        false);
                u.setOrder(Order.Type.ReturnToBase, homeBase, true);
                o["action"] = "rtb";
                o["homeBase"] = SafeNum(Rx(homeBase, "UniqueID"));
            }
            else if (op == "ceasefire")
            {
                // ACT-008: these six used to be hardcoded true while the whitelist
                // accepted clear/report/stop_jamming and ignored them - a caller could
                // ask for anything and always get the full stop. Now each flag maps
                // 1:1 onto the native parameter; the defaults are unchanged, so an
                // empty body still produces the exact call it always did.
                bool cfReport = ReqBool(req, "report", true);
                bool cfClearOrder = ReqBool(req, "clear_order", true);
                bool cfFormation = ReqBool(req, "formation_check", true);
                bool cfStopJamming = ReqBool(req, "stop_jamming", true);
                bool cfAirFormation = ReqBool(req, "air_formation_check", true);
                bool cfClearAttack = ReqBool(req, "clear_attack_tasks", true);
                u.CeaseFire(cfReport, cfClearOrder, cfFormation,
                            cfStopJamming, cfAirFormation, cfClearAttack);
                o["action"] = "ceasefire";
                o["applied"] = new JObject();
                o["applied"]["report"] = cfReport;
                o["applied"]["clear_order"] = cfClearOrder;
                o["applied"]["formation_check"] = cfFormation;
                o["applied"]["stop_jamming"] = cfStopJamming;
                o["applied"]["air_formation_check"] = cfAirFormation;
                o["applied"]["clear_attack_tasks"] = cfClearAttack;
            }
            else if (op == "emcon")
            {
                bool on = ReqBool(req, "on", true);
                u.setEMCON(on, true);
                o["action"] = "emcon";
                o["on"] = on;
            }
            else if (op == "radar")
            {
                bool on = ReqBool(req, "on", true);
                if (on) u.EnableAllRadars(true);
                else u.DisableAllRadars();
                o["action"] = "radar";
                o["on"] = on;
            }
            else if (op == "sonar")
            {
                bool on = ReqBool(req, "on", true);
                if (on) u.EnableActiveSonars(false, true, true);
                else u.DisableActiveSonars(false, true, true);
                o["action"] = "sonar";
                o["on"] = on;
            }
            else if (op == "launch")
            {
                // T19.5: the flight-deck gate comes BEFORE any game call, so asking a destroyer
                // to launch a plane is refused instead of silently returning false from the game.
                object deck = DeckObject(u);
                if (deck == null)
                    throw new ApiException(409, "no_flight_deck",
                        "unit " + id + " (" + u.GetType().Name + ") has no flight deck", false);

                string aircraft = ReqStr(req, "aircraft", null);
                string loadout = ReqStr(req, "loadout", null);
                string squadron = ReqStr(req, "squadron", null);
                // m04013: an omitted loadout defaulted to AirToAir, so a strike package
                // took off with AIM-7s and could not bomb anything. Say what you carry;
                // GET /units/{id}/deck lists the loadouts this airframe accepts.
                if (string.IsNullOrEmpty(loadout))
                    throw new ApiException(400, "loadout_required",
                        "POST /units/" + id + "/launch needs an explicit 'loadout' (Strike, SEAD, CAP, ...)."
                        + " It used to default to AirToAir, which is how a bombing run left the deck with"
                        + " air-to-air missiles. GET /units/" + id + "/deck lists what this airframe accepts.",
                        false);
                bool requireExact = ReqBool(req, "require_exact_loadout", false);
                int launchCount = ReqInt(req, "launch_count", 0);
                string missionName = ReqStr(req, "mission", null);

                int airborneBefore = CountAirborne(u);

                o["action"] = "launch";
                o["aircraftRequested"] = aircraft;
                o["loadoutRequested"] = loadout;
                o["squadronRequested"] = squadron;
                o["mission"] = missionName == null ? "None" : missionName;
                o["airborneBefore"] = airborneBefore;

                bool ok;
                if (aircraft == null)
                {
                    // Legacy path, kept so existing callers behave exactly as before. Selects by
                    // ObjectType.Aircraft + UnitRoles.Fighter, i.e. the game picks the airframe.
                    o["selectionPath"] = "type+role (ObjectType.Aircraft / UnitRoles.Fighter) - the game picks the airframe";
                    ok = u.FlightDeckLaunchUnit(
                        ObjectBase.ObjectType.Aircraft,
                        loadout,
                        ObjectBaseParameters.UnitRoles.Fighter,
                        ParseMissionType(missionName),
                        true, 0f, true);
                }
                else
                {
                    // Exact path: fileName must match VehicleTypeOnBoard._fileName byte for byte
                    // (the game compares with String.op_Inequality, an ordinal compare).
                    o["selectionPath"] = "exact file name (5-argument FlightDeckLaunchUnit)";
                    SeaPower.LaunchTaskParameters ltp = new SeaPower.LaunchTaskParameters();
                    SetMember(ltp, "_missionType", ParseMissionType(missionName));
                    if (launchCount > 0) SetMember(ltp, "_launchCount", launchCount);
                    SetMember(ltp, "_fromScript", true);
                    o["launchCount"] = launchCount;

                    object[] args = new object[] { aircraft, ltp, loadout, requireExact, squadron };
                    object ret = InvokeN(u, "FlightDeckLaunchUnit",
                        new Type[] { typeof(string), typeof(SeaPower.LaunchTaskParameters), typeof(string), typeof(bool), typeof(string) },
                        args);
                    ok = ret is bool && (bool)ret;
                }

                o["ok"] = ok;
                o["accepted"] = ok;
                if (!ok)
                {
                    o["why"] = "the game returned false: the airframe/loadout is not on board, the loadout"
                             + " could not be matched (require_exact_loadout=true makes a miss fatal),"
                             + " or Globals._cheatFlightDeckAmmo is off so createLaunchTask returns null";
                }
                o["airborneAfter"] = CountAirborne(u);
                o["confirmation"] = "submitted";
                o["next"] = "the spawn can happen a moment later: poll GET /units/" + id + "/airborne to see the actual aircraft objects";
            }
            else if (op == "recover")
            {
                bool recoverAll = ReqBool(req, "all", false);
                bool fallbackAll = ReqBool(req, "fallback_all", false);
                // Filled in on the single-aircraft path so the tail of this branch can tell
                // whether the game actually queued anything for that aircraft.
                int recoveredId = -1;

                object deck = DeckObject(u);
                if (deck == null)
                    throw new ApiException(409, "no_flight_deck",
                        "unit " + id + " (" + u.GetType().Name + ") has no flight deck", false);

                JObject recBefore = DeckTasks(id);

                if (recoverAll)
                {
                    // B28: RecoverAll on an airgroup that is already coming home stacks a second
                    // task on every aircraft, and the deck then ticks a FlightDeckTask whose
                    // transform no longer exists (10,612 NREs on the B27 stack). Look first.
                    System.Collections.Generic.List<int> airborneIds = new System.Collections.Generic.List<int>();
                    System.Collections.Generic.List<int> needHome = new System.Collections.Generic.List<int>();
                    JArray alreadyHome = new JArray();
                    try
                    {
                        System.Collections.IEnumerable air = ChildAircraft(u);
                        if (air != null)
                        {
                            foreach (object ob in air)
                            {
                                ObjectBase ac = ob as ObjectBase;
                                if (ac == null) continue;
                                int acId = UnitIdOf(ac);
                                if (acId <= 0) continue;
                                airborneIds.Add(acId);
                                if (HomeTasksForVehicle(recBefore, acId).Count > 0) alreadyHome.Add(acId);
                                else needHome.Add(acId);
                            }
                        }
                    }
                    catch (Exception) { }

                    o["action"] = "recover";
                    o["scope"] = "all";
                    o["airborneCount"] = airborneIds.Count;
                    if (alreadyHome.Count > 0) o["alreadyReturning"] = alreadyHome;

                    if (airborneIds.Count > 0 && needHome.Count == 0)
                    {
                        o["skipped"] = "every airborne aircraft already has a ReturnTask or RecoveryTask";
                        o["note"] = "RecoverAll was NOT called: asking again would queue a second home task "
                            + "per aircraft, and the deck has been seen throwing a NullReferenceException every "
                            + "fixed update when one aircraft carries two competing tasks (B27/B28).";
                        o["confirmation"] = "already_returning";
                    }
                    else
                    {
                        // FlightDeck.RecoverAll(Boolean instant, Boolean abortAllTasks) - the whole
                        // airgroup at once, which is also the only recovery path T19.5 has not seen
                        // silently do nothing yet (see the note below).
                        InvokeN(deck, "RecoverAll", new Type[] { typeof(bool), typeof(bool) },
                            new object[] { false, false });
                    }
                }
                else
                {
                    int acId = ReqInt(req, "aircraft", 0);
                    // REVIEW (round 3, important 10): same oracle as identify, and the null check
                    // was already dead - FindUnit throws rather than answering null. The knowledge
                    // gate is the one that matters: a plane the player has not detected is not
                    // "not ours", it is not there.
                    ObjectBase target = FindKnownUnit(acId);

                    // The task card says "same aircraft recovery", so refuse to vector home someone
                    // else's plane: only an object this deck actually launched can be recovered here.
                    if (!IsOurAircraft(u, target))
                        throw new ApiException(409, "not_our_aircraft",
                            "unit " + acId + " is not in unit " + id + "'s _childAircraft list; this deck did not launch it", false);

                    // B28: never stack a second home task on the same aircraft. This exact call
                    // produced 10,612 NullReferenceExceptions on the B27 stack.
                    System.Collections.Generic.List<JObject> home = HomeTasksForVehicle(recBefore, acId);

                    o["action"] = "recover";
                    o["scope"] = "one";
                    o["aircraft"] = acId;
                    o["aircraftName"] = SafeStr(Rx(target, "DisplayNameFull"));
                    o["alreadyHomeTasks"] = TaskSummaries(home);

                    if (home.Count > 0)
                    {
                        o["skipped"] = "this aircraft already carries a ReturnTask or RecoveryTask";
                        o["note"] = "createReturnTask was NOT called: the aircraft is already being brought home. "
                            + "Calling it again queues a second task, and the deck has been seen throwing a "
                            + "NullReferenceException every fixed update when that happens (B27/B28). Watch 'info' "
                            + "on the task above instead: last approach / landing mean it is nearly down, and the "
                            + "aircraft leaves GET /units/{id}/airborne once it lands.";
                        o["confirmation"] = "already_returning";
                    }
                    else
                    {
                        InvokeN(deck, "createReturnTask", new Type[] { typeof(ObjectBase) }, new object[] { target });
                        recoveredId = acId;
                    }
                }

                JObject recAfter = DeckTasks(id);
                o["tasksBefore"] = recBefore["count"];
                o["tasksAfter"] = recAfter["count"];

                // B25, judged by NEW uid rather than by presence. Presence is a confounded test:
                // an aircraft can already carry a ReturnTask from an earlier recall or from the
                // game's own fuel RTB, so "a task names this aircraft" stays true even when the
                // call just placed queued nothing - the first version of this check was fooled by
                // exactly that. Live runs have seen both outcomes: two recalls of an SH-3H queued
                // nothing (the queue was emptied first, to rule out a busy deck), while a later
                // recall of an SH-2F added a third ReturnTask to the two it already had.
                bool recSeen = false;
                if (recoveredId > 0)
                {
                    JObject recTask = NewTaskForVehicle(recBefore, recAfter, recoveredId);
                    recSeen = recTask != null;
                    o["observedTask"] = recSeen ? (JToken)recTask["clrType"] : JValue.CreateNull();
                    o["tasksBeforeForAircraft"] = TaskUidsForVehicle(recBefore, recoveredId).Count;
                    o["tasksAfterForAircraft"] = TaskUidsForVehicle(recAfter, recoveredId).Count;
                }

                if (recoveredId > 0 && !recSeen && fallbackAll)
                {
                    // Explicit opt-in only: recalling the whole airgroup is a wider action than
                    // the caller asked for, so it is never taken on the caller's behalf.
                    JObject beforeFallback = recAfter;
                    InvokeN(deck, "RecoverAll", new Type[] { typeof(bool), typeof(bool) },
                        new object[] { false, false });
                    recAfter = DeckTasks(id);
                    JObject viaFallback = NewTaskForVehicle(beforeFallback, recAfter, recoveredId);
                    recSeen = viaFallback != null;
                    o["fallback"] = "all";
                    o["tasksAfter"] = recAfter["count"];
                    if (recSeen) o["observedTask"] = (JToken)viaFallback["clrType"];
                    o["tasksAfterForAircraft"] = TaskUidsForVehicle(recAfter, recoveredId).Count;
                }

                if (recoveredId > 0)
                {
                    o["confirmation"] = recSeen ? "submitted" : "submitted_no_visible_task";
                    if (!recSeen)
                    {
                        o["note"] = "createReturnTask answered, but the immediate read-back saw no deck task for "
                            + "this aircraft. That is an observation, not a verdict: the game schedules deck work on "
                            + "its own clock, and an approach plus landing takes minutes of game time. Measured with "
                            + "time compression at 10x: in every case that worked a ReturnTask was visible within "
                            + "about one game second, while one aircraft (an SH-3H, at 1x) never produced one inside "
                            + "70 game seconds. Raise time compression, poll GET /units/" + id + "/decktasks, and only "
                            + "then consider fallback_all:true or all:true if the aircraft really must come back now. "
                            + "Only meaningful while the aircraft is airborne - a ReturnTask raised for one still "
                            + "sitting in the launch queue disappeared again. Give it a waypoint over safe water "
                            + "first when it must stay clear of threats.";
                        o["next"] = "POST {\"compression\": 10} to /time, poll GET /units/" + id + "/decktasks for at "
                            + "least 120 game seconds, and only then POST "
                            + "{\"unit\": " + id + ", \"aircraft\": " + recoveredId + ", \"fallback_all\": true}";
                        return o;
                    }
                }
                else
                {
                    o["confirmation"] = "submitted";
                }

                // The game's own fuel-triggered recall looks exactly like this (orderText
                // "return to base - minimum fuel" plus a ReturnTask labelled "return to base"), so
                // the state recover produces is the state the game produces natively.
                // B28: when nothing was submitted the response must say nothing was submitted.
                // This tail is shared by every path, so it used to overwrite the skip answer
                // with the success wording.
                if (o["skipped"] != null)
                {
                    o["confirmation"] = "already_returning";
                }
                else
                {
                o["verification"] = "poll GET /units/" + id + "/decktasks until a ReturnTask names this aircraft, then GET /units/" + id + "/airborne until it is gone - allow minutes of game time";
                o["note"] = "the game chains a ReturnTask and then its own RecoveryTask, and lands the aircraft by itself - recalled aircraft came back on deck in a live run, with the whole chain visible at 10x. Everything the game does after the task is queued is on its own clock: a carrier recovery is an approach pattern, so raise time compression rather than waiting in real time. It is only meaningful while the aircraft is airborne: a ReturnTask raised for one still sitting in the launch queue disappeared again. Give the aircraft a waypoint over safe water first when it must stay clear of threats. The bridge answers as soon as the task is submitted, landing not observed by the bridge.";
                o["next"] = "poll GET /units/" + id + "/decktasks and GET /units/" + id + "/airborne to watch it come down";
                }
            }
            else if (op == "abort")
            {
                object deck = DeckObject(u);
                if (deck == null)
                    throw new ApiException(409, "no_flight_deck",
                        "unit " + id + " (" + u.GetType().Name + ") has no flight deck", false);

                string taskUid = ReqStr(req, "task", null);
                bool abortAll = ReqBool(req, "all", false);
                // B27. delayed defaults to TRUE, and that default is a safety decision, not a
                // taste one. A live run cancelled a launch task while its aircraft was already
                // taxiing; Player.log then held 35,032 NullReferenceExceptions on one stack -
                // FlightDeck.OnFixedUpdate -> FlightDeckTask.fixedTickStateMachine ->
                // StateMachine.OnFixedUpdate -> ClearRunway.onFixedUpdate -> Taxi.onFixedUpdate ->
                // GroundTaxiPhysics.get_PositionError -> Component.get_transform - i.e. the deck
                // kept ticking a task whose aircraft transform was gone, every single FixedUpdate.
                // abortLaunchTask(Guid, Boolean delayed) is the game's own way of asking for the
                // abort to be deferred to a safe point, so it is the default here.
                bool abortDelayed = ReqBool(req, "delayed", true);   // the consent check lives in ValidateUnitCommandFields

                // Snapshot the task list so the response can prove which tasks were cancelled and
                // which were left alone - "we cancelled something" is not evidence.
                JObject before = DeckTasks(id);

                if (abortAll)
                {
                    // abortAllLaunchTasks() has no delayed parameter, so it cannot express the safe
                    // form at all - and it is the call that produced B27. "all" is done here by
                    // asking once per queued launch task, which is the same API the single-task
                    // form uses, with the same delayed semantics.
                    JArray queued = before["tasks"] as JArray;
                    System.Collections.Generic.List<string> targeted =
                        new System.Collections.Generic.List<string>();
                    if (queued != null)
                    {
                        for (int i = 0; i < queued.Count; i++)
                        {
                            JObject qt = queued[i] as JObject;
                            if (qt == null) continue;
                            string qtType = SafeStr(qt["clrType"]);
                            if (qtType != "LaunchTask" && qtType != "PendingLaunchTask") continue;
                            string qtUid = SafeStr(qt["uid"]);
                            if (qtUid == null) continue;
                            InvokeN(deck, "abortLaunchTask", new Type[] { typeof(System.Guid), typeof(bool) },
                                new object[] { new Guid(qtUid), abortDelayed });
                            targeted.Add(qtUid);
                        }
                    }
                    o["action"] = "abort";
                    o["scope"] = "all";
                    o["delayed"] = abortDelayed;
                    o["tasksTargeted"] = targeted.Count;
                    o["tasksTargetedUids"] = new JArray(targeted.ToArray());
                    o["scopeNote"] = "only queued launch tasks (LaunchTask / PendingLaunchTask) are targeted; a "
                        + "ReturnTask or RecoveryTask is never touched. This walks the queue and calls "
                        + "abortLaunchTask(uid, delayed) per task - abortAllLaunchTasks() takes no delayed flag and "
                        + "was the call behind B27, so it is not used.";
                }
                else
                {
                    System.Guid g = new Guid(taskUid);
                    InvokeN(deck, "abortLaunchTask", new Type[] { typeof(System.Guid), typeof(bool) },
                        new object[] { g, abortDelayed });
                    o["action"] = "abort";
                    o["scope"] = "one";
                    o["task"] = taskUid;
                    o["delayed"] = abortDelayed;
                    // Which kind of task was named. A live run found the queue holding both
                    // LaunchTask and ReturnTask entries. abortLaunchTask is a launch abort, so a
                    // ReturnTask (what recover creates) is not expected to be cancellable here,
                    // and naming one must not be reported as a successful cancel.
                    o["taskType"] = TaskTypeOf(before, taskUid);
                    if (o["taskType"] != null && (string)o["taskType"] != "LaunchTask"
                        && (string)o["taskType"] != "PendingLaunchTask")
                        o["taskTypeNote"] = "this is not a launch task: abortLaunchTask cancels queued launches, and a "
                            + (string)o["taskType"] + " is what recover creates. Expect no effect.";
                }

                JObject afterTasks = DeckTasks(id);
                int beforeCount = before["count"] == null ? -1 : (int)before["count"];
                int afterCount = afterTasks["count"] == null ? -1 : (int)afterTasks["count"];
                o["tasksBefore"] = beforeCount;
                o["tasksBeforeDetail"] = before["tasks"];
                o["tasksAfter"] = afterCount;
                o["removed"] = (beforeCount >= 0 && afterCount >= 0) ? beforeCount - afterCount : (int?)null;
                o["confirmation"] = "submitted";
                // T19.5: a live run showed a queued aircraft still launch after this call, so a
                // shrinking list is the only thing that counts as evidence. Say so instead of
                // letting "submitted" read as "cancelled".
                if (beforeCount >= 0 && afterCount >= 0 && beforeCount - afterCount <= 0)
                    o["note"] = "the deck task list did not shrink. Live runs disagree: one removed a task, another "
                        + "returned removed=0 while the queued aircraft still took off. A 200 here is not proof that a "
                        + "launch was cancelled. Also note the game schedules its own deck work, so read this on a "
                        + "game-time budget (raise time compression, poll) rather than immediately";
                o["verification"] = "poll GET /units/" + id + "/airborne; if the aircraft is still listed there, the abort did not take effect";
            }
            else if (op == "rearm")
            {
                // ACT-008: 'reload_broken' was whitelisted and then dropped on the floor -
                // RearmAll(false) ignores what the caller asked for. Default false keeps
                // the old call exactly; true now actually reloads broken mounts.
                bool reloadBroken = ReqBool(req, "reload_broken", false);
                u.RearmAll(reloadBroken);
                o["action"] = "rearm";
                o["applied"] = new JObject();
                o["applied"]["reload_broken"] = reloadBroken;
            }
            else if (op == "waypointClear")
            {
                u.RemoveWaypoints();
                o["action"] = "waypointClear";
                // D2: RemoveWaypoints only removes waypoints. Attack tasks live on
                // ObjectBase._userRoot and an AttackAtWaypoint carries its own waypoint, so a
                // hull that was given one keeps flying to it after a successful clear (measured:
                // a BM-21 ordered to attack a position held 40.5 kt with orderText "heading for
                // waypoint" while waypointClear read back as 200). The clearing op already
                // exists - ceasefire passes clear_attack_tasks (default true) to
                // ObjectBase.CeaseFire - and this receipt is the only place a caller would
                // learn that.
                o["clearScope"] = "waypoints only";
                o["engageTasksAfter"] = TaskCount(u);
                o["hint"] = "waypointClear removes waypoints, not queued tasks. If the hull keeps"
                    + " moving or firing, the order came from a task: POST /units/" + u.UniqueID
                    + "/ceasefire {\"clear_attack_tasks\":true} is the op that clears those"
                    + " (clear_attack_tasks defaults to true).";
            }
            else if (op == "vertical")
            {
                // T19.2: immediate vertical command. Type gate comes FIRST, before any game
                // call, so asking a destroyer to dive is refused instead of silently no-op'd.
                string vmode = ReqStr(req, "mode", "");
                o["action"] = "vertical";
                o["mode"] = vmode;
                o["altitudeBefore"] = SafeNum(Rx(u, "Altitude"));
                o["desiredAltitudeBefore"] = SafeNum(Rx(u, "DesiredAltitude"));

                bool subOnly = vmode == "depth" || vmode == "periscope" || vmode == "preset_depth" || vmode == "push_waypoints";
                if (subOnly && !(u is SeaPower.Submarine))
                    throw new ApiException(409, "wrong_unit_type",
                        "'" + vmode + "' is a submarine action; unit " + id + " is " + u.GetType().Name, false);
                if (vmode == "preset_height" && !(u is SeaPower.Aircraft) && !(u is SeaPower.Helicopter))
                    throw new ApiException(409, "wrong_unit_type",
                        "'preset_height' is an air action; unit " + id + " is " + u.GetType().Name, false);

                if (vmode == "depth")
                {
                    float meters = ReqFloat(req, "meters", 0f);
                    if (!HasMethod(u, "setDepth", new Type[] { typeof(float) }))
                        throw new ApiException(409, "unsupported_for_unit_type", "this unit has no setDepth(float)", false);
                    // Refuse a depth this hull cannot survive BEFORE the game sees it.
                    // Live incident: meters=150 came back 200 and the game clamped
                    // DesiredAltitude to a fixed value past the deepest preset, and the
                    // boat sank below 450 m and never recovered. The hull publishes its
                    // own preset table, so the deepest safe depth is that maximum in
                    // metres (1 ft = 0.3048 m, SI - no game constant is copied here).
                    double deepestPresetMeters = double.NaN;
                    try
                    {
                        double deepestFeet = double.NaN;
                        for (int pi = 0; pi <= 6; pi++)
                        {
                            object pf = InvokeN(u, "GetPresetDepthFeet", new Type[] { typeof(int) }, new object[] { pi });
                            if (pf == null) continue;
                            double f = Convert.ToDouble(pf);
                            if (double.IsNaN(deepestFeet) || f > deepestFeet) deepestFeet = f;
                        }
                        if (!double.IsNaN(deepestFeet) && deepestFeet > 0.0) deepestPresetMeters = deepestFeet * 0.3048;
                    }
                    catch (Exception) { deepestPresetMeters = double.NaN; }
                    // Tier 2: the one safe ceiling that was actually measured. The game
                    // does not keep metre values above ~110 m - it replaces them with a fixed
                    // depth past the crush depth - so by default refuse that band instead of
                    // letting a caller walk into it. allow_beyond_measured is the documented
                    // escape hatch, and it does NOT disable the preset-table refusal below.
                    const double MeasuredSafeCellingM = 110.0;
                    bool allowBeyondMeasured = ReqBool(req, "allow_beyond_measured", false);
                    if (!allowBeyondMeasured && (double)meters > MeasuredSafeCellingM)
                    {
                        throw new ApiException(400, "depth_beyond_measured_ceiling",
                            "'" + meters + " m' is above the deepest depth ORDER that was measured to survive (110 m - the boat itself can sit deeper than that, but an order deeper than that was never survived). Measured on 091G: 20..110 m were kept exactly, while 120 m and above were all replaced by one fixed value past the crush depth, after which the boat kept sinking to -482 m and depth orders stopped working. Nothing was submitted. Use preset_depth to reach the hull's own deep presets, keep mode=depth at or below 110 m, or pass allow_beyond_measured:true if you have your own reason to risk the hull", false);
                    }
                    if (!double.IsNaN(deepestPresetMeters) && (double)meters > deepestPresetMeters + 0.5)
                    {
                        throw new ApiException(400, "depth_beyond_preset",
                            "'" + meters + " m' is deeper than this hull's deepest preset (" +
                            Math.Round(deepestPresetMeters, 1) + " m). Measured on 091G: above ~110 m the game stops keeping the number and clamps DesiredAltitude to a fixed value past the crush depth, and the boat sinks for good - it does not surface again. Nothing was submitted; ask for a depth at or below the deepest preset, or use preset_depth and pass the preset index", false);
                    }
                    InvokeN(u, "setDepth", new Type[] { typeof(float) }, new object[] { meters });
                    o["meters"] = meters;
                    o["signConvention"] = "positive = deeper (the game stores DesiredAltitude = -meters)";
                    // T19.2 live finding: setDepth does NOT always keep the number it is given.
                    // Measured on a 091G: 20/60/90/100/110 metres read back as exactly -20/-60/
                    // -90/-100/-110, while 120 and above all read back as -1120 - the hull's
                    // deepest preset expressed in FEET. A caller that trusts "150 metres" takes
                    // the boat to 341 m. Say so instead of reporting a clean submit.
                    // Measurements and reasoning: docs\VERTICAL_DEPTH_UNITS.md
                    double wantDesired = -(double)meters;
                    double gotDesired = double.NaN;
                    try
                    {
                        object gotRaw = Rx(u, "DesiredAltitude");
                        if (gotRaw != null) gotDesired = Convert.ToDouble(gotRaw);
                    }
                    catch (Exception) { gotDesired = double.NaN; }
                    if (!double.IsNaN(gotDesired) && Math.Abs(gotDesired - wantDesired) > 0.6)
                    {
                        o["clamped"] = true;
                        o["clampedDeeperThanRequested"] = Math.Abs(gotDesired) > Math.Abs(wantDesired);
                        o["requestedMeters"] = SafeNum(meters);
                        o["clampNote"] = "HAZARD: the game did not keep the depth that was asked for (asked " +
                            meters + " m; DesiredAltitude is now " + Math.Round(gotDesired, 3) +
                            "). Measured on 091G: 110 m reads back exactly, while 120 m and above all snap to one fixed value that is past the hull's crush depth - the boat kept sinking to -482 m and further depth orders had no effect on it any more. Treat this boat as lost: surface or move it now, do not send more depth orders. The safe ceiling is hull-specific and the bridge does not guess it; use preset_depth for a depth the hull itself offers, and re-read GET /units/{id}/vertical before assuming anything.";
                    }
                }
                else if (vmode == "periscope")
                {
                    if (!HasMethod(u, "setPeriscopeDepth", NoArgTypes))
                        throw new ApiException(409, "unsupported_for_unit_type", "this unit has no setPeriscopeDepth()", false);
                    InvokeN(u, "setPeriscopeDepth", NoArgTypes, new object[0]);
                    o["note"] = "drops to the hull's own periscope depth; the value is not exposed, read DesiredAltitude after";
                }
                else if (vmode == "preset_depth")
                {
                    int preset = ReqInt(req, "preset", 0);
                    bool upd = ReqBool(req, "update_waypoints", false);
                    // T19.4 range guard: the table has exactly 7 entries (0..6) and the game's
                    // own cyclePresetDepth clamps to that. setPresetDepth does NOT clamp, so an
                    // out-of-table value would silently resolve to a switch default instead of
                    // failing. Refuse before any native call. The data-only range (0..50) cannot
                    // know the table size, so this capability check runs after the type gate.
                    if (preset < 0 || preset > 6)
                        throw new ApiException(400, "out_of_range",
                            "preset must be 0..6 for a submarine (7 preset depths); got " + preset +
                            ". The game would silently resolve an out-of-table index instead of failing, so nothing was submitted", false);
                    if (!HasMethod(u, "setPresetDepth", new Type[] { typeof(int), typeof(bool) }))
                        throw new ApiException(409, "unsupported_for_unit_type", "this unit has no setPresetDepth(int,bool)", false);
                    object feet = InvokeN(u, "setPresetDepth", new Type[] { typeof(int), typeof(bool) }, new object[] { preset, upd });
                    o["preset"] = preset;
                    o["updateWaypoints"] = upd;
                    o["feet"] = SafeNum(feet);
                    o["desiredAltitudeUnitNote"] = "DesiredAltitude is NOT metres on this path: setPresetDepth writes -(feet * <preset scale factor>), a fixed multiplier that is documented but deliberately not repeated in code (C53). Measured on a 091G: 100 ft -> -0.454, 240 ft -> -1.089, 1120 ft -> -5.08, i.e. the same numbers divided by the factor. Never compare it with altitude (metres) or with a value written by mode=depth; 'feet' is the authority here";
                }
                else if (vmode == "preset_height")
                {
                    int preset = ReqInt(req, "preset", 0);
                    bool upd = ReqBool(req, "update_waypoints", false);
                    // Range guard against the airframe's own table. setPresetHeight clamps the
                    // index to Length-1 instead of failing, so an out-of-range preset would
                    // quietly become "the highest preset" - refuse it and state the real size.
                    object apg = Member(u, "Ap");
                    if (apg == null) apg = Member(u, "AP");
                    if (apg == null) apg = Member(u, "_ap");
                    float[] guardAlts = Member(apg, "_presetAltitudes") as float[];
                    if (guardAlts != null && (preset < 0 || preset >= guardAlts.Length))
                        throw new ApiException(400, "out_of_range",
                            "preset must be 0.." + (guardAlts.Length - 1) + " for this airframe; it has " +
                            guardAlts.Length + " preset altitudes. The game clamps instead of failing, so nothing was submitted", false);
                    if (!HasMethod(u, "setPresetHeight", new Type[] { typeof(int), typeof(bool) }))
                        throw new ApiException(409, "unsupported_for_unit_type", "this unit has no setPresetHeight(int,bool)", false);
                    object ft = InvokeN(u, "setPresetHeight", new Type[] { typeof(int), typeof(bool) }, new object[] { preset, upd });
                    o["preset"] = preset;
                    o["updateWaypoints"] = upd;
                    o["reportedFeet"] = SafeNum(ft);
                    if (upd)
                        o["updateWaypointsNote"] = "Aircraft.setPresetHeight(...,true) calls UpdateWaypointsAltitudes, which the game type-gates to Submarine/Biologic, so it does nothing for an aircraft - rebuild the waypoint instead";
                }
                else if (vmode == "push_waypoints")
                {
                    int index = ReqInt(req, "index", 0);
                    if (!HasMethod(u, "UpdateWaypointsAltitudes", new Type[] { typeof(int) }))
                        throw new ApiException(409, "unsupported_for_unit_type", "this unit has no UpdateWaypointsAltitudes(int)", false);
                    InvokeN(u, "UpdateWaypointsAltitudes", new Type[] { typeof(int) }, new object[] { index });
                    o["index"] = index;
                    o["indexMap"] = "0=Number 1=Surface 2=Periscope 3=VeryLow 4=Low 5=Medium 6=High 7=VeryHigh";
                    o["pushNote"] = "only rewrites existing GoToWaypoint/AttackAtWaypoint tasks, and returns early when the current order is ReturnToBase";
                }

                o["altitudeAfter"] = SafeNum(Rx(u, "Altitude"));
                o["desiredAltitudeAfter"] = SafeNum(Rx(u, "DesiredAltitude"));
                o["confirmation"] = "submitted";
                o["confirmNote"] = "altitude/depth changes take time; re-read GET /units/{id}/vertical after a few seconds rather than expecting altitudeAfter to differ immediately";
                // Measured on 207: a boat that is running the game's own patrol order
                // kept descending even though DesiredAltitude read -60, and only the
                // hull's own shallow orders turned it around. Say who owns the depth.
                o["orderText"] = SafeStr(Rx(u, "CurrentOrderText"));
                o["depthOwnership"] = "if orderText names an order (patrol/course/engage), that order"
                    + " owns the depth: a metres write lands in DesiredAltitude but was measured not to"
                    + " arrest a descent. mode=periscope and mode=preset_depth are the hull's own shallow"
                    + " orders and did turn the boat around; /units/{id}/waypointClear drops the order first";
            }
            else
            {
                throw new Exception("unknown unit command: " + op);
            }

            o["state"] = UnitJson(u);
            return o;
        }

        private static JToken TimeCommand(JObject req)
        {
            // compression first: SetTimeCompression internally unpauses, so 'paused'
            // has to be applied afterwards or it gets silently overridden.
            if (req["compression"] != null)
            {
                float c = ReqFloat(req, "compression", 1f);
                GameTime.SetTimeCompression(c);
            }
            if (req["paused"] != null)
            {
                bool paused = ReqBool(req, "paused", false);
                if (paused) GameTime.Pause(true);
                else GameTime.UnPause();
            }
            JObject o = new JObject();
            o["paused"] = GameTime.IsPaused();
            o["timeCompression"] = GameTime.TimeCompression;
            o["missionElapsedTime"] = GameTime.missionElapsedTime;
            return o;
        }

        /// <summary>GET /taskforces - the player's own taskforces.
        ///
        /// REVIEW (round 2, important 7): this called CoreService.GetTaskforces(0L) and listed every
        /// taskforce in the world with every member's display name, with no IntelScope, no TfType
        /// filter and no diagnostics switch. If that API sees both sides it is a complete order of
        /// battle, and since /group takes `tf` by name it was also the front half of "enumerate,
        /// then command the lot". A taskforce is reported when at least one hull in it is the
        /// player's, and the members listed are the player's own hulls.</summary>
        private static JToken Taskforces()
        {
            Dictionary<string, JArray> own = new Dictionary<string, JArray>(StringComparer.Ordinal);
            List<ObjectBase> all = AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u == null || !u.isUnit() || u.IsDestroyed) continue;
                if (!SafeFlag(u, "mine")) continue;
                string tf = TaskforceName(Member(u, "_taskforce"));
                if (tf == null) continue;
                JArray bucket;
                if (!own.TryGetValue(tf, out bucket))
                {
                    bucket = new JArray();
                    own[tf] = bucket;
                }
                bucket.Add(SafeStr(Rx(u, "DisplayNameFull")));
            }

            JArray arr = new JArray();
            try
            {
                CoreService svc = new CoreService();
                List<string> names = svc.GetTaskforces(0L);
                if (names != null)
                {
                    for (int i = 0; i < names.Count; i++)
                    {
                        string name = names[i];
                        if (name == null) continue;
                        JArray mine;
                        if (!own.TryGetValue(name, out mine)) continue;
                        JObject o = new JObject();
                        o["name"] = name;
                        o["unitCount"] = mine.Count;
                        o["units"] = mine;
                        arr.Add(o);
                    }
                }
            }
            catch (Exception e)
            {
                JObject err = new JObject();
                err["error"] = e.Message;
                err["note"] = "CoreService.GetTaskforces failed";
                return err;
            }
            // Any of the player's taskforces that the service did not name still exist in the object
            // layer; report them rather than dropping them silently.
            foreach (KeyValuePair<string, JArray> kv in own)
            {
                bool seen = false;
                for (int i = 0; i < arr.Count; i++)
                {
                    if (String.Equals((string)arr[i]["name"], kv.Key, StringComparison.Ordinal)) { seen = true; break; }
                }
                if (seen) continue;
                JObject o = new JObject();
                o["name"] = kv.Key;
                o["unitCount"] = kv.Value.Count;
                o["units"] = kv.Value;
                o["note"] = "this taskforce is in the object layer but CoreService.GetTaskforces did not list it";
                arr.Add(o);
            }

            JObject res = new JObject();
            res["count"] = arr.Count;
            res["taskforces"] = arr;
            res["scope"] = "the player's own taskforces";
            if (MiniHttp.DiagnosticsEnabled)
            {
                JArray every = new JArray();
                try
                {
                    CoreService svc2 = new CoreService();
                    List<string> names2 = svc2.GetTaskforces(0L);
                    if (names2 != null)
                    {
                        for (int i = 0; i < names2.Count; i++) every.Add(names2[i]);
                    }
                }
                catch (Exception) { }
                res["allTaskforceNames"] = every;
                res["allTaskforceNamesNote"] = "diagnostics.on: this includes foreign taskforces and is never"
                    + " published with diagnostics off";
            }
            else
            {
                res["allTaskforceNamesWithheld"] = new JValue(
                    "how many taskforces exist in the world is intel; create diagnostics.on in the mod folder to publish the list");
            }
            return res;
        }

        // GET /units/{id}/weapons : each magazine entry with its launch-range envelope.
        // AmmunitionParameters keeps these as private fields, so they are read by reflection;
        // minRangeMi is the figure the game refuses to shoot inside of.
        private static JObject Weapons(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null) throw new ApiException(404, "unit_not_found", "unit not found: " + id, false); // I8: same answer as a missing id

            JArray arr = new JArray();
            try
            {
                UniRx.ReactiveDictionary<string, int> dict = u.AmmunitionAmountDictionary;
                foreach (KeyValuePair<string, int> kv in dict)
                {
                    JObject w = new JObject();
                    w["ammo"] = kv.Key;
                    w["count"] = kv.Value;
                    try
                    {
                        Ammunition ammo = u.getAmmunitionByName(kv.Key);
                        if (ammo != null)
                        {
                            object ap = Member(ammo, "_ap");
                            if (ap != null)
                            {
                                w["minRangeMi"] = SafeNum(Member(ap, "_minLaunchRangeInMiles"));
                                w["maxRangeMi"] = SafeNum(Member(ap, "_maxLaunchRangeInMiles"));
                                // Which weapon can be given a bare coordinate: the game answers,
                                // and the caller needs it before choosing /fire over /attack.
                                // Guns and unguided artillery need a target object (= a firing
                                // solution); cruise missiles, anti-ship missiles and bombs fly to
                                // a point and are fireable at a position.
                                SeaPower.AmmunitionParameters apTyped = ap as SeaPower.AmmunitionParameters;
                                if (apTyped != null)
                                {
                                    try
                                    {
                                        bool needsTarget = u.DoesEngageRequiresTarget(apTyped);
                                        w["requiresTarget"] = needsTarget;
                                        w["requiresSolution"] = needsTarget;
                                        w["fireAtPoint"] = !needsTarget;
                                    }
                                    catch (Exception) { w["requiresTarget"] = JValue.CreateNull(); }
                                }
                            }
                        }
                    }
                    catch (Exception e2) { w["rangeError"] = e2.Message; }
                    arr.Add(w);
                }
            }
            catch (Exception e3) { throw new Exception("cannot read ammunition: " + e3.Message); }

            JObject o = new JObject();
            o["unit"] = id;
            o["count"] = arr.Count;
            o["weapons"] = arr;
            // READ-SIDE CONSISTENCY: POST /units/{id}/weapon_status writes the fire discipline, so the
            // route that lists what this hull can shoot has to report that discipline too. Without it a
            // caller cannot tell "no weapons" from "weapons held by doctrine" - and the write op looks
            // like it invented a field. Same private _weaponStatus the setter writes.
            o["weaponStatus"] = WeaponStatusName(u) == null ? JValue.CreateNull() : (JToken)new JValue(WeaponStatusName(u));
            o["weaponStatusField"] = "_weaponStatus";
            return o;
        }

        // GET /units/{id}/deck : read-only flight deck state (T19.3).
        //
        // The deck hangs off the parameters object, not off ObjectBase:
        //     ObjectBase._obp (ObjectBaseParameters) -> _flightDeck (SeaPower.FlightDeck)
        // Both are private fields; the path was confirmed against the IL dump in
        // research\il_t193_deck.txt and research\il_t193_flightdeck.txt.
        //
        // Deliberately no UnitRoles/Carrier gate: escort ships and helicopter carriers own a
        // deck too, so the only question asked here is "is _flightDeck non-null". Nothing in
        // this method launches, recovers, re-arms or prepares anything - it only reads.
        private static JObject Deck(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null)
                throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);

            object obp = null;
            object fd = null;
            string why = null;
            try { obp = Member(u, "_obp"); }
            catch (Exception e1) { why = "_obp threw: " + e1.Message; }
            if (obp == null && why == null) why = "_obp is null";
            if (obp != null)
            {
                try { fd = Member(obp, "_flightDeck"); }
                catch (Exception e2) { why = "_obp._flightDeck threw: " + e2.Message; }
                if (fd == null && why == null) why = "_obp._flightDeck is null";
            }

            JObject o = new JObject();
            o["unit"] = id;
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            o["readPath"] = "ObjectBase._obp._flightDeck";
            // Reported even when the deck object is unreachable: it is how "this platform has
            // no deck" is told apart from "a deck may exist but I could not reach it".
            o["deckStateText"] = SafeStr(Member(u, "_flightDeckState"));

            if (fd == null)
            {
                o["hasFlightDeck"] = false;
                o["supported"] = "not_applicable";
                o["deck"] = JValue.CreateNull();
                o["reason"] = why != null ? why : "no flight deck on this unit";
                return o;
            }

            o["hasFlightDeck"] = true;
            o["supported"] = "read_only";

            JObject d = new JObject();
            d["maxAircraftOnBoard"] = SafeNum(Member(fd, "MaxAircraftOnBoard"));
            d["aircraftCurrentlyOnBoard"] = SafeNum(Member(fd, "AircraftCurrentlyOnBoard"));
            d["aircraftCurrentlyOnBoardField"] = SafeNum(Member(fd, "aircraftCurrentlyOnBoard"));
            d["totalVehiclesOnBoard"] = SafeNum(Invoke0(fd, "TotalVehiclesOnBoard"));
            d["totalVehiclesOnBoardIncludingCooldown"] =
                SafeNum(Invoke0(fd, "TotalVehiclesOnBoardIncludingCooldown"));
            d["totalVehiclesInAir"] = SafeNum(Invoke0(fd, "TotalVehiclesInAir"));
            d["totalVehiclesNotInFlight"] = SafeNum(Invoke0(fd, "TotalVehiclesNotInFlight"));
            d["launchInProgress"] = Truthy(Member(fd, "LaunchInProgress"));
            d["landingInProgress"] = Truthy(Member(fd, "LandingInProgress"));
            d["groundCrewCount"] = SafeNum(Member(fd, "GroundCrewCount"));
            d["assignedGroundCrew"] = SafeNum(Member(fd, "AssignedGroundCrew"));
            d["deckParkSlots"] = SafeNum(Member(fd, "DeckParkSlots"));
            d["assignedDeckParkSlots"] = SafeNum(Member(fd, "AssignedDeckParkSlots"));
            d["capacityFraction"] = SafeNum(Rx(fd, "CapacityFraction"));
            d["ammoCapacity"] = SafeNum(Member(fd, "_ammoCapacity"));
            d["ammoCurrent"] = SafeNum(Member(fd, "_currentAmmo"));
            d["aiDisabled"] = Truthy(Member(fd, "_aiDisabled"));

            JArray onBoard = new JArray();
            List<object> boardList = Seq(Member(fd, "_vehiclesOnBoard"));
            for (int i = 0; i < boardList.Count; i++) onBoard.Add(AircraftTypeJson(boardList[i]));
            d["airgroupOnBoard"] = onBoard;
            d["airgroupOnBoardCount"] = boardList.Count;

            JArray supTypes = new JArray();
            List<object> supList = Seq(Member(fd, "_supportedAirVehicleTypes"));
            for (int i = 0; i < supList.Count; i++) supTypes.Add(AircraftTypeJson(supList[i]));
            d["supportedAircraftTypes"] = supTypes;
            d["supportedAircraftTypeCount"] = supList.Count;

            o["deck"] = d;
            return o;
        }

        // ---- reflection helpers used by the read-only aviation probe (T19.3) ----
        private static readonly Type[] NoArgTypes = new Type[0];

        private static object Invoke0(object target, string method)
        {
            if (target == null) return null;
            try
            {
                MethodInfo m = target.GetType().GetMethod(method, PrivInst, null, NoArgTypes, null);
                if (m == null) return null;
                return m.Invoke(target, null);
            }
            catch (Exception) { return null; }
        }

        private static List<object> Seq(object collection)
        {
            List<object> items = new List<object>();
            System.Collections.IEnumerable e = collection as System.Collections.IEnumerable;
            if (e == null) return items;
            foreach (object item in e) items.Add(item);
            return items;
        }

        // One airgroup entry: SeaPower.VehicleTypeOnBoard. _fileName has no public property,
        // so the private field is read directly. clrType is emitted so an unexpected element
        // shape shows up as such instead of silently producing a row of nulls.
        private static JObject AircraftTypeJson(object vt)
        {
            JObject a = new JObject();
            if (vt == null) { a["clrType"] = JValue.CreateNull(); return a; }
            a["clrType"] = vt.GetType().Name;
            a["fileName"] = SafeStr(Member(vt, "_fileName"));
            a["displayName"] = SafeStr(Member(vt, "DisplayName"));
            a["number"] = SafeNum(Member(vt, "Numbers"));
            a["objectType"] = Names(Member(vt, "_objectType"));
            JArray roles = new JArray();
            List<object> roleList = Seq(Member(vt, "_unitRoles"));
            for (int i = 0; i < roleList.Count; i++) roles.Add(Names(roleList[i]));
            a["unitRoles"] = roles;
            JArray loadouts = new JArray();
            List<object> loList = Seq(Member(vt, "Loadouts"));
            for (int i = 0; i < loList.Count; i++) loadouts.Add(LoadoutJson(loList[i]));
            a["loadouts"] = loadouts;
            return a;
        }

        private static JObject LoadoutJson(object loadout)
        {
            JObject l = new JObject();
            if (loadout == null) { l["name"] = JValue.CreateNull(); return l; }
            l["name"] = SafeStr(Member(loadout, "Name"));
            l["displayName"] = SafeStr(Member(loadout, "DisplayName"));
            l["available"] = Truthy(Member(loadout, "Available"));
            l["combatRadius"] = SafeStr(Member(loadout, "CombatRadiusText"));
            return l;
        }

        // T19.2 -------------------------------------------------------------------
        // Vertical state, read-only. The altitude/depth of everything is driven by one
        // value: ObjectBase.DesiredAltitude. Submarines and aircraft disagree about how
        // that value is chosen, and the game converts it with two scale factors whose
        // physical meaning is NOT established, so the bridge never converts anything
        // itself - it calls the game's own setter and reads back.
        private static JObject Vertical(int id)
        {
            ObjectBase u = FindUnit(id);
            JObject o = new JObject();
            o["unit"] = id;
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            o["clrType"] = u.GetType().FullName;
            o["readPath"] = "ObjectBase.Altitude / DesiredAltitude + type specific parameters";

            o["altitude"] = SafeNum(Rx(u, "Altitude"));
            o["desiredAltitude"] = SafeNum(Rx(u, "DesiredAltitude"));

            bool isSub = u is SeaPower.Submarine;
            bool isAircraft = u is SeaPower.Aircraft;
            bool isHelicopter = u is SeaPower.Helicopter;
            o["isSubmarine"] = isSub;
            o["isAircraft"] = isAircraft;
            o["isHelicopter"] = isHelicopter;

            JArray modes = new JArray();
            if (isSub)
            {
                if (HasMethod(u, "setDepth", new Type[] { typeof(float) })) modes.Add("depth");
                if (HasMethod(u, "setPeriscopeDepth", NoArgTypes)) modes.Add("periscope");
                if (HasMethod(u, "setPresetDepth", new Type[] { typeof(int), typeof(bool) })) modes.Add("preset_depth");
            }
            if (isAircraft || isHelicopter)
            {
                if (HasMethod(u, "setPresetHeight", new Type[] { typeof(int), typeof(bool) })) modes.Add("preset_height");
            }
            // UpdateWaypointsAltitudes only does anything for Submarine and Biologic
            // (the game itself type-gates it), so do not advertise it elsewhere.
            if (isSub && HasMethod(u, "UpdateWaypointsAltitudes", new Type[] { typeof(int) })) modes.Add("push_waypoints");
            o["modes"] = modes;

            if (isSub)
            {
                o["submerged"] = Truthy(Member(u, "IsSubmerged"));
                o["belowPeriscopeDepth"] = Truthy(Member(u, "IsBelowPeriscopeDepth"));
                o["emergencySurface"] = Truthy(Member(u, "IsInEmergencySurfaceState"));
                o["needToSnorkel"] = Truthy(Member(u, "NeedToSnorkel"));
                o["batteryChargePct"] = SafeNum(Rx(u, "BatteryChargePercentage"));
                o["distanceOnBatteriesLeftNm"] = SafeNum(Member(u, "DistanceOnBatteriesLeftNM"));
                o["maxCurrentDistanceOnBatteriesNm"] = SafeNum(Member(u, "MaxCurrentDistanceOnBatteriesNM"));
                o["periscopeMastOut"] = Truthy(Member(u, "_isPeriscopeMastOut"));
                o["snorkelMastOut"] = Truthy(Member(u, "_isSnorkelMastOut"));
                o["radarMastOut"] = Truthy(Member(u, "_isRadarMastOut"));
                o["esmMastOut"] = Truthy(Member(u, "_isESMMastOut"));
                o["currentPresetDepth"] = SafeNum(Member(u, "_currentPresetDepth"));

                object sp = Member(u, "SP");
                if (sp == null) sp = Member(u, "_sp");
                o["paramsPath"] = sp != null ? "Submarine.SP" : null;
                o["crushDepth"] = SafeNum(Member(sp, "_crushDepth"));
                o["periscopeDepthParam"] = SafeNum(Member(sp, "_periscopeDepth"));

                // The game's own preset table; never re-derived here.
                JArray presets = new JArray();
                for (int i = 0; i <= 6; i++)
                {
                    object feet = InvokeN(u, "GetPresetDepthFeet", new Type[] { typeof(int) }, new object[] { i });
                    JObject pr = new JObject();
                    pr["preset"] = i;
                    pr["feet"] = SafeNum(feet);
                    presets.Add(pr);
                }
                o["presetDepthsFeet"] = presets;
            }

            if (isAircraft || isHelicopter)
            {
                object ap = Member(u, "Ap");
                if (ap == null) ap = Member(u, "AP");
                if (ap == null) ap = Member(u, "_ap");
                o["paramsPath"] = ap != null ? "Aircraft.Ap" : null;
                float[] alts = Member(ap, "_presetAltitudes") as float[];
                if (alts != null)
                {
                    JArray presets = new JArray();
                    for (int i = 0; i < alts.Length; i++)
                    {
                        JObject pr = new JObject();
                        pr["preset"] = i;
                        pr["raw"] = SafeNum(alts[i]);
                        presets.Add(pr);
                    }
                    o["presetAltitudesRaw"] = presets;
                    o["presetAltitudeUnitNote"] = "raw _presetAltitudes entries exactly as stored; the game applies its own scale factor when it reports feet, and that conversion is NOT established here so nothing is converted";
                }
                else
                {
                    o["presetAltitudesRaw"] = JValue.CreateNull();
                }
            }

            o["waypointHeightStates"] = JoinNames(HeightStateNames());
            o["heightStateNote"] = "pass height_state on move/course; omitted keeps the old behaviour (NoChange)";
            return o;
        }

        // ---- T19.4: submarine systems, read-only ----------------------------
        // This deliberately does NOT re-implement the vertical command surface: immediate
        // depth, periscope and preset commands live on POST /units/{id}/vertical (T19.2).
        // Mast writes and wire-guided torpedo control are out of scope by the task card,
        // so this probe only reports them and says so explicitly in "capabilities".
        private static JObject Sub(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null)
                throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);

            JObject o = new JObject();
            o["unit"] = id;
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            o["clrType"] = u.GetType().Name;

            SeaPower.Submarine sub = u as SeaPower.Submarine;
            if (sub == null)
            {
                o["isSubmarine"] = false;
                o["supported"] = "not_applicable";
                o["sub"] = JValue.CreateNull();
                o["reason"] = "unit " + id + " is " + u.GetType().Name + ", not a Submarine";
                return o;
            }

            o["isSubmarine"] = true;
            o["supported"] = "read_only";

            JObject s = new JObject();
            s["altitude"] = SafeNum(Rx(sub, "Altitude"));
            s["desiredAltitude"] = SafeNum(Rx(sub, "DesiredAltitude"));
            s["verticalCoordinateNote"] = "Altitude is metres and negative when deeper. DesiredAltitude is negative when deeper too, but its SCALE depends on who wrote it: mode=depth stores -metres (measured 20..110 m exact), preset_depth stores -(feet * <preset scale factor>) (measured 100 ft -> -0.454), so the two are not comparable. See docs\\VERTICAL_DEPTH_UNITS.md";
        s["desiredAltitudeMixedScaleWarning"] = "do not compare DesiredAltitude against altitude, and do not compare values written by different vertical modes; only the response of the call that wrote it knows the unit";
        s["depthAdvice"] = "measured on 091G (2026-11-01): mode=depth kept 20..110 m exactly, and every order of 120 m or more was replaced by one fixed deep value that is past the hull's crush depth, after which the boat kept sinking and depth orders stopped working. Keep depth orders well under whichever preset the hull offers, prefer preset_depth, and never send a depth order you have not first read back from this probe.";

            // --- dive state
            s["isSubmerged"] = Truthy(Rx(sub, "IsSubmerged"));
            s["isBelowPeriscopeDepth"] = Truthy(Rx(sub, "IsBelowPeriscopeDepth"));
            s["isInEmergencySurfaceState"] = Truthy(Rx(sub, "IsInEmergencySurfaceState"));
            s["isFullySurfaced"] = Truthy(Member(sub, "IsFullySurfaced"));
            s["wasEmergencySurfacingInitiated"] = Truthy(Member(sub, "WasEmergencySurfacingInitiated"));
            s["isFasterThanSnorkelMaxSpeed"] = Truthy(Rx(sub, "IsFasterThanSnorkelMaxSpeed"));
            s["needToSnorkel"] = Truthy(Member(sub, "NeedToSnorkel"));
            s["lowBatterySpeedOverride"] = Truthy(Member(sub, "LowBatterySpeedOverride"));
            s["divingInProgress"] = Truthy(Member(sub, "_divingInProgress"));
            s["setToDive"] = Truthy(Member(sub, "_setToDive"));
            s["currentPresetDepth"] = SafeNum(Member(sub, "_currentPresetDepth"));
            s["telegraph"] = SafeNum(Invoke0(sub, "getTelegraph"));
            object sm = Member(sub, "StateMachine");
            s["stateMachineState"] = sm != null ? SafeStr(Member(sm, "CurrentStateName")) : null;

            // --- speed envelope; this is where the submerged-knots semantics become checkable
            s["maxForwardSpeedInKnots"] = SafeNum(Member(sub, "MaxForwardSpeedInKnots"));
            s["maxBackwardSpeedInKnots"] = SafeNum(Member(sub, "MaxBackwardSpeedInKnots"));
            s["maxForwardSpeedSubmergedInKnots"] = SafeNum(Member(sub, "MaxForwardSpeedSubmergedInKnots"));
            s["maxBackwardSpeedSubmergedInKnots"] = SafeNum(Member(sub, "MaxBackwardSpeedSubmergedInKnots"));

            // --- battery
            s["batteryChargePercentage"] = SafeNum(Rx(sub, "BatteryChargePercentage"));
            s["distanceOnBatteriesLeftNM"] = SafeNum(Member(sub, "DistanceOnBatteriesLeftNM"));
            s["maxCurrentDistanceOnBatteriesNM"] = SafeNum(Member(sub, "MaxCurrentDistanceOnBatteriesNM"));
            s["batteryCapacityExpenditure"] = SafeNum(Member(sub, "BatteryCapacityExpenditure"));
            s["batteryCapacityRecharge"] = SafeNum(Member(sub, "BatteryCapacityRecharge"));
            s["batteryCapacityRaw"] = SafeNum(Member(sub, "_batteryCapacity"));
            object bs = Member(sub, "BatterySystem");
            s["batterySystemType"] = bs != null ? bs.GetType().Name : null;
            object us = Member(sub, "UnderwaterSystem");
            s["underwaterSystemType"] = us != null ? us.GetType().Name : null;
            s["batteryNote"] = "batteryChargePercentage comes from the game's own ReactiveProperty; raw capacity figures are reported unconverted";

            // --- masts: READ ONLY. T19.4 forbids extending mast writes.
            s["periscopeMastOut"] = Truthy(Invoke0(sub, "isPeriscopeMastOut"));
            s["snorkelMastOut"] = Truthy(Invoke0(sub, "isSnorkelMastOut"));
            s["radarMastOut"] = Truthy(Invoke0(sub, "isRadarMastOut"));
            s["esmMastOut"] = Truthy(Invoke0(sub, "isESMMastOut"));
            s["raisedMastsCount"] = SafeNum(Invoke0(sub, "getRaisedMastsCount"));

            // --- acoustics
            s["noise"] = SafeNum(Invoke0(sub, "getNoise"));
            s["cavitationOnsetKts"] = SafeNum(Invoke0(sub, "CavitationOnsetKts"));

            // --- hull parameters, reported raw on purpose
            object sp = Member(sub, "SP");
            s["paramsPath"] = sp != null ? "Submarine.SP" : null;
            s["crushDepthRaw"] = SafeNum(Member(sp, "_crushDepth"));
            s["periscopeDepthRaw"] = SafeNum(Member(sp, "_periscopeDepth"));
            s["hullParamUnitNote"] = "crushDepth/periscopeDepth are shown exactly as stored. A metre conversion was attempted and did not close, so nothing is converted and no range guard is derived from them";

            // --- wire-guided torpedo: reported only, never commanded
            s["autoWireControlEnabled"] = Truthy(Member(sub, "AutoWireControlEnabled"));
            object wire = Member(sub, "_torpedoWireController");
            s["torpedoWireControllerType"] = wire != null ? wire.GetType().Name : null;

            s["presetDepthNote"] = "the per-preset depth table (GetPresetDepthFeet 0..6) and immediate depth commands live on GET/POST /units/{id}/vertical - not duplicated here";

            JObject cap = new JObject();
            cap["depth"] = "POST /units/{id}/vertical {\"mode\":\"depth\",\"meters\":N}";
            cap["periscope"] = "POST /units/{id}/vertical {\"mode\":\"periscope\"}";
            cap["presetDepth"] = "POST /units/{id}/vertical {\"mode\":\"preset_depth\",\"preset\":0..6}";
            cap["pushWaypoints"] = "POST /units/{id}/vertical {\"mode\":\"push_waypoints\",\"index\":0..7}";
            cap["mastWriteSupported"] = false;
            cap["mastWriteReason"] = "T19.4 keeps masts read-only; raising/retracting them stays a player action";
            cap["wireTorpedoSupported"] = false;
            cap["wireTorpedoReason"] = "T19.4 forbids extending wire-guided torpedo control";
            cap["equivalentDepthOpSupported"] = false;
            cap["equivalentDepthOpReason"] = "depth/periscope/preset already exist on /vertical (T19.2); a second op would duplicate them";
            o["capabilities"] = cap;

            o["sub"] = s;
            return o;
        }

        // ---------------------------------------------------------------------------------
        // T19.5 aviation. Two IL facts drive this whole block:
        //
        //   1. LaunchTaskParameters is all private fields, so asking for a SPECIFIC type means
        //      building one and calling the 5-argument overload
        //      FlightDeckLaunchUnit(string fileName, ltp, loadoutName, requireExactLoadout,
        //      squadronName). The old code called the 7-argument overload, which selects by
        //      ObjectType + UnitRoles and therefore launches whatever the game picks first.
        //   2. FlightDeck.launchVehicle adds the spawned aircraft to _baseObject._childAircraft.
        //      That list is the game's own "what did this deck put in the air", so reading it is
        //      the authoritative association rather than an inference.
        // ---------------------------------------------------------------------------------

        private static object DeckObject(ObjectBase u)
        {
            try
            {
                object obp = Member(u, "_obp");
                if (obp == null) return null;
                return Member(obp, "_flightDeck");
            }
            catch (Exception) { return null; }
        }

        private static System.Collections.IEnumerable ChildAircraft(ObjectBase u)
        {
            try { return Member(u, "_childAircraft") as System.Collections.IEnumerable; }
            catch (Exception) { return null; }
        }

        private static int CountAirborne(ObjectBase u)
        {
            System.Collections.IEnumerable list = ChildAircraft(u);
            if (list == null) return -1;
            int n = 0;
            foreach (object a in list) { if (a != null) n++; }
            return n;
        }

        private static bool IsOurAircraft(ObjectBase host, ObjectBase candidate)
        {
            if (host == null || candidate == null) return false;
            System.Collections.IEnumerable list = ChildAircraft(host);
            if (list == null) return false;
            foreach (object a in list)
            {
                if (ReferenceEquals(a, candidate)) return true;
            }
            return false;
        }

        internal static string[] MissionTypeNames()
        {
            return Enum.GetNames(typeof(SeaPower.FlightDeckTask.MissionType));
        }

        private static int FindMissionType(string name)
        {
            if (name == null) return 0;
            string[] names = MissionTypeNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (String.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static SeaPower.FlightDeckTask.MissionType ParseMissionType(string name)
        {
            if (name == null) return SeaPower.FlightDeckTask.MissionType.None;
            int idx = FindMissionType(name);
            if (idx < 0) return SeaPower.FlightDeckTask.MissionType.None;
            return (SeaPower.FlightDeckTask.MissionType)Enum.Parse(
                typeof(SeaPower.FlightDeckTask.MissionType), MissionTypeNames()[idx], true);
        }

        private static bool IsGuid(string s)
        {
            try { new Guid(s); return true; }
            catch (Exception) { return false; }
        }

        private static JObject AircraftJson(ObjectBase a)
        {
            JObject e = new JObject();
            if (a == null) return e;
            e["objectId"] = SafeNum(Member(a, "UniqueID"));
            e["name"] = SafeStr(Rx(a, "DisplayNameFull"));
            e["className"] = SafeStr(Rx(a, "ClassName"));
            e["clrType"] = a.GetType().Name;
            e["isAircraft"] = a is SeaPower.Aircraft;
            e["isHelicopter"] = a is SeaPower.Helicopter;
            e["destroyed"] = SafeFlag(a, "destroyed");
            e["altitude"] = SafeNum(Rx(a, "Altitude"));
            e["desiredAltitude"] = SafeNum(Rx(a, "DesiredAltitude"));
            e["speedKts"] = SafeNum(Rx(a, "Velocity"));
            e["heading"] = SafeNum(Rx(a, "Heading"));
            object posObj = Rx(a, "Position");
            if (posObj is GeoPosition)
            {
                GeoPosition gp = (GeoPosition)posObj;
                e["lat"] = Math.Round(gp.Latitude, 6);
                e["lon"] = Math.Round(gp.Longitude, 6);
            }
            return e;
        }

        private static JObject Airborne(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null) throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);

            JObject o = new JObject();
            o["unit"] = id;
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            o["source"] = "ObjectBase._childAircraft";

            object deck = DeckObject(u);
            o["hasFlightDeck"] = deck != null;

            System.Collections.IEnumerable list = ChildAircraft(u);
            JArray arr = new JArray();
            int n = 0;
            if (list != null)
            {
                foreach (object a in list)
                {
                    if (a == null) continue;
                    arr.Add(AircraftJson(a as ObjectBase));
                    n++;
                }
            }
            o["count"] = n;
            o["aircraft"] = arr;
            if (deck == null)
                o["reason"] = "_obp._flightDeck is null - this unit cannot hold aircraft";
            else if (list == null)
                o["reason"] = "_childAircraft is null on this unit type";
            return o;
        }

        /// <summary>
        /// True when the given deck task names this vehicle.
        /// </summary>
        private static bool NamesVehicle(JObject task, int vehicleId)
        {
            if (task == null) return false;
            JToken v = task["vehicleId"];
            if (v == null || v.Type == JTokenType.Null) return false;
            try { return Convert.ToInt32(v.ToString()) == vehicleId; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The uids of every deck task that names this vehicle. Comparing two snapshots of
        /// this set is how a call is proved to have queued something new - mere presence can
        /// be a pre-existing task, which is exactly the trap the presence check fell into.
        /// </summary>
        private static System.Collections.Generic.HashSet<string> TaskUidsForVehicle(JObject deckTasks, int vehicleId)
        {
            System.Collections.Generic.HashSet<string> uids =
                new System.Collections.Generic.HashSet<string>();
            if (deckTasks == null) return uids;
            JArray tasks = deckTasks["tasks"] as JArray;
            if (tasks == null) return uids;
            for (int i = 0; i < tasks.Count; i++)
            {
                JObject task = tasks[i] as JObject;
                if (!NamesVehicle(task, vehicleId)) continue;
                string uid = SafeStr(task["uid"]);
                if (uid != null) uids.Add(uid);
            }
            return uids;
        }

        /// <summary>
        /// Deck tasks that already bring this aircraft home: ReturnTask (on its way) and
        /// RecoveryTask (in the landing pattern). `recover` looks at this before asking the
        /// game for anything, because asking twice queues two competing tasks (B28).
        /// </summary>
        private static System.Collections.Generic.List<JObject> HomeTasksForVehicle(JObject deckTasks, int vehicleId)
        {
            System.Collections.Generic.List<JObject> found =
                new System.Collections.Generic.List<JObject>();
            if (deckTasks == null) return found;
            JArray tasks = deckTasks["tasks"] as JArray;
            if (tasks == null) return found;
            for (int i = 0; i < tasks.Count; i++)
            {
                JObject task = tasks[i] as JObject;
                if (!NamesVehicle(task, vehicleId)) continue;
                JToken c = task["clrType"];
                string type = c == null || c.Type == JTokenType.Null ? null : c.ToString();
                if (type == "ReturnTask" || type == "RecoveryTask") found.Add(task);
            }
            return found;
        }

        private static JArray TaskSummaries(System.Collections.Generic.List<JObject> tasks)
        {
            JArray arr = new JArray();
            for (int i = 0; i < tasks.Count; i++)
            {
                JObject t = tasks[i];
                JObject e = new JObject();
                e["type"] = SafeStr(t["clrType"]);
                e["uid"] = SafeStr(t["uid"]);
                e["info"] = SafeStr(t["info"]);
                arr.Add(e);
            }
            return arr;
        }

        private static int UnitIdOf(ObjectBase u)
        {
            if (u == null) return 0;
            try { return Convert.ToInt32(Member(u, "UniqueID")); }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// The first task in <paramref name="after"/> that names this vehicle and whose uid was
        /// not present in <paramref name="before"/>, or null when the call queued nothing new.
        /// </summary>
        private static JObject NewTaskForVehicle(JObject before, JObject after, int vehicleId)
        {
            System.Collections.Generic.HashSet<string> seen = TaskUidsForVehicle(before, vehicleId);
            JArray tasks = after == null ? null : after["tasks"] as JArray;
            if (tasks == null) return null;
            for (int i = 0; i < tasks.Count; i++)
            {
                JObject task = tasks[i] as JObject;
                if (!NamesVehicle(task, vehicleId)) continue;
                string uid = SafeStr(task["uid"]);
                if (uid != null && !seen.Contains(uid)) return task;
            }
            return null;
        }

        private static JObject DeckTasks(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null) throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);

            JObject o = new JObject();
            o["unit"] = id;
            o["name"] = SafeStr(Rx(u, "DisplayNameFull"));
            object deck = DeckObject(u);
            o["hasFlightDeck"] = deck != null;

            JArray arr = new JArray();
            int n = 0;
            if (deck != null)
            {
                System.Collections.IEnumerable tasks = null;
                try { tasks = Member(deck, "FlightDeckTasks") as System.Collections.IEnumerable; }
                catch (Exception) { }
                if (tasks != null)
                {
                    foreach (object t in tasks)
                    {
                        if (t == null) continue;
                        JObject e = new JObject();
                        // Guid _uid is exactly what abortLaunchTask(Guid, bool) takes, so it is
                        // the only handle that can cancel ONE task and leave the rest alone.
                        e["uid"] = SafeStr(Member(t, "_uid"));
                        e["clrType"] = t.GetType().Name;
                        e["label"] = SafeStr(Rx(t, "FlightDeckTaskLabel"));
                        e["aircraftType"] = SafeStr(Rx(t, "AircraftType"));
                        e["squadron"] = SafeStr(Rx(t, "Squadron"));
                        e["info"] = SafeStr(Rx(t, "Info"));
                        e["cancelled"] = Truthy(Member(t, "_isCancelled"));
                        e["isRTB"] = Truthy(Member(t, "_isRTB"));
                        e["isBolter"] = Truthy(Member(t, "_isBolter"));
                        e["startTime"] = SafeNum(Member(t, "_startTime"));
                        e["duration"] = SafeNum(Member(t, "_duration"));
                        object veh = Member(t, "Vehicle");
                        if (veh is ObjectBase)
                        {
                            ObjectBase v = (ObjectBase)veh;
                            e["vehicleId"] = SafeNum(Member(v, "UniqueID"));
                            e["vehicleName"] = SafeStr(Rx(v, "DisplayNameFull"));
                        }
                        arr.Add(e);
                        n++;
                    }
                }
            }
            // Diagnostic: the queue holds both LaunchTask and ReturnTask entries and they behave
            // differently under abort, so the mix matters more than the total.
            JObject counts = new JObject();
            for (int ci = 0; ci < arr.Count; ci++)
            {
                string ct = arr[ci]["clrType"] == null ? "null" : arr[ci]["clrType"].ToString();
                JToken cur = counts[ct];
                counts[ct] = (cur == null ? 0 : (int)cur) + 1;
            }
            o["countsByType"] = counts;
            o["count"] = n;
            o["tasks"] = arr;
            return o;
        }

        /// <summary>clrType of the deck task with this uid, or null. Used to tell a named
        /// LaunchTask (abortable in principle) apart from a ReturnTask (not a launch).</summary>
        private static string TaskTypeOf(JObject deckTasks, string uid)
        {
            if (deckTasks == null || uid == null) return null;
            JArray list = deckTasks["tasks"] as JArray;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                JObject t = list[i] as JObject;
                if (t == null) continue;
                JToken u = t["uid"];
                if (u != null && String.Equals(u.ToString(), uid, StringComparison.OrdinalIgnoreCase))
                {
                    JToken c = t["clrType"];
                    return c == null ? null : c.ToString();
                }
            }
            return null;
        }

        private static bool HasMethod(object target, string name, Type[] argTypes)
        {
            return FindInstanceMethod(target, name, argTypes) != null;
        }

        private static System.Reflection.MethodInfo FindInstanceMethod(object target, string name, Type[] argTypes)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, argTypes, null);
            }
            catch (Exception) { return null; }
        }

        private static object InvokeN(object target, string name, Type[] argTypes, object[] args)
        {
            System.Reflection.MethodInfo mi = FindInstanceMethod(target, name, argTypes);
            if (mi == null) return null;
            try { return mi.Invoke(target, args); }
            catch (Exception) { return null; }
        }

        // GET /tracks : contacts visible on the PLAYER's plotting tables.
        // Vehicle.Id is the number the game prints next to a contact, so this is the
        // bridge between "track 1006" in the UI and an ObjectBase we can shoot at.
        //
        // P-31: this used to walk EVERY unit's own plotting table and merge the results, so
        // an enemy submarine's sensor picture landed in our contact list - objects the
        // player's own plot never showed. The walk is now restricted to the player's tables,
        // which is what the tactical UI displays. ?scope=all restores the old aggregate and
        // is DIAGNOSTICS ONLY; it is not a product capability and not an acceptance path.
        private static JObject Tracks(string query)
        {
            string scope = FilterValue(query, "scope");
            bool allTables = scope == "all";
            // 2: view=contact swaps the raw plotting-table row for the projection the player can
            // actually see (PositionEstimate / bearing fan / confidence-as-is). Opt-in on purpose:
            // the default shape is what every existing test and caller already reads.
            string view = FilterValue(query, "view");
            if (!string.IsNullOrEmpty(view) && view != "contact")
                throw new ApiException(400, "bad_value",
                    "view " + view + " is not one we support; use contact (player observation) or omit", false);
            bool contactView = view == "contact";
            // V2 DECISION (2026-11-05, owner): same as ?all=1 above - no intel isolation.
            if (false && allTables && !MiniHttp.DiagnosticsEnabled)
                throw new ApiException(403, "scope_denied",
                    "/tracks?scope=all aggregates every side's plotting table, so it exposes contacts the player's own plot never showed. It is diagnostics-only (create diagnostics.on in the mod folder). Omit scope to read the player picture.", false);

            List<object> items = new List<object>();
            HashSet<int> seen = new HashSet<int>();

            foreach (ObjectBase o in AllObjects())
            {
                if (o == null) continue;
                if (!o.isUnit()) continue;
                if (!allTables && !SafeFlag(o, "mine")) continue;

                PlottingTable pt = null;
                try { pt = o.PlottingTable; } catch (Exception) { }
                if (pt == null) continue;

                // Vehicles holds the contacts this table can see; LocalVehicles also carries
                // the own-side rows, so both are merged here.
                System.Collections.IEnumerable vs = null;
                try { vs = pt.Vehicles; } catch (Exception) { }
                System.Collections.IDictionary local = null;
                try { local = Member(pt, "LocalVehicles") as System.Collections.IDictionary; } catch (Exception) { }
                if (vs == null && local == null) continue;

                if (vs != null)
                {
                    foreach (object raw in vs)
                        AddTrack(items, seen, raw as Vehicle, o);
                }
                if (local != null)
                {
                    foreach (System.Collections.DictionaryEntry e in local)
                        AddTrack(items, seen, e.Value as Vehicle, o);
                }
            }

            JObject res = new JObject();
            res["scope"] = allTables ? "all" : "player";
            res["count"] = items.Count;
            JArray trackList = JArray.FromObject(items);
            // TR2-I / ISS-004: a contact is not an identification. Until the game itself says the
            // contact is identified and classified, objectId/class/type must not travel: objectId
            // is the target's UniqueID, and feeding it to /units/{id} turned "unknown contact"
            // into "that exact hull" in one step.
            // ContactView never reads ObjectBase.Position; a track it cannot find keeps
            // positionSource:"unknown" rather than being filled in with the real position.
            // O4: every face now says WHERE a contact is - or that it does not know. The numbers
            // come from Vehicle.PositionEstimate (what the player has plotted), never from
            // ObjectBase.Position, and they are nullable so "bearing only" is expressible at all.
            // Before this a track row carried no position keys, so "unknown" and "not reported"
            // were the same shape to a caller.
            ContactView.Decorate(trackList);
            JObject trackAudit = TrackScope.Apply(trackList);
            res["tracks"] = trackList;
            res["intel"] = trackAudit;
            res["revealSwitch"] = IntelRevealEnabled();
            res["positionFace"] = "estimate";
            if (contactView) res["view"] = "contact";
            return res;
        }

        // Weapons control (Tight / Free / Hold). Whole file evidence: SeaPower.ObjectBase/WeaponStatus
        // has exactly three members (Tight, Free, Hold) and SetWeaponStatus(WeaponStatus, Boolean)
        // is the only writer of _weaponStatus. There is no read-back route yet, so this command
        // reports its own readback instead of claiming success.
        private static JObject WeaponStatusCommand(int id, JObject req)
        {
            ObjectBase u = FindUnit(id);
            string want = ReqStr(req, "status", null);
            string wanted = want == null ? "" : want.Trim().ToLowerInvariant();
            if (wanted != "tight" && wanted != "free" && wanted != "hold")
                throw new ApiException(400, "bad_value",
                    "status must be tight, free or hold (got " + want + ")", false);
            bool formation = ReqBool(req, "formation", false);
            bool validateOnly = ReqBool(req, "validate_only", false);

            Type enumType = GameReflect.FindType("SeaPower.ObjectBase+WeaponStatus");
            if (enumType == null)
                throw new ApiException(500, "no_weapon_status_type",
                    "SeaPower.ObjectBase+WeaponStatus was not found in the loaded assemblies", false);
            string canonical = wanted == "tight" ? "Tight" : (wanted == "free" ? "Free" : "Hold");
            object value = null;
            try { value = Enum.Parse(enumType, canonical, true); }
            catch (Exception)
            {
                throw new ApiException(500, "bad_weapon_status_enum",
                    "the game enum has no member " + canonical, false);
            }

            string before = WeaponStatusName(u);
            JObject o = new JObject();
            o["unit"] = id;
            o["statusRequested"] = wanted;
            o["statusBefore"] = before == null ? JValue.CreateNull() : (JToken)new JValue(before);
            o["formation"] = formation;
            o["validateOnly"] = validateOnly;
            // READ-SIDE CONSISTENCY: when formationCheck is set the game writes the status onto every
            // Station and leaves this hull's own field alone (IL: SetWeaponStatus -> foreach
            // Station.UnitObject -> stfld _weaponStatus). Reporting only the hull field would make that
            // branch look like a silent no-op, so say whether this hull is in a formation at all.
            object follows = GameReflect.Member(u, "FollowingFormation");
            o["followingFormation"] = follows == null ? JValue.CreateNull() : (JToken)new JValue(Convert.ToBoolean(follows));
            if (validateOnly)
            {
                o["executed"] = false;
                o["validated"] = true;
                o["note"] = "validate_only: nothing was written; without a policy change the real call would set _weaponStatus";
                return o;
            }

            GameReflect.Call(u, "SetWeaponStatus", value, formation);
            string after = WeaponStatusName(u);
            o["executed"] = true;
            o["statusAfter"] = after == null ? JValue.CreateNull() : (JToken)new JValue(after);
            bool own = after != null && string.Equals(after, canonical, StringComparison.OrdinalIgnoreCase);
            o["verified"] = own;
            if (!own)
                o["verifyNote"] = formation
                    ? "the hull follows a formation, so the game copied the status onto every Station instead of its own field; read a station to confirm"
                    : "the game accepted the call but its own field still reads a different value";
            return o;
        }

        private static string WeaponStatusName(ObjectBase u)
        {
            try
            {
                object v = GameReflect.Member(u, "_weaponStatus");
                return v == null ? null : v.ToString();
            }
            catch (Exception) { return null; }
        }

        private static void AddTrack(List<object> items, HashSet<int> seen, Vehicle v, ObjectBase from)
        {
            if (v == null) return;

            int id = 0;
            try { id = v.Id; } catch (Exception) { }
            if (id <= 0) return;
            if (!seen.Add(id)) return;

            JObject t = new JObject();
            t["trackId"] = id;

            object tf = Member(v, "ReportingSide");
            // ReportingSide is the plotting table's owner, NOT the contact's allegiance.
            // Vehicle.Side carries the target's own side, but it is usually wrapped in a
            // SourcedProperty-like holder whose ToString is just the type name, so unwrap
            // until we hit a real Taskforce and read its TfType out.
            string ownSide = NameField(Member(v, "Side"));
            // Falling back to the plotting table's side advertised every still-unidentified
            // contact as friendly; a submarine came back as "Player". Say Unknown instead
            // and leave the plotting table's owner in reportedBy.
            t["side"] = ownSide != null ? ownSide : "Unknown";
            t["reportedBy"] = TaskforceName(tf) != null ? TaskforceName(tf) : SideName(tf);

            t["class"] = NameField(Member(v, "Class"));
            t["identified"] = Truthy(Member(v, "Identified"));
            t["type"] = Names(Member(v, "Type"));
            t["fresh"] = Truthy(Member(v, "IsDetectedTrackFresh"));
            t["live"] = Truthy(Member(v, "HasLiveTrack"));
            t["visual"] = Truthy(Member(v, "IsObservedByVisual"));
            t["classified"] = Truthy(Member(v, "IsClassified"));
            t["tableFrom"] = from.UniqueID;

            ObjectBase det = Member(v, "Object") as ObjectBase;
            if (det != null) t["objectId"] = det.UniqueID;
            items.Add(t);
        }

        // Pick a weapon that can actually reach the target at rangeNm.
        // The game's own GetDefaultAmmunitionForEngage returns null on some hulls (verified
        // on 051G3, which carries ASCMs), and InsertEngageTask then silently does nothing,
        // so choose by hand: skip countermeasures, require the target to sit inside the
        // min/max launch envelope, and prefer the longest-reaching weapon available.
        private static string PickAmmo(ObjectBase u, double rangeNm)
        {
            string best = null;
            double bestRange = -1.0;
            try
            {
                UniRx.ReactiveDictionary<string, int> inv = u.AmmunitionAmountDictionary;
                foreach (KeyValuePair<string, int> kv in inv)
                {
                    if (kv.Value <= 0) continue;
                    string id = kv.Key;
                    if (id == null) continue;
                    string low = id.ToLower();
                    if (low.IndexOf("chaff") >= 0 || low.IndexOf("noisemaker") >= 0 ||
                        low.IndexOf("decoy") >= 0 || low.IndexOf("flare") >= 0) continue;
                    Ammunition am = u.getAmmunitionByName(id);
                    if (am == null) continue;
                    object ap = Member(am, "_ap");
                    if (ap == null) continue;
                    object mnObj = Member(ap, "_minLaunchRangeInMiles");
                    object mxObj = Member(ap, "_maxLaunchRangeInMiles");
                    double mn = mnObj == null ? 0.0 : Convert.ToDouble(mnObj);
                    double mx = mxObj == null ? 0.0 : Convert.ToDouble(mxObj);
                    if (mx <= 0.0) continue;
                    if (rangeNm >= 0.0 && (rangeNm < mn || rangeNm > mx)) continue;
                    if (mx > bestRange)
                    {
                        bestRange = mx;
                        best = id;
                    }
                }
            }
            catch (Exception) { }
            return best;
        }

        // Resolve a field that may hold a plain string / enum, or a wrapper object such as
        // SourcedProperty<T> whose ToString is only the type name. Wrappers are useless to
        // an agent, so unwrap one level and, for Taskforce holders, surface the TfType.
        internal static string NameField(object v)
        {
            if (v == null) return null;
            string s = Names(v);
            if (s == null) return null;
            if (s.StartsWith("SeaPower."))
            {
                object raw = Member(v, "Value");
                if (raw == null) raw = v;
                string inner = Names(raw);
                if (inner != null) s = inner;
                if (s.StartsWith("SeaPower."))
                {
                    try
                    {
                        Type rt = raw.GetType();
                        if (rt.FullName != null && rt.FullName.IndexOf("Taskforce") >= 0)
                        {
                            string side = SideName(raw);
                            if (side != null) s = side;
                        }
                    }
                    catch (Exception) { }
                }
            }
            return s;
        }

        // Taskforce exposes TfType Side plus the raw mission-file name.
        internal static string SideName(object taskforce)
        {
            if (taskforce == null) return null;
            try { return Names(Member(taskforce, "Side")); }
            catch (Exception) { return null; }
        }

        internal static string TaskforceName(object taskforce)
        {
            if (taskforce == null) return null;
            try
            {
                string s = Names(Member(taskforce, "_nameInMissionFile"));
                if (s != null && s.Length > 0) return s;
                return Names(Member(taskforce, "Name"));
            }
            catch (Exception) { return null; }
        }

        // Unwrap ReactiveProperty<T> / Nullable<T> and stringify for JSON.
        internal static string Names(object v)
        {
            if (v == null) return null;
            object inner = Member(v, "Value");
            if (inner != null) v = inner;
            return Convert.ToString(v);
        }

        internal static bool Truthy(object v)
        {
            if (v == null) return false;
            object inner = Member(v, "Value");
            if (inner != null) v = inner;
            try { return Convert.ToBoolean(v); }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- probes
        // Read-only introspection: answers "what is actually in here?" at runtime so a
        // new DLL is not needed for every question. Nothing here mutates game state.

        private static JObject ProbeUi()
        {
            JObject o = new JObject();
            JArray views = new JArray();
            o["views"] = views;

            Type nvType = FindType("NoesisView");
            o["noesisViewType"] = nvType == null ? null : nvType.FullName;
            if (nvType == null) return o;

            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(nvType);
            o["count"] = objs.Length;
            for (int i = 0; i < objs.Length && i < 8; i++)
            {
                JObject v = new JObject();
                Component c = objs[i] as Component;
                if (c != null)
                {
                    v["gameObject"] = c.gameObject.name;
                    v["activeInHierarchy"] = c.gameObject.activeInHierarchy;
                    try { v["hierarchy"] = GameObjectPath(c.gameObject.transform); }
                    catch (Exception) { }
                }
                object content = Member(objs[i], "Content");
                if (content == null) { v["contentType"] = null; }
                else
                {
                    v["contentType"] = content.GetType().FullName;
                    v["tree"] = UiTree(content, 0, 3);
                }
                views.Add(v);
            }
            return o;
        }

        private static JArray UiTree(object elem, int depth, int maxDepth)
        {
            JArray arr = new JArray();
            if (elem == null || depth > maxDepth) return arr;
            try
            {
                List<object> kids = UiChildren(elem);
                for (int i = 0; i < kids.Count; i++)
                {
                    object child = kids[i];
                    if (child == null) continue;
                    JObject node = new JObject();
                    node["type"] = child.GetType().Name;
                    string nm = Names(Member(child, "Name"));
                    if (nm != null && nm.Length > 0) node["name"] = nm;
                    try
                    {
                        if (depth < maxDepth) node["children"] = UiTree(child, depth + 1, maxDepth);
                    }
                    catch (Exception) { }
                    arr.Add(node);
                }
            }
            catch (Exception) { }
            return arr;
        }

        /** Noesis has three ways to reach a child element; FrameworkElement itself has no Children. */
        private static List<object> UiChildren(object elem)
        {
            List<object> kids = new List<object>();
            if (elem == null) return kids;

            // 1) Panel.Children / ItemsControl (UIElementCollection is usually IList)
            object children = Member(elem, "Children");
            if (children != null)
            {
                System.Collections.IList list = children as System.Collections.IList;
                if (list != null)
                {
                    for (int i = 0; i < list.Count && i < 40; i++) kids.Add(list[i]);
                }
                else
                {
                    object cntObj = Member(children, "Count");
                    int cnt = 0;
                    try { if (cntObj != null) cnt = Convert.ToInt32(cntObj); } catch (Exception) { }
                    PropertyInfo ip = null;
                    try
                    {
                        ip = children.GetType().GetProperty("Item", PrivInst, null, null,
                            new Type[] { typeof(int) }, null);
                    }
                    catch (Exception) { }
                    if (ip != null)
                    {
                        for (int i = 0; i < cnt && i < 40; i++)
                        {
                            object ch = null;
                            try { ch = ip.GetValue(children, new object[] { i }); } catch (Exception) { }
                            if (ch != null) kids.Add(ch);
                        }
                    }
                }
            }

            // 2) generic visual tree: Noesis.Visual.VisualChildrenCount + GetVisualChild(int)
            if (kids.Count == 0)
            {
                object vcObj = null;
                try { vcObj = Member(elem, "VisualChildrenCount"); } catch (Exception) { }
                int vc = 0;
                try { if (vcObj != null) vc = Convert.ToInt32(vcObj); } catch (Exception) { }
                MethodInfo gvc = null;
                try
                {
                    gvc = elem.GetType().GetMethod("GetVisualChild", PrivInst, null,
                        new Type[] { typeof(int) }, null);
                }
                catch (Exception) { }
                if (gvc != null)
                {
                    for (int i = 0; i < vc && i < 40; i++)
                    {
                        object ch = null;
                        try { ch = gvc.Invoke(elem, new object[] { i }); } catch (Exception) { }
                        if (ch != null) kids.Add(ch);
                    }
                }
            }

            // 3) ContentControl.Content (single child)
            if (kids.Count == 0)
            {
                object content = null;
                try { content = Member(elem, "Content"); } catch (Exception) { }
                if (content != null && !(content is string)) kids.Add(content);
            }
            return kids;
        }

        private static string GameObjectPath(Transform t)
        {
            if (t == null) return null;
            string s = t.gameObject.name;
            int guard = 0;
            while (t.parent != null && guard++ < 16)
            {
                t = t.parent;
                s = t.gameObject.name + "/" + s;
            }
            return s;
        }

        /**
         * Probe the live simulation clocks and each friendly unit's Vehicle (track host).
         *
         * Deliberately does NOT touch Unity.Entities.World.All: enumerating it from a
         * mod on the main thread blocks (measured 20s+ timeout), and everything we need
         * is reachable through PlottingTable instead.
         */
        private static JObject ProbeWorld()
        {
            JObject o = new JObject();
            try
            {
                o["missionSessionTime"] = SafeNum(SeaPower.GameTime.missionSessionTime);
                o["missionElapsedTime"] = SafeNum(SeaPower.GameTime.missionElapsedTime);
                o["simulationTick"] = SafeNum(SeaPower.GameTime.simulationTick);
                o["timeCompression"] = SafeNum(SeaPower.GameTime.TimeCompression);
                o["isPaused"] = SeaPower.GameTime.IsPaused();
            }
            catch (Exception ex) { o["clockError"] = ex.Message; }

            JArray us = new JArray();
            o["vehicles"] = us;
            try
            {
                List<ObjectBase> all = AllObjects();
                int n = 0;
                for (int i = 0; i < all.Count && n < 12; i++)
                {
                    ObjectBase u = all[i];
                    if (u == null) continue;
                    bool isUnit = false;
                    try { isUnit = u.isUnit(); } catch (Exception) { }
                    if (!isUnit) continue;
                    n++;

                    JObject ju = new JObject();
                    ju["id"] = u.UniqueID;
                    ju["name"] = SafeStr(u.Name);
                    try
                    {
                        object v = FindVehicle(u);
                        if (v == null) { ju["vehicle"] = null; }
                        else
                        {
                            JObject jv = new JObject();
                            jv["trackId"] = SafeNum(Member(v, "Id"));
                            jv["initialDetectionEpoch"] = SafeNum(Member(v, "InitialDetectionEpoch"));
                            jv["updateEpoch"] = SafeNum(Member(v, "UpdateEpoch"));
                            jv["suspicion"] = SafeNum(Member(v, "Suspicion"));
                            jv["updatePhase"] = SafeNum(Member(v, "UpdatePhase"));
                            jv["detTrackEpochLive"] = SafeNum(Member(v, "_detTrackEpochLive"));
                            jv["detTrackMaxAge"] = SafeNum(Member(v, "_detTrackMaxAge"));
                            jv["detTrackHasComponent"] = Truthy(Member(v, "_detTrackHasComponent"));
                            jv["isSelfCached"] = Truthy(Member(v, "_isSelfCached"));
                            jv["isDetectedTrackFresh"] = Truthy(Member(v, "IsDetectedTrackFresh"));
                            jv["hasLiveTrack"] = Truthy(Member(v, "HasLiveTrack"));
                            jv["isObservedByVisual"] = Truthy(Member(v, "IsObservedByVisual"));
                            jv["detectingSensors"] = Names(Member(v, "DetectingSensors"));
                            ju["vehicle"] = jv;
                        }
                    }
                    catch (Exception ex) { ju["vehicleError"] = ex.Message; }
                    us.Add(ju);
                }
            }
            catch (Exception ex) { o["vehicleError"] = ex.Message; }
            return o;
        }

        /** Resolve the Vehicle (track host) for a unit through its PlottingTable. */
        private static object FindVehicle(ObjectBase u)
        {
            object pt = null;
            try { pt = u.PlottingTable; } catch (Exception) { }
            if (pt == null) return null;
            try
            {
                MethodInfo vfo = pt.GetType().GetMethod("VehicleForObject",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (vfo != null)
                {
                    object r = vfo.Invoke(pt, new object[] { u });
                    if (r != null) return r;
                }
            }
            catch (Exception) { }
            try
            {
                PropertyInfo lv = pt.GetType().GetProperty("LocalVehicles",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (lv != null)
                {
                    System.Collections.IDictionary d = lv.GetValue(pt, null) as System.Collections.IDictionary;
                    if (d != null && d.Count > 0)
                    {
                        foreach (object k in d.Keys) return d[k];
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // ====================================================== T04 / TR2-S sample
        // Ticked from BridgeBehaviour.Update on the main thread. The old /probe/session
        // only sampled while it was being polled, so a savegame reload landing between two
        // polls was invisible - and V4 (same save reloaded) is the vector the whole T04
        // question hangs on. Reading _objects directly is cheap (AllObjects returns the
        // cached GameMain list), the expensive work is the per-object loop, and that is
        // why this is throttled instead of running every frame.
        private static float _lastSessionTick = -10f;

        /// <summary>
        /// The player's own fleet, as of now. Same row shape as /units (so ids, names and
        /// positions agree with it), filtered to own units, plus the session identity.
        /// The failure mode it answers is concrete: an id kept from the previous scenario
        /// returns 404, and the caller then has no cheap way to ask what replaced it.
        /// </summary>
        internal static JObject FleetSnapshot()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "FleetSnapshot reads game state, which is only legal on the Unity main thread", false);
            JObject o = new JObject();
            o["session"] = "world-" + SessionRegistry.WorldGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
            o["worldGeneration"] = SessionRegistry.WorldGeneration;
            o["boundaryState"] = SessionRegistry.BoundaryState;

            JArray rows = new JArray();
            int air = 0, surface = 0, sub = 0, land = 0, dead = 0;
            List<SeaPower.ObjectBase> all = AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                SeaPower.ObjectBase u = all[i];
                if (u == null) continue;
                if (!SafeFlag(u, "mine")) continue;   // own side only: no foreign existence leak
                if (!SafeFlag(u, "unit")) continue;   // missiles and markers are not a fleet
                JObject r = UnitJson(u);
                // B15: the property is CurrentOrderText; "orderText" does not exist on ObjectBase.
                r["orderText"] = SafeStr(Rx(u, "CurrentOrderText"));
                rows.Add(r);
                if (SafeFlag(u, "destroyed")) dead++;
                else if (SafeFlag(u, "sub")) sub++;
                else if (SafeFlag(u, "air")) air++;
                else if (LandUnits.IsLand(u)) land++;
                else surface++;
            }
            o["count"] = rows.Count;
            o["destroyed"] = dead;
            JObject kinds = new JObject();
            kinds["air"] = air;
            kinds["surface"] = surface;
            kinds["sub"] = sub;
            kinds["land"] = land;
            o["byKind"] = kinds;
            o["units"] = rows;
            o["note"] = "own units only. ids are meaningful inside one world generation: after loading a scenario "
                + "call this again instead of reusing ids from the previous one.";
            return o;
        }

        private static int _fleetGen = -1;
        private static int _fleetCount;

        /// <summary>
        /// One log line per world generation change, so the log itself records that the fleet
        /// was re-read. Cheap: it only enumerates when the generation actually moves.
        /// </summary>
        private static void FleetGenerationTick()
        {
            int g = SessionRegistry.WorldGeneration;
            if (g == _fleetGen) return;
            _fleetGen = g;
            int n = 0;
            string err = null;
            try
            {
                List<SeaPower.ObjectBase> all = AllObjects();
                for (int i = 0; i < all.Count; i++)
                {
                    SeaPower.ObjectBase u = all[i];
                    if (u == null) continue;
                    if (!SafeFlag(u, "mine")) continue;
                    if (!SafeFlag(u, "unit")) continue;
                    n++;
                }
            }
            catch (Exception ex) { err = ex.GetType().Name; }
            _fleetCount = n;
            Core.Log("session entry: world generation " + g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> own fleet " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " unit(s)"
                + (err == null ? "" : " (count failed: " + err + ")")
                + "; ids from the previous world are not reusable, GET /session/fleet re-reads them");
        }
        public static void SessionTick()
        {
            if (!Core.IsMain) return;                 // reading Unity state off-thread is fatal
            float now;
            try { now = Time.realtimeSinceStartup; }
            catch (Exception) { return; }
            if (now - _lastSessionTick < 0.5f) return;
            _lastSessionTick = now;
            try { SessionRegistry.Offer(SessionSampleNow()); }
            catch (Exception) { }
            // Same tick, same thread: when the generation moves, say so in the log.
            try { FleetGenerationTick(); }
            catch (Exception) { }
        }

        // ACT-004 / T06: the panel's TextBox is the authority for what the player typed,
        // and it can only be touched on the main thread. Kept here rather than in
        // UiBridge so the Update loop has exactly one game-facing entry point.
        private static bool _keyPollBroken;
        private static bool _keyPollLogged;

        /// <summary>
        /// ACT-010 / I1: the inspector window's own hotkey.
        /// Polled rather than routed. Noesis raised no key events at all on the tactical
        /// root in a live session - the listener reported as attached, keyLog stayed
        /// empty - so relying on the UI's focus model would leave the window impossible
        /// to open. UnityEngine.Input does not care where the focus is. If this build
        /// has legacy input disabled the read throws once; it is caught, logged once and
        /// then skipped instead of being retried every frame.
        /// </summary>
        // TEMPORARY: the console/ inspector UI is switched off while the work-order
        // pipeline is being built. It flickered and stole the N key; the pages are
        // still here, they just do not paint and do not poll. Flip this to false to
        // bring the console back exactly as it was.
        private const bool UiHidden = true;

        public static void InspectorKeyTick()
        {
            if (_keyPollBroken) return;
            if (UiHidden) return;
            try
            {
                // The console host owns the single hotkey: N opens or collapses the whole
                // console. Switching tabs is a click on the strip under the top bar, not a
                // key, so no other key is polled here.
                // Two ways out of the text box, both polled rather than routed: Escape and
                // a left click. Inside Noesis the shell sits between the box and the root
                // tunnel, and the click that should release the lock never arrives - the
                // operator reported both dead while a tab click still worked (that path
                // releases because it unmounts the page).
                if (UiBridge.Typing)
                {
                    if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
                    {
                        UiBridge.ReleaseTypingNow("escape (frame poll)");
                        return;
                    }
                    // A click on the box itself re-arms on the same frame; a click anywhere
                    // else leaves the lock off, which is the behaviour the operator asked for.
                    if (UnityEngine.Input.GetMouseButtonDown(0))
                    {
                        UiBridge.ReleaseTypingNow("click outside the box (frame poll)");
                        return;
                    }
                }

                string hit = null;
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.N)) hit = "N";
                if (hit == null) return;
                if (!_keyPollLogged)
                {
                    _keyPollLogged = true;
                    Core.Log("key poll is delivering (" + hit + ")");
                }
                ConsoleHost.HandleKey(hit);
            }
            catch (Exception ex)
            {
                _keyPollBroken = true;
                Core.Log("key polling unavailable: " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        public static void UiTick()
        {
            if (!Core.IsMain) return;
            if (UiHidden) return;
            // (temporary) no window ticks while the UI is hidden
            try { UiBridge.Tick(); }
            catch (Exception) { }
            // One console, two tabs: decide who is mounted before either page paints.
            try { ConsoleHost.Tick(); }
            catch (Exception) { }
            // The inspector window reads only the buffer Api itself fills, so a broken
            // window can never take the bridge down with it.
            try { InspectorWindow.Tick(); }
            catch (Exception) { }
            // TR2-H: repaint the harness page only when its signature changed. Its own try
            // matters: a broken page must never stop the bridge or the inspector.
            try { CommsWindow.Tick(); }
            catch (Exception) { }
        }

        private static SessionSample SessionSampleNow()
        {
            SessionSample s = new SessionSample();
            s.TakenAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            try
            {
                s.MissionSessionTime = Convert.ToDouble(SeaPower.GameTime.missionSessionTime);
                s.MissionElapsedTime = Convert.ToDouble(SeaPower.GameTime.missionElapsedTime);
                s.SimulationTick = Convert.ToDouble(SeaPower.GameTime.simulationTick);
                s.ClockReadable = true;
            }
            catch (Exception) { }
            try { s.TimeCompression = Convert.ToDouble(SeaPower.GameTime.TimeCompression); } catch (Exception) { }
            try { s.Paused = SeaPower.GameTime.IsPaused(); } catch (Exception) { }

            try
            {
                UnityEngine.SceneManagement.Scene sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                s.SceneName = sc.name;
            }
            catch (Exception) { }

            try
            {
                List<ObjectBase> all = AllObjects();
                s.ObjectCount = all.Count;
                int maxUid = int.MinValue;
                int minUid = int.MaxValue;
                for (int i = 0; i < all.Count; i++)
                {
                    ObjectBase u = all[i];
                    if (u == null) continue;
                    int uid = u.UniqueID;
                    if (uid > maxUid) maxUid = uid;
                    if (uid < minUid) minUid = uid;
                    try { if (u.isUnit()) s.UnitCount++; } catch (Exception) { }
                }
                s.MaxUid = maxUid == int.MinValue ? 0 : maxUid;
                s.MinUid = minUid == int.MaxValue ? 0 : minUid;
            }
            catch (Exception) { }

            return s;
        }

        // ====================================================== T04 / TR2-S probe
        // Session boundary probe. Read-only, ring buffered, sampled on demand.
        // Question: after a new mission / a savegame load / a trip back to the menu,
        // which signal actually changes?

        private const int SessionRingMax = 256;
        private static readonly List<JObject> SessionRing = new List<JObject>();
        private static string _lastSceneName = null;
        private static int _sceneChangeCount = 0;

        private static JObject ProbeSession()
        {
            JObject s = new JObject();
            s["takenAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            try { s["missionSessionTime"] = SafeNum(SeaPower.GameTime.missionSessionTime); }
            catch (Exception ex) { s["missionSessionTime"] = null; s["clockError"] = ex.Message; }
            try { s["missionElapsedTime"] = SafeNum(SeaPower.GameTime.missionElapsedTime); } catch (Exception) { }
            try { s["simulationTick"] = SafeNum(SeaPower.GameTime.simulationTick); } catch (Exception) { }
            try { s["timeCompression"] = SafeNum(SeaPower.GameTime.TimeCompression); } catch (Exception) { }
            try { s["paused"] = SeaPower.GameTime.IsPaused(); } catch (Exception) { }

            string scene = null;
            try
            {
                UnityEngine.SceneManagement.Scene sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                scene = sc.name;
            }
            catch (Exception ex) { s["sceneError"] = ex.Message; }
            if (scene != null && scene != _lastSceneName) { _sceneChangeCount++; _lastSceneName = scene; }
            s["sceneName"] = scene;
            s["sceneChangeCount"] = _sceneChangeCount;

            int objCount = 0;
            int unitCount = 0;
            int maxUid = int.MinValue;
            int minUid = int.MaxValue;
            try
            {
                List<ObjectBase> all = AllObjects();
                objCount = all.Count;
                for (int i = 0; i < all.Count; i++)
                {
                    ObjectBase u = all[i];
                    if (u == null) continue;
                    int uid = u.UniqueID;
                    if (uid > maxUid) maxUid = uid;
                    if (uid < minUid) minUid = uid;
                    try { if (u.isUnit()) unitCount++; } catch (Exception) { }
                }
            }
            catch (Exception ex) { s["objectError"] = ex.Message; }
            s["objectCount"] = objCount;
            s["unitCount"] = unitCount;
            s["maxUid"] = maxUid == int.MinValue ? 0 : maxUid;
            s["minUid"] = minUid == int.MaxValue ? 0 : minUid;

            SessionRing.Add(s);
            if (SessionRing.Count > SessionRingMax) SessionRing.RemoveAt(0);

            JObject o = new JObject();
            o["current"] = s;
            o["sampleCount"] = SessionRing.Count;
            o["ringCapacity"] = SessionRingMax;
            JArray hist = new JArray();
            int from = SessionRing.Count - 20;
            if (from < 0) from = 0;
            for (int i = from; i < SessionRing.Count; i++) hist.Add(SessionRing[i]);
            o["history"] = hist;
            o["verdictGuidance"] = "A world rebuild must be detected by a signal that changes on V2 (menu to mission), V4 (same save reload) and V5 (mission, menu, same mission), but NOT on V3 (pause 30s) or V6 (time compression). Do not read EpochClock.CurrentGen: main-thread reflection on it deadlocks.";
            return o;
        }

        // ====================================================== T05 / TR2-I probe
        // Observer scoped intel probe. Walks exactly ONE authorised observer's
        // plotting table -- never all tables, which is how legacy /tracks leaks
        // across sides. Whitelisted fields only; unknown stays null, never 0 or "".

        private static JObject ProbeIntel(string query)
        {
            JObject o = new JObject();
            string obsRaw = FilterValue(query, "observer");
            int obsId = -1;
            if (obsRaw != null) int.TryParse(obsRaw, out obsId);
            o["observerId"] = obsId;
            if (obsId < 0)
            {
                o["error"] = "missing ?observer=<unitId>";
                o["hint"] = "this probe requires an explicit friendly observer and refuses to enumerate other sides' tables";
                return o;
            }
            ObjectBase obs = FindUnit(obsId);
            if (obs == null) { o["error"] = "observer unit not found"; return o; }
            o["observerName"] = SafeStr(obs.Name);

            object pt = null;
            try { pt = obs.PlottingTable; }
            catch (Exception ex) { o["plottingTableError"] = ex.Message; }
            if (pt == null) { o["error"] = "observer has no plotting table"; return o; }

            System.Collections.IList list = null;
            try
            {
                PropertyInfo pv = pt.GetType().GetProperty("Vehicles",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pv != null) list = pv.GetValue(pt, null) as System.Collections.IList;
                if (list == null)
                {
                    FieldInfo fv = pt.GetType().GetField("Vehicles",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fv != null) list = fv.GetValue(pt) as System.Collections.IList;
                }
            }
            catch (Exception ex) { o["vehiclesError"] = ex.Message; }

            JArray arr = new JArray();
            o["tracks"] = arr;
            if (list == null) { o["vehiclesUnreachable"] = true; return o; }

            int n = 0;
            for (int i = 0; i < list.Count && n < 200; i++)
            {
                object v = list[i];
                if (v == null) continue;
                n++;
                JObject t = new JObject();
                t["trackId"] = SafeNum(Member(v, "Id"));

                object sideObj = Member(v, "Side");
                string side = null;
                try
                {
                    SeaPower.Taskforce tfSide = sideObj as SeaPower.Taskforce;
                    if (tfSide != null) side = Convert.ToString(tfSide.Side);
                    else if (sideObj != null) side = Convert.ToString(sideObj);
                }
                catch (Exception) { }
                t["side"] = side;

                t["class"] = SourcedText(Member(v, "Class"));
                t["identified"] = Truthy(Member(v, "Identified"));
                t["freshness"] = Truthy(Member(v, "IsDetectedTrackFresh"));
                t["hasLiveTrack"] = Truthy(Member(v, "HasLiveTrack"));
                t["detectingSensors"] = Names(Member(v, "DetectingSensors"));

                t["precise"] = null;
                t["preciseUnavailable"] = "HasFreshPrecisePositionTrack needs an EntityManager, and every reachable way to obtain a World blocks the Unity main thread. Reported null rather than approximated.";

                t["positionObserved"] = GeoJson(Member(v, "DetectedPosition"), false);
                t["positionEstimated"] = GeoJson(Member(v, "PositionEstimate"), true);
                t["suspicion"] = SafeNum(Member(v, "Suspicion"));
                t["isSelfCached"] = Truthy(Member(v, "_isSelfCached"));
                arr.Add(t);
            }
            o["count"] = n;
            o["canaryNote"] = "TrueTargetPosition and RealUnityPosition are intentionally absent from every field above. Grep any response body for those names and for their values to prove non-leakage.";
            return o;
        }

        /** Unwrap a SourcedProperty<T> shell and return printable text, or null when the value is an object we must not render. */
        private static string SourcedText(object v)
        {
            if (v == null) return null;
            try
            {
                Type t = v.GetType();
                string tn = t.FullName;
                if (tn != null && tn.StartsWith("SeaPower.SourcedProperty"))
                {
                    PropertyInfo p = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null) { object inner = p.GetValue(v, null); return inner == null ? null : Convert.ToString(inner); }
                    FieldInfo f = t.GetField("<Value>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) { object inner = f.GetValue(v); return inner == null ? null : Convert.ToString(inner); }
                    return null;
                }
                if (tn != null && tn.StartsWith("SeaPower.")) return null;
            }
            catch (Exception) { return null; }
            string s = Convert.ToString(v);
            return (s == null || s.Length == 0) ? null : s;
        }

        /** Render a GeoPosition (or Nullable<GeoPosition>) without inventing values. */
        private static JObject GeoJson(object geo, bool isEstimate)
        {
            if (geo == null) return null;
            try
            {
                object inner = geo;
                Type t = geo.GetType();
                string tn = t.FullName;
                if (tn != null && tn.StartsWith("System.Nullable"))
                {
                    PropertyInfo hv = t.GetProperty("HasValue", BindingFlags.Instance | BindingFlags.Public);
                    if (hv != null && !Convert.ToBoolean(hv.GetValue(geo, null))) return null;
                    PropertyInfo vp = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                    if (vp != null) inner = vp.GetValue(geo, null);
                }
                if (inner == null) return null;
                JObject o = new JObject();
                o["lat"] = SafeNum(Member(inner, "Latitude"));
                o["lon"] = SafeNum(Member(inner, "Longitude"));
                o["height"] = SafeNum(Member(inner, "Height"));
                if (isEstimate) o["is_estimate"] = true;
                return o;
            }
            catch (Exception) { return null; }
        }

        // ====================================================== T06 / TR2-U probe
        // Locate the tactical UI host and the mount point, without mutating the
        // visual tree. Installing controls is deferred to the runtime phase.

        private static JObject ProbeUi2()
        {
            JObject o = new JObject();
            Type nvType = FindType("NoesisView");
            if (nvType == null) { o["error"] = "NoesisView type not found"; return o; }
            UnityEngine.Object[] views;
            try { views = UnityEngine.Object.FindObjectsOfType(nvType); }
            catch (Exception ex) { o["error"] = "FindObjectsOfType failed: " + ex.Message; return o; }

            JArray hosts = new JArray();
            o["hosts"] = hosts;
            bool sawTactical = false;
            for (int i = 0; i < views.Length; i++)
            {
                object view = views[i];
                if (view == null) continue;
                JObject h = new JObject();
                try
                {
                    UnityEngine.Component comp = view as UnityEngine.Component;
                    h["gameObject"] = comp != null ? comp.gameObject.name : null;
                }
                catch (Exception) { }
                object content = Member(view, "Content");
                string ct = content != null ? content.GetType().FullName : null;
                h["contentType"] = ct;
                if (ct != null && ct == "SeapowerUI.MainGameView")
                {
                    sawTactical = true;
                    h["isTacticalHost"] = true;
                    JObject mount = new JObject();
                    object grid = FindByName(content, "DisplayAreaGrid", 0);
                    mount["displayAreaGridFound"] = grid != null;
                    if (grid != null)
                    {
                        try
                        {
                            List<object> kids = UiChildren(grid);
                            mount["childCount"] = kids != null ? kids.Count : -1;
                        }
                        catch (Exception) { }
                    }
                    object console = FindByName(content, "ConsoleList", 0);
                    mount["consoleListFound"] = console != null;
                    object input = FindByName(content, "InputBox", 0);
                    mount["inputBoxFound"] = input != null;
                    h["mountPoint"] = mount;
                }
                else h["isTacticalHost"] = false;
                hosts.Add(h);
            }
            o["tacticalHostFound"] = sawTactical;
            o["mode"] = "report-only: nothing is inserted into the visual tree";
            o["runtimeRequirements"] = "Installing controls needs a real mission UI, plus IME composition validation (pinyin must not fire a send before confirmation).";
            return o;
        }

        /** Depth-first search of the Noesis visual tree by FrameworkElement.Name. */
        private static object FindByName(object root, string name, int depth)
        {
            if (root == null || depth > 6) return null;
            try
            {
                object nm = Member(root, "Name");
                if (nm != null && Convert.ToString(nm) == name) return root;
            }
            catch (Exception) { }
            List<object> kids;
            try { kids = UiChildren(root); }
            catch (Exception) { return null; }
            if (kids == null) return null;
            for (int i = 0; i < kids.Count; i++)
            {
                object r = FindByName(kids[i], name, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>
        /// Read-only reconnaissance of the live tactical map. This exists so the map work
        /// can answer "does the thing exist at runtime, where is it looking, how big is
        /// its render target" without guessing from IL. It changes nothing.
        /// </summary>
        private static JObject ProbeMap()
        {
            JObject o = new JObject();
            Type sceneType = FindType("SeaPower.LiveMap.MapScene");
            o["mapSceneType"] = sceneType == null ? null : sceneType.FullName;
            o["mapCameraType"] = FindType("SeaPower.LiveMap.MapCamera") == null
                ? null : "SeaPower.LiveMap.MapCamera";
            o["mapViewType"] = FindType("SeapowerUI.MapView") == null
                ? null : "SeapowerUI.MapView";
            if (sceneType == null)
            {
                o["error"] = "SeaPower.LiveMap.MapScene is not loaded in this build";
                return o;
            }

            UnityEngine.Object[] all;
            try { all = UnityEngine.Object.FindObjectsOfType(sceneType); }
            catch (Exception ex)
            {
                o["error"] = "FindObjectsOfType failed: " + ex.GetType().Name;
                return o;
            }
            o["sceneCount"] = all == null ? 0 : all.Length;
            if (all == null || all.Length == 0)
            {
                o["error"] = "no MapScene instance is alive (main menu, or the map is not built yet)";
                return o;
            }

            object scene = all[0];
            o["sceneName"] = ((UnityEngine.Object)scene).name;
            object viewport = Member(scene, "Viewport");
            if (viewport == null)
            {
                o["viewport"] = null;
            }
            else
            {
                JObject vp = new JObject();
                vp["west"] = Raw(Member(viewport, "West"));
                vp["east"] = Raw(Member(viewport, "East"));
                vp["south"] = Raw(Member(viewport, "South"));
                vp["north"] = Raw(Member(viewport, "North"));
                vp["width"] = Raw(Member(viewport, "Width"));
                vp["height"] = Raw(Member(viewport, "Height"));
                object center = Member(viewport, "Center");
                if (center != null)
                {
                    vp["centerLat"] = Raw(Member(center, "Latitude"));
                    vp["centerLon"] = Raw(Member(center, "Longitude"));
                }
                o["viewport"] = vp;
            }
            o["zoomLevel"] = Raw(Member(scene, "ZoomLevel"));

            object cam = Member(scene, "Camera");
            if (cam != null)
            {
                JObject c = new JObject();
                c["width"] = Raw(Member(cam, "Width"));
                c["height"] = Raw(Member(cam, "Height"));
                c["hasTexture"] = Member(cam, "MapTexture") != null;
                o["camera"] = c;
            }

            // The map view model is the cheap place to look: SeapowerUI.MapViewModel
            // holds Center (GeoPosition) and Zoom (Single) as plain properties, and the
            // view is a Noesis UserControl, so it can be reached through the same
            // tactical root the inspector mounts into.
            JObject ui = new JObject();
            Noesis.FrameworkElement root = UiBridge.TacticalRootForInspector();
            ui["tacticalRoot"] = root == null ? null : root.GetType().FullName;            if (root != null)
            {
                object mapView = UiBridge.FindTypeForInspector(root, "SeapowerUI.MapView");
                ui["mapViewFound"] = mapView != null;
                if (mapView != null)
                {
                    object dc = Member(mapView, "DataContext");
                    ui["dataContextType"] = dc == null ? null : dc.GetType().FullName;
                    if (dc != null)
                    {
                        object center = Member(dc, "Center");
                        if (center != null)
                        {
                            ui["centerLat"] = Raw(Member(center, "Latitude"));
                            ui["centerLon"] = Raw(Member(center, "Longitude"));
                        }
                        ui["zoom"] = Raw(Member(dc, "Zoom"));
                        ui["interactionState"] = Raw(Member(dc, "InteractionState"));
                    }
                }
            }
            o["ui"] = ui;
            return o;
        }

        // Reflection reads can hand back anything (float, double, null, a reactive box).
        // JSON wants a number or null, so unnamed numeric types are converted and the
        // rest is reported as a string rather than silently dropped.
        private static JToken Raw(object v)
        {
            if (v == null) return JValue.CreateNull();
            if (v is double || v is float || v is int || v is long || v is bool || v is string)
                return JToken.FromObject(v);
            return new JValue(v.ToString());
        }

        private static JObject ProbeTypes(string query)        {
            string pat = FilterValue(query, "q");
            JObject o = new JObject();
            o["pattern"] = pat;
            JArray hits = new JArray();
            o["matches"] = hits;
            if (pat == null || pat.Length == 0) return o;

            int found = 0;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < asms.Length && found < 60; a++)
            {
                Type[] types;
                try { types = asms[a].GetTypes(); }
                catch (Exception) { continue; }
                for (int t = 0; t < types.Length && found < 60; t++)
                {
                    Type ty = types[t];
                    if (ty == null) continue;
                    string full = ty.FullName;
                    if (full == null) continue;
                    if (full.IndexOf(pat, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    JObject h = new JObject();
                    h["type"] = full;
                    h["assembly"] = asms[a].GetName().Name;
                    hits.Add(h);
                    found++;
                }
            }
            o["count"] = found;
            return o;
        }

        private static JObject ProbeTypeMembers(string typeName)
        {
            JObject o = new JObject();
            o["type"] = typeName;
            Type ty = FindType(typeName);
            if (ty == null) { o["error"] = "type not found"; return o; }
            JArray fields = new JArray();
            JArray props = new JArray();
            JArray methods = new JArray();
            o["fields"] = fields; o["properties"] = props; o["methods"] = methods;
            try
            {
                FieldInfo[] fs = ty.GetFields(BindingFlags.Instance | BindingFlags.Static |
                                              BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fs.Length && i < 200; i++)
                    fields.Add(fs[i].FieldType.Name + " " + fs[i].Name);
                PropertyInfo[] ps = ty.GetProperties(BindingFlags.Instance | BindingFlags.Static |
                                                     BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < ps.Length && i < 200; i++)
                    props.Add(ps[i].PropertyType.Name + " " + ps[i].Name);
                MethodInfo[] ms = ty.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.DeclaredOnly);
                for (int i = 0; i < ms.Length && i < 200; i++)
                    methods.Add(ms[i].Name);
            }
            catch (Exception ex) { o["error"] = ex.Message; }
            return o;
        }

        private static Type FindType(string fullName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t;
                try { t = asms[i].GetType(fullName, false); }
                catch (Exception) { continue; }
                if (t != null) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers

        private static GeoPosition GetGeo(ObjectBase u)
        {
            object p = Rx(u, "Position");
            if (p is GeoPosition) return (GeoPosition)p;
            return GeoPosition.zero;
        }

        private static object Member(object holder, string name)
        {
            if (holder == null) return null;
            try
            {
                Type t = holder.GetType();
                FieldInfo f = t.GetField(name, PrivInst);
                if (f != null) return f.GetValue(holder);
                PropertyInfo p = t.GetProperty(name, PrivInst);
                if (p != null) return p.GetValue(holder, null);
                return null;
            }
            catch (Exception) { return null; }
        }

        private static bool SetMember(object holder, string name, object value)
        {
            if (holder == null) return false;
            try
            {
                Type t = holder.GetType();
                FieldInfo f = t.GetField(name, PrivInst);
                if (f != null) { f.SetValue(holder, value); return true; }
                PropertyInfo p = t.GetProperty(name, PrivInst);
                if (p != null && p.CanWrite) { p.SetValue(holder, value, null); return true; }
            }
            catch (Exception) { }
            return false;
        }

        internal static object Rx(object holder, string prop)
        {
            if (holder == null) return null;
            try
            {
                PropertyInfo p = holder.GetType().GetProperty(prop, PrivInst);
                if (p == null) return null;
                object v = p.GetValue(holder, null);
                if (v == null) return null;
                PropertyInfo vp = v.GetType().GetProperty("Value");
                if (vp != null) return vp.GetValue(v, null);
                return v;
            }
            catch (Exception) { return null; }
        }

        private static string[] SplitPath(string path, out string query)
        {
            query = null;
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                query = path.Substring(q + 1);
                path = path.Substring(0, q);
            }
            // Trim(char) is not available on every runtime we target; the array overload is.
            path = path.Trim(new char[] { '/' });
            if (path.Length == 0) return new string[0];
            return path.Split(new char[] { '/' });
        }

        private static JObject ParseBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return new JObject();
            try { return JObject.Parse(body); }
            catch (Exception ex)
            {
                // R2: a body that does not parse is a 400. It used to become {} and then
                // every missing field silently took its default - a broken "speed" request
                // turned into "all stop" and a broken "attack" still picked a target.
                throw new ApiException(400, "bad_json",
                    "request body is not valid JSON: " + ex.Message, false);
            }
        }

        private static string FilterValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string[] parts = query.Split(new char[] { '&' });
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                if (parts[i].Substring(0, eq) == key) return parts[i].Substring(eq + 1);
            }
            return null;
        }

        // R2: the four Req* helpers below distinguish "absent" from "present but wrong".
        // Absent keeps the documented default. Wrong type is a 400 - a typo must never
        // turn into a command.
        /// <summary>
        /// Distinguishes "the caller omitted this field" (null -> use the default) from
        /// "the caller sent an explicit null" (a mistake). Rework 3: an explicit null used to
        /// fall through to the default, so {"validate_only": null} quietly meant "go live".
        /// </summary>
        private static JToken Present(JObject req, string key)
        {
            JToken t = req == null ? null : req[key];
            if (t == null) return null;
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            return t;
        }

        private static float ReqFloat(JObject req, string key, float dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be a number, got " + t.Type.ToString(), false);
            float v;
            try { v = (float)t; }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type",
                    "'" + key + "' is not a valid number", false);
            }
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new ApiException(400, "bad_number",
                    "'" + key + "' must be a finite number", false);
            return v;
        }

        /// <summary>Same as ReqFloat plus an inclusive range, so 999 knots or a latitude of
        /// 1200 degrees are rejected while the request is still pure data.</summary>
        private static float ReqFloatIn(JObject req, string key, float dflt, float min, float max)
        {
            float v = ReqFloat(req, key, dflt);
            if (v < min || v > max)
                throw new ApiException(400, "out_of_range",
                    "'" + key + "' must be between " + min + " and " + max + ", got " + v, false);
            return v;
        }

        private static int ReqInt(JObject req, string key, int dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be an integer, got " + t.Type.ToString(), false);
            int v;
            try { v = (int)t; }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type",
                    "'" + key + "' is not a valid integer", false);
            }
            return v;
        }

        private static int ReqIntIn(JObject req, string key, int dflt, int min, int max)
        {
            int v = ReqInt(req, key, dflt);
            if (v < min || v > max)
                throw new ApiException(400, "out_of_range",
                    "'" + key + "' must be between " + min + " and " + max + ", got " + v, false);
            return v;
        }

        private static bool ReqBool(JObject req, string key, bool dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Boolean)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be true or false, got " + t.Type.ToString(), false);
            return (bool)t;
        }

        private static string ReqStr(JObject req, string key, string dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            // Rework 3: accepting an integer here let {"ammo": 5} become the string "5" and
            // reach the game as a bogus ammunition name.
            if (t.Type != JTokenType.String)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be a string, got " + t.Type.ToString(), false);
            return (string)t;
        }

        // R2: unknown fields are rejected so a typo like "knot" instead of "knots" cannot
        // fall through to the default. Each command declares what it accepts.
        private static void RejectUnknownFields(JObject req, string op, string[] allowed)
        {
            if (req == null) return;
            foreach (JProperty p in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == p.Name) { known = true; break; }
                }
                if (!known)
                    throw new ApiException(400, "unknown_field",
                        "op '" + op + "' does not accept field '" + p.Name + "'", false);
            }
        }

        internal static string SafeStr(object o)
        {
            if (o == null) return null;
            try { return o.ToString(); }
            catch (Exception) { return null; }
        }

        internal static JToken SafeNum(object o)
        {
            if (o == null) return JValue.CreateNull();
            try
            {
                double d = Convert.ToDouble(o);
                if (double.IsNaN(d) || double.IsInfinity(d)) return JValue.CreateNull();
                return new JValue(Math.Round(d, 3));
            }
            catch (Exception) { return JValue.CreateNull(); }
        }
    }
}
