// Minimal HTTP/1.1 server on top of a raw Socket (avoids Mono's HttpListener quirks).
// T01: strict request parsing. Nothing reaches the game unless the request is fully
// formed, within size limits, and uses an allowed method.
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SeaPowerAgentBridge
{
    /// <summary>Parsed request, or the HTTP status that should be returned instead.</summary>
    public sealed class HttpReq
    {
        public int Status;          // 200 = parsed ok, otherwise the error status to return
        public string Error;        // short machine code for the error status
        public string Method;
        public string Path;
        public string Body;
    }

    /// <summary>
    /// Handler reply carrying the real HTTP status. The status must reach the wire:
    /// a rejected command that answers 200 with an error body teaches clients to ignore
    /// status codes entirely, which is exactly what TR1 had to fix.
    /// </summary>
    public sealed class HttpResult
    {
        public int Status;
        public string Body;

        // A screenshot is an image, not JSON with a giant base64 string inside it: the
        // caller asked for a picture and gets image/png bytes. Raw wins when it is set.
        public byte[] Raw;
        public string ContentType;

        public HttpResult(int status, string body)
        {
            Status = status;
            Body = body;
        }

        public HttpResult(int status, byte[] raw, string contentType)
        {
            Status = status;
            Raw = raw;
            ContentType = contentType;
        }
    }


    public static class MiniHttp
    {
        // Limits from the implementation architecture (§10).
        public const int MaxHeaderBytes = 16 * 1024;
        public const int MaxBodyBytes = 256 * 1024;
        public const int MaxConcurrent = 32;
        public const int ReadTimeoutMs = 15000;

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        // R5: body bytes must be valid UTF-8. Encoding.UTF8 replaces bad sequences with
        // U+FFFD, which silently accepted a garbled request; the strict variant throws.
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Monotonic milliseconds since a start tick (Environment.TickCount wraps safely in unchecked math).</summary>
        private static int ElapsedMs(int startedAt)
        {
            return unchecked(Environment.TickCount - startedAt);
        }

        /// <summary>
        /// True only when the media type is exactly application/json, ignoring case and any
        /// "; charset=..." parameters. A substring test used to accept "application/json-foo".
        /// </summary>
        private static bool IsJsonMediaType(string contentType)
        {
            if (contentType == null) return false;
            int semi = contentType.IndexOf(';');
            string mime = (semi >= 0 ? contentType.Substring(0, semi) : contentType);
            mime = mime.Trim().ToLowerInvariant();
            return mime == "application/json";
        }

        /// <summary>
        /// Shrinks the per-read socket timeout to whatever is left of the whole-request budget,
        /// so a single blocking read cannot outlive the deadline.
        /// </summary>
        private static bool ApplyRemainingBudget(NetworkStream ns, int startedAt)
        {
            int remaining = ReadTimeoutMs - ElapsedMs(startedAt);
            if (remaining <= 0) return false;
            try { ns.ReadTimeout = remaining; }
            catch (Exception) { }
            return true;
        }

        private static Socket _socket;
        private static Thread _thread;
        private static volatile bool _running;
        private static Func<string, string, string, HttpResult> _handler;
        private static int _inFlight;
        private static volatile bool _diagnostics;

        public static void SetHandler(Func<string, string, string, HttpResult> handler)
        {
            _handler = handler;
        }

        public static bool Running { get { return _running; } }

        /// <summary>Raw diagnostic routes (/__raw, /__d1..3) are off unless explicitly enabled.</summary>
        public static bool DiagnosticsEnabled
        {
            get { return _diagnostics; }
            set { _diagnostics = value; }
        }

        public static void Start(int port)
        {
            if (_running) return;

            // A raw socket is used so SO_REUSEADDR can be set. A game process that is killed
            // hard leaves the loopback port in TIME_WAIT, and a plain TcpListener.Start()
            // then fails to bind for minutes.
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            s.Listen(128);
            _socket = s;
            _running = true;
            _thread = new Thread(AcceptLoop);
            _thread.IsBackground = true;
            _thread.Name = "SPMCP_Http";
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            Socket s = _socket;
            _socket = null;
            try { if (s != null) s.Close(); }
            catch (Exception) { }
        }

        private static void AcceptLoop()
        {
            Core.Log("accept loop started");
            while (_running)
            {
                Socket client = null;
                try
                {
                    Socket listener = _socket;
                    if (listener == null) break;
                    client = listener.Accept();
                }
                catch (Exception e)
                {
                    if (_running) Core.Log("accept failed: " + e.Message);
                    break;
                }

                if (Interlocked.Increment(ref _inFlight) > MaxConcurrent)
                {
                    Interlocked.Decrement(ref _inFlight);
                    RejectBusy(client);
                    continue;
                }

                try
                {
                    ThreadPool.QueueUserWorkItem(HandleClientTracked, client);
                }
                catch (Exception e)
                {
                    Core.Log("dispatch failed: " + e.Message);
                    Interlocked.Decrement(ref _inFlight);
                    try { client.Close(); }
                    catch (Exception) { }
                }
            }
            Core.Log("accept loop ended");
        }

        private static void HandleClientTracked(object state)
        {
            try { HandleClient(state); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        private static void RejectBusy(Socket client)
        {
            try
            {
                client.NoDelay = true;
                client.SendTimeout = 2000;
                NetworkStream ns = new NetworkStream(client, false);
                WriteResponse(ns, 503, ErrorJson("server_busy", "too many concurrent requests", true));
                ns.Flush();
            }
            catch (Exception) { }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

                // D6: every request carries an id. Both log lines print it and the reply envelope
        // carries it as "cmd", because "POST /units/5/attack bodyLen=45" followed by
        // "replied 200 1234 chars" are otherwise two unrelated lines - matching them took
        // manual timestamp work, and a write that did nothing was unattributable.
        [ThreadStatic]
        internal static long CurrentCommandId;
        private static long _commandSeq;

        internal static long NextCommandId()
        {
            return System.Threading.Interlocked.Increment(ref _commandSeq);
        }

private static void HandleClient(object state)
        {
                        Socket client = (Socket)state;
            CurrentCommandId = NextCommandId();
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = ReadTimeoutMs + 2000;
                client.SendTimeout = ReadTimeoutMs;
                NetworkStream ns = new NetworkStream(client, true);

                HttpReq req = ReadRequest(ns);
                if (req.Status != 200)
                {
                    Core.Log("REQ #" + CurrentCommandId + " rejected " + req.Status + " " + req.Error);
                    WriteResponse(ns, req.Status, ErrorJson(req.Error, ErrorText(req.Error), req.Status >= 500));
                    ns.Flush();
                    return;
                }

                Core.Log("REQ #" + CurrentCommandId + " " + req.Method + " " + req.Path + " bodyLen=" + (req.Body == null ? 0 : req.Body.Length));

                // Diagnostic surfaces (/__* legacy probes and /probe/* white-box
                // readers) must be OFF by default. /probe/* leaks the whole Noesis
                // visual tree, per-unit Vehicle internals and cross-side contact
                // data, so it is gated by exactly the same switch.
                if (req.Path != null && (req.Path.StartsWith("/__") || req.Path.StartsWith("/probe")) && !DiagnosticsEnabled)
                {
                    Core.Log("diagnostic route blocked: " + req.Path);
                    WriteResponse(ns, 404, ErrorJson("diagnostics_disabled", "diagnostic routes are disabled", false));
                    ns.Flush();
                    return;
                }

                HttpResult result;
                if (req.Method == "OPTIONS")
                {
                    result = new HttpResult(200, "{\"ok\":true,\"data\":{\"cors\":true}}");
                }
                else if (_handler == null)
                {
                    result = new HttpResult(503, ErrorJson("handler_not_ready", "request handler is not installed yet", true));
                }
                else
                {
                    try { result = _handler(req.Method, req.Path, req.Body); }
                    catch (Exception e)
                    {
                        Core.Log("handler threw: " + e.GetType().Name + ": " + e.Message);
                        result = new HttpResult(500, ErrorJson("handler_exception", e.GetType().Name + ": " + e.Message, false));
                    }
                }
                if (result == null) result = new HttpResult(500, ErrorJson("empty_reply", "handler returned nothing", false));
                if (result.Raw == null && result.Body == null) result.Body = ErrorJson("empty_reply", "handler returned nothing", false);

                WriteResponse(ns, result);
                ns.Flush();
                Core.Log("replied #" + CurrentCommandId + " " + result.Status + " "
                    + (result.Raw != null
                        ? result.Raw.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes"
                        : result.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " chars"));
            }
            catch (Exception e)
            {
                Core.Log("client error: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Reads exactly one request. Returns Status 200 with a filled HttpReq, or a non-200
        /// Status that the caller turns into an HTTP error. A body is only handed on when it
        /// was received in full, so a truncated upload can never reach the game.
        /// </summary>
        private static HttpReq ReadRequest(NetworkStream ns)
        {
            HttpReq req = new HttpReq();
            req.Status = 200;

            byte[] chunk = new byte[4096];
            StringBuilder head = new StringBuilder();
            int headerEnd = -1;

            // R5: one absolute deadline for the whole request. ns.ReadTimeout only bounds a
            // single blocking read, so a slow-drip client could hold a worker indefinitely
            // by sending one byte every few seconds.
            int startedAt = Environment.TickCount;

            while (true)
            {
                if (head.Length > MaxHeaderBytes)
                {
                    req.Status = 431;
                    req.Error = "headers_too_large";
                    return req;
                }
                if (ElapsedMs(startedAt) > ReadTimeoutMs)
                {
                    req.Status = 408;
                    req.Error = "request_timeout";
                    return req;
                }
                // Rework 5: tighten the socket timeout to the remaining budget so one blocking
                // read cannot overshoot the whole-request deadline.
                if (!ApplyRemainingBudget(ns, startedAt))
                {
                    req.Status = 408;
                    req.Error = "request_timeout";
                    return req;
                }
                int r;
                try { r = ns.Read(chunk, 0, chunk.Length); }
                catch (System.IO.IOException)
                {
                    req.Status = 408;
                    req.Error = "request_timeout";
                    return req;
                }
                if (r <= 0)
                {
                    req.Status = 400;
                    req.Error = "empty_request";
                    return req;
                }
                head.Append(Latin1.GetString(chunk, 0, r));
                string h = head.ToString();
                headerEnd = h.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0) break;
            }

            // Rework 5: the read that completed the header may have returned after the deadline.
            // Leaving the loop on success used to skip the clock entirely.
            if (ElapsedMs(startedAt) >= ReadTimeoutMs)
            {
                req.Status = 408;
                req.Error = "request_timeout";
                return req;
            }

            // R5: the terminator can arrive in the same read that pushes the buffer past
            // the cap, so the size has to be re-checked once it is found.
            if (headerEnd > MaxHeaderBytes)
            {
                req.Status = 431;
                req.Error = "headers_too_large";
                return req;
            }

            string headText = head.ToString();
            // R5: only the bytes before the terminator are headers. Scanning the whole
            // buffer let a Transfer-Encoding line inside the body be read as a header.
            string headerBlock = headText.Substring(0, headerEnd);
            string[] lines = headerBlock.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                req.Status = 400;
                req.Error = "bad_request_line";
                return req;
            }

            string[] reqLine = lines[0].Split(new char[] { ' ' });
            if (reqLine.Length < 3)
            {
                req.Status = 400;
                req.Error = "bad_request_line";
                return req;
            }
            req.Method = reqLine[0].ToUpperInvariant();
            req.Path = reqLine[1];
            if (!reqLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
            {
                req.Status = 400;
                req.Error = "bad_http_version";
                return req;
            }

            if (req.Method != "GET" && req.Method != "POST" && req.Method != "OPTIONS" && req.Method != "HEAD" && req.Method != "DELETE")
            {
                req.Status = 405;
                req.Error = "method_not_allowed";
                return req;
            }

            // Content-Length: at most one, valid, non-negative, within the body cap.
            int contentLength = 0;
            bool seenLength = false;
            bool seenTransferEncoding = false;
            string contentType = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                int colon = ln.IndexOf(':');
                if (colon <= 0) continue;
                string name = ln.Substring(0, colon).Trim().ToLowerInvariant();
                string val = ln.Substring(colon + 1).Trim();

                if (name == "content-length")
                {
                    if (seenLength)
                    {
                        req.Status = 400;
                        req.Error = "duplicate_content_length";
                        return req;
                    }
                    seenLength = true;
                    int parsed;
                    if (!int.TryParse(val, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed) || parsed < 0)
                    {
                        req.Status = 400;
                        req.Error = "bad_content_length";
                        return req;
                    }
                    contentLength = parsed;
                }
                else if (name == "transfer-encoding")
                {
                    seenTransferEncoding = true;
                }
                else if (name == "content-type")
                {
                    contentType = val;
                }
            }

            if (seenTransferEncoding)
            {
                // Chunked uploads are not supported; refusing them is safer than guessing.
                req.Status = 400;
                req.Error = "transfer_encoding_unsupported";
                return req;
            }

            if (contentLength > MaxBodyBytes)
            {
                req.Status = 413;
                req.Error = "body_too_large";
                return req;
            }

            if (req.Method == "GET" && contentLength > 0)
            {
                req.Status = 400;
                req.Error = "get_with_body";
                return req;
            }

            // Rework 5: exact media type, not a substring search. "application/json-foo" and
            // any parameter value containing "application/json" used to pass. The contract is
            // now: a request with a body MUST declare application/json.
            if (contentLength > 0)
            {
                if (contentType == null)
                {
                    req.Status = 415;
                    req.Error = "missing_content_type";
                    return req;
                }
                if (!IsJsonMediaType(contentType))
                {
                    req.Status = 415;
                    req.Error = "unsupported_content_type";
                    return req;
                }
            }

            // Bytes that arrived together with the header block.
            string overflow = headText.Length > headerEnd + 4 ? headText.Substring(headerEnd + 4) : "";
            StringBuilder bodyBuf = new StringBuilder(overflow);

            int need = contentLength;
            if (need == 0)
            {
                req.Body = "";
                return req;
            }

            while (bodyBuf.Length < need)
            {
                // R5: the body phase shares the same absolute deadline as the header phase,
                // but running out of time with a partial body is a truncation, not a timeout.
                if (ElapsedMs(startedAt) > ReadTimeoutMs)
                {
                    req.Status = 400;
                    req.Error = "body_truncated";
                    return req;
                }
                if (!ApplyRemainingBudget(ns, startedAt))
                {
                    req.Status = 400;
                    req.Error = "body_truncated";
                    return req;
                }
                int r;
                try { r = ns.Read(chunk, 0, chunk.Length); }
                catch (System.IO.IOException) { r = 0; }
                if (r <= 0) break;
                bodyBuf.Append(Latin1.GetString(chunk, 0, r));
            }

            // Rework 5: a complete body that only arrived after the deadline is still late.
            if (ElapsedMs(startedAt) >= ReadTimeoutMs)
            {
                req.Status = 400;
                req.Error = "body_truncated";
                return req;
            }

            if (bodyBuf.Length < need)
            {
                req.Status = 400;
                req.Error = "body_truncated";
                return req;
            }

            string raw = bodyBuf.ToString();
            if (raw.Length > need) raw = raw.Substring(0, need);
            byte[] bytes = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++) bytes[i] = (byte)raw[i];
            if (bytes.Length > 0)
            {
                // R5: reject rather than silently substituting U+FFFD for bad bytes.
                try
                {
                    req.Body = StrictUtf8.GetString(bytes);
                }
                catch (Exception)
                {
                    req.Status = 400;
                    req.Error = "bad_utf8";
                    return req;
                }
            }
            else
            {
                req.Body = "";
            }
            return req;
        }

        private static void WriteResponse(NetworkStream ns, HttpResult result)
        {
            if (result.Raw != null)
            {
                WriteResponse(ns, result.Status, result.Raw,
                    result.ContentType == null ? "application/octet-stream" : result.ContentType);
                return;
            }
            WriteResponse(ns, result.Status, result.Body);
        }

        private static void WriteResponse(NetworkStream ns, int code, byte[] payload, string contentType)
        {
            string head =
                "HTTP/1.1 " + code + " " + Reason(code) + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(payload, 0, payload.Length);
        }

        private static void WriteResponse(NetworkStream ns, int code, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            string head =
                "HTTP/1.1 " + code + " " + Reason(code) + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            ns.Write(hb, 0, hb.Length);
            ns.Write(payload, 0, payload.Length);
        }

        private static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                case 431: return "Request Header Fields Too Large";
                case 503: return "Service Unavailable";
                default: return "Internal Server Error";
            }
        }

        private static string ErrorText(string code)
        {
            if (code == "headers_too_large") return "request headers exceed " + MaxHeaderBytes + " bytes";
            if (code == "body_too_large") return "request body exceeds " + MaxBodyBytes + " bytes";
            if (code == "duplicate_content_length") return "multiple Content-Length headers are not allowed";
            if (code == "bad_content_length") return "Content-Length must be a non-negative integer";
            if (code == "transfer_encoding_unsupported") return "Transfer-Encoding is not supported";
            if (code == "body_truncated") return "request body ended before Content-Length bytes arrived";
            if (code == "get_with_body") return "GET requests must not carry a body";
            if (code == "request_timeout") return "the request did not finish within " + ReadTimeoutMs + " ms";
            if (code == "bad_utf8") return "request body is not valid UTF-8";
            if (code == "missing_content_type") return "a request with a body must declare Content-Type: application/json";
            if (code == "unsupported_content_type") return "only application/json bodies are accepted";
            if (code == "method_not_allowed") return "only GET, POST, OPTIONS, HEAD and DELETE are allowed";
            if (code == "empty_request") return "connection closed before a request arrived";
            if (code == "bad_request_line") return "malformed HTTP request line";
            if (code == "bad_http_version") return "malformed HTTP version";
            if (code == "server_busy") return "too many concurrent requests";
            return code;
        }

        /// <summary>Standard error envelope: {"ok":false,"error":{code,message,retryable}}.</summary>
        public static string ErrorJson(string code, string message, bool retryable)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) +
                   ",\"message\":" + Quote(message) +
                   ",\"retryable\":" + (retryable ? "true" : "false") + "}}";
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }
}
