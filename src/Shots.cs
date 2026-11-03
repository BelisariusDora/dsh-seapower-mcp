// Sea Power Agent Bridge - on-demand frame capture.
// Compiled with C# 5 (framework csc.exe). No string interpolation, no ?., no nameof.
//
// Scope, from GAME_SCREENSHOT_CAPABILITY_PROPOSAL.md:
//   * one picture at a time, bounded wait, explicit failure - never an old frame
//     dressed up as a new one;
//   * the picture is the frame the player can see, WITH whatever UI is on screen. That
//     is not the same thing as an IntelPolicy-filtered AgentContext, and this file must
//     never claim otherwise;
//   * no caller-chosen paths. Evidence goes to <mod>\shots\ when asked for;
//   * texture size comes from the captured texture, never from Noesis logical space,
//     and nothing is ever upscaled.
//
// Threading: Dispatch/Status/Png run on the HTTP worker thread and touch no Unity API.
// Tick() runs from BridgeBehaviour.Update() (main thread) and starts one end-of-frame
// coroutine. The HTTP thread never waits on Unity.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    internal static class Shots
    {
        // A picture to read, not a picture to archive: 1024 px is enough to check layout
        // at a glance, and "full" gives the untouched texture for CJK glyph and 1px-line
        // diagnosis.
        internal const int ReadWidth = 1024;
        internal const int MaxOutputWidth = 4096;
        internal const int TimeoutMs = 12000;

        private sealed class Entry
        {
            public int Id;
            public string Status;        // queued | capturing | done | error
            public string Error;

            public int TexWidth;
            public int TexHeight;
            public int Width;            // what was actually delivered
            public int Height;
            public int Bytes;

            public int Frame;
            public float SinceStart;
            public string Wall;
            public string Session;

            public bool WantsFull;
            public bool WantsSave;
            public string SavedPath;

            public byte[] Png;
            public int StartedTick;
            public bool Started;

            /// <summary>Size of the file actually written, which is not the size handed to
            /// the agent when ShotSaveFull is on. Reported separately so nobody has to guess
            /// which of the two the path refers to.</summary>
            public int SavedWidth;
            public int SavedHeight;
            public int SavedBytes;

            /// <summary>Set by an explicit reset so a late coroutine cannot resurrect the
            /// abandoned capture. Without this the entry keeps its id but silently turns
            /// done - and Png() has no id check, so a caller would get a frame it thought
            /// it had thrown away.</summary>
            public bool Cancelled;
        }

        private static Entry _current;
        private static int _nextId = 1;
        private static MonoBehaviour _host;
        private static string _dir;

        internal static void Attach(MonoBehaviour host)
        {
            _host = host;
            // Fixed evidence directory under the mod folder. A caller never chooses a path:
            // a screenshot that can be written anywhere is a file-write primitive, not a
            // diagnostic read.
            try
            {
                string dir = Core.ModDir();
                if (!string.IsNullOrEmpty(dir)) _dir = Path.Combine(dir, "shots");
            }
            catch (Exception) { _dir = null; }
        }

        /// <summary>
        /// take: queue one capture. status: report the last one. The bytes are served
        /// separately by GET /shot/png - an image is not a JSON field.
        /// </summary>
        internal static JObject Dispatch(JObject req)
        {
            string op = req == null ? null : (string)req["op"];
            if (string.IsNullOrEmpty(op)) op = "status";

            if (op == "take") return Take(req);
            if (op == "status") return Status();
            if (op == "reset") return Reset();
            throw new ApiException(404, "not_found", "unknown shot op: " + op, false);
        }

        private static bool Flag(JObject req, string key)
        {
            JToken t = req == null ? null : req[key];
            if (t == null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return string.Equals(t.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Setting in setting.ini, read at save time so a hand edit takes effect
        /// without restarting. A parse failure must never lose the capture, so anything
        /// unexpected falls back to the small copy that was already encoded.</summary>
        private static bool WantsFullResOnDisk()
        {
            try
            {
                string dir = Core.ModDir();
                if (string.IsNullOrEmpty(dir)) return false;
                return InspectorSettings.Load(dir).ShotSaveFull;
            }
            catch (Exception) { return false; }
        }

        internal static JObject Take(JObject req)
        {
            Entry e = _current;
            if (e != null && (e.Status == "queued" || e.Status == "capturing"))
            {
                // One at a time on purpose: two captures racing for the back buffer would
                // double the peak texture cost for no extra information.
                throw new ApiException(409, "shot_busy",
                    "shot " + e.Id + " is still being taken; ask for status and retry", true);
            }

            Entry n = new Entry();
            n.Id = _nextId++;
            n.Status = "queued";
            n.Error = "";
            n.WantsFull = Flag(req, "full");
            n.WantsSave = Flag(req, "save");
            n.StartedTick = Environment.TickCount;
            n.Wall = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            n.Session = SessionOf();
            _current = n;

            Core.Log("shot " + n.Id + " queued (full=" + n.WantsFull + ", save=" + n.WantsSave + ")");
            return Status();
        }

        internal static JObject Status()
        {
            JObject o = new JObject();
            Entry e = _current;
            if (e == null)
            {
                o["status"] = "idle";
                o["ok"] = true;
                o["taken"] = _nextId - 1;
                return o;
            }
            o["id"] = e.Id;
            o["status"] = e.Status;
            // A failed capture must not read back as a success: the envelope turns this
            // into ok:false so the MCP layer raises an error instead of handing the model
            // an old picture with a fresh-looking timestamp.
            o["ok"] = !string.Equals(e.Status, "error", StringComparison.Ordinal);
            o["frame"] = e.Frame;
            o["wall"] = e.Wall;
            o["session"] = e.Session;
            o["textureWidth"] = e.TexWidth;
            o["textureHeight"] = e.TexHeight;
            o["width"] = e.Width;
            o["height"] = e.Height;
            o["bytes"] = e.Bytes;
            o["full"] = e.WantsFull;
            o["scaled"] = e.TexWidth > 0 && (e.Width != e.TexWidth);
            o["ageMs"] = Math.Max(0, Environment.TickCount - e.StartedTick);
            if (!string.IsNullOrEmpty(e.SavedPath)) o["saved"] = e.SavedPath;
            if (!string.IsNullOrEmpty(e.SavedPath))
            {
                // Separate from width/height on purpose: those describe what the agent was
                // handed, these describe the file. With ShotSaveFull they differ.
                o["savedWidth"] = e.SavedWidth;
                o["savedHeight"] = e.SavedHeight;
                o["savedBytes"] = e.SavedBytes;
            }
            if (!string.IsNullOrEmpty(e.Error)) o["error"] = e.Error;
            if (e.Status == "queued" || e.Status == "capturing") o["retryable"] = true;
            return o;
        }

        /// <summary>The delivered PNG, or null when there is nothing to serve.</summary>
        internal static byte[] Png(out string why)
        {
            why = null;
            Entry e = _current;
            if (e == null)
            {
                why = "no shot has been taken yet";
                return null;
            }
            if (e.Status != "done" || e.Png == null || e.Png.Length == 0)
            {
                why = "shot " + e.Id + " is " + e.Status
                    + (string.IsNullOrEmpty(e.Error) ? "" : " (" + e.Error + ")");
                return null;
            }
            return e.Png;
        }

        /// <summary>Main thread, once per frame: start the grab, or give up on it.</summary>
        internal static void Tick()
        {
            Entry e = _current;
            if (e == null) return;
            // "capturing" has to stay visible here. Refusing to look at it made the wedged
            // coroutine case unrecoverable: _current stayed capturing forever, every take
            // answered 409 shot_busy, and the only way out was restarting the game.
            if (e.Status != "queued" && e.Status != "capturing") return;

            int age = Math.Max(0, Environment.TickCount - e.StartedTick);
            if (age > TimeoutMs)
            {
                Fail(e, "shot_timeout", e.Started
                    ? "the capture did not complete within " + TimeoutMs + " ms"
                    : "no frame was captured within " + TimeoutMs + " ms");
                return;
            }

            if (!e.Started)
            {
                if (_host == null)
                {
                    Fail(e, "shot_unavailable", "the bridge behaviour is not available");
                    return;
                }
                e.Started = true;
                e.Status = "capturing";
                try { _host.StartCoroutine(Grab(e)); }
                catch (Exception ex) { Fail(e, "shot_failed", ex.GetType().Name + ": " + ex.Message); }
            }
        }

        /// <summary>Drops a stuck capture so a caller does not have to restart the game.</summary>
        internal static JObject Reset()
        {
            Entry e = _current;
            if (e == null || e.Status == "done")
            {
                JObject idle = Status();
                idle["reset"] = false;
                idle["reason"] = "nothing was in flight";
                return idle;
            }
            Core.Log("shot " + e.Id + " reset by request while " + e.Status);
            e.Status = "error";
            e.Error = "shot_reset: the capture was abandoned by request";
            e.Cancelled = true;
            // Keep _current so the reset stays visible in GET /shot: reporting it as idle
            // would hide the very state the caller asked us to clear.
            JObject o = Status();
            o["reset"] = true;
            o["abandoned"] = e.Id;
            return o;
        }

        private static void Fail(Entry e, string code, string why)
        {
            e.Status = "error";
            e.Error = code + ": " + why;
            Core.Log("shot " + e.Id + " failed: " + e.Error);
            Record(e, "failed");
        }

        private static IEnumerator Grab(Entry e)
        {
            // Unity requires the capture to happen after the frame finished rendering.
            // Reading or capturing during Update either throws or yields a stale frame.
            yield return new WaitForEndOfFrame();

            // An explicit reset can land while this coroutine is parked on the yield. Bail
            // out before touching the entry, otherwise the "abandoned" shot would quietly
            // become done and Png() would hand out a frame nobody asked for any more.
            if (e.Cancelled) yield break;

            Texture2D shot = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null)
                {
                    Fail(e, "shot_unavailable", "Unity returned no texture for the current frame");
                    yield break;
                }

                e.TexWidth = shot.width;
                e.TexHeight = shot.height;
                e.Frame = Time.frameCount;
                e.SinceStart = Time.realtimeSinceStartup;

                if (shot.width < 1 || shot.height < 1)
                {
                    Fail(e, "shot_unavailable",
                        "the game reports a " + shot.width + "x" + shot.height + " frame");
                    yield break;
                }

                Texture2D outTex = shot;
                bool scaled = false;
                if (!e.WantsFull && shot.width > ReadWidth)
                {
                    // Shrink for reading. Never enlarge: an upscaled screenshot invents
                    // detail that was not rendered, which is worse than a small picture.
                    int w = ReadWidth;
                    int h = Math.Max(1, (int)Math.Round((double)shot.height * ReadWidth / shot.width));
                    outTex = Downscale(shot, w, h);
                    scaled = outTex != shot;
                }

                byte[] png = ImageConversion.EncodeToPNG(outTex);
                if (png == null || png.Length == 0)
                {
                    Fail(e, "shot_failed", "the PNG encoder returned nothing");
                    yield break;
                }

                e.Width = outTex.width;
                e.Height = outTex.height;
                e.Bytes = png.Length;
                e.Png = png;
                e.Status = "done";

                if (e.WantsSave && !string.IsNullOrEmpty(_dir))
                {
                    try
                    {
                        // The file on disk is a record, not what the agent reads: when
                        // ShotSaveFull is on, encode the untouched texture for the file and
                        // keep handing out the small one. Silently saving only the small copy
                        // is how "save the screenshot" ends up meaning "save the 1024 preview".
                        byte[] savePng = png;
                        int saveW = outTex.width;
                        int saveH = outTex.height;
                        bool saveFull = WantsFullResOnDisk();
                        if (saveFull && outTex != shot)
                        {
                            byte[] big = ImageConversion.EncodeToPNG(shot);
                            if (big != null && big.Length > 0)
                            {
                                savePng = big;
                                saveW = shot.width;
                                saveH = shot.height;
                            }
                        }

                        if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                        string name = "shot_" + e.Id.ToString("0000") + "_"
                            + (e.Frame.ToString()) + "x" + saveW + "x" + saveH + ".png";
                        string full = Path.Combine(_dir, name);
                        File.WriteAllBytes(full, savePng);
                        e.SavedPath = full;
                        e.SavedWidth = saveW;
                        e.SavedHeight = saveH;
                        e.SavedBytes = savePng.Length;
                    }
                    catch (Exception ex)
                    {
                        e.Error = "saved: false, " + ex.GetType().Name + ": " + ex.Message;
                    }
                }

                if (outTex != shot && outTex != null) UnityEngine.Object.Destroy(outTex);

                Core.Log("shot " + e.Id + " done: texture " + e.TexWidth + "x" + e.TexHeight
                    + " -> " + e.Width + "x" + e.Height + (scaled ? " (scaled)" : " (full)")
                    + ", " + e.Bytes + " bytes, frame " + e.Frame
                    + (string.IsNullOrEmpty(e.SavedPath) ? "" : ", saved " + e.SavedPath));

                Record(e, "done");
            }
            catch (Exception ex)
            {
                Fail(e, "shot_failed", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (shot != null)
                {
                    try { UnityEngine.Object.Destroy(shot); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>Bilinear shrink on the CPU. Fine for an occasional diagnostic frame.</summary>
        private static Texture2D Downscale(Texture2D src, int w, int h)
        {
            Color32[] from = src.GetPixels32();
            Color32[] to = new Color32[w * h];
            int sw = src.width;
            int sh = src.height;

            for (int y = 0; y < h; y++)
            {
                float fy = ((y + 0.5f) * sh / h) - 0.5f;
                if (fy < 0f) fy = 0f;
                int y0 = (int)fy;
                int y1 = y0 + 1 < sh ? y0 + 1 : sh - 1;
                float ay = fy - y0;

                for (int x = 0; x < w; x++)
                {
                    float fx = ((x + 0.5f) * sw / w) - 0.5f;
                    if (fx < 0f) fx = 0f;
                    int x0 = (int)fx;
                    int x1 = x0 + 1 < sw ? x0 + 1 : sw - 1;
                    float ax = fx - x0;

                    Color32 c00 = from[y0 * sw + x0];
                    Color32 c10 = from[y0 * sw + x1];
                    Color32 c01 = from[y1 * sw + x0];
                    Color32 c11 = from[y1 * sw + x1];

                    Color32 o = new Color32();
                    o.r = Mix(c00.r, c10.r, c01.r, c11.r, ax, ay);
                    o.g = Mix(c00.g, c10.g, c01.g, c11.g, ax, ay);
                    o.b = Mix(c00.b, c10.b, c01.b, c11.b, ax, ay);
                    o.a = 255;
                    to[y * w + x] = o;
                }
            }

            Texture2D dst = new Texture2D(w, h, TextureFormat.RGB24, false);
            dst.SetPixels32(to);
            dst.Apply();
            return dst;
        }

        private static byte Mix(byte c00, byte c10, byte c01, byte c11, float ax, float ay)
        {
            float top = c00 + (c10 - c00) * ax;
            float bottom = c01 + (c11 - c01) * ax;
            float v = top + (bottom - top) * ay;
            if (v < 0f) v = 0f;
            if (v > 255f) v = 255f;
            return (byte)v;
        }

        private static void Record(Entry e, string status)
        {
            // Metadata only. The picture itself must never go into the event buffer: it
            // would multiply memory per event, and reading it back would hand the inspector
            // a wall of bytes instead of a readable line.
            try
            {
                int ms = Math.Max(0, Environment.TickCount - e.StartedTick);
                string summary = "shot " + e.Id + " " + status + ": texture " + e.TexWidth + "x"
                    + e.TexHeight + " -> " + e.Width + "x" + e.Height + " (" + e.Bytes + " bytes, frame "
                    + e.Frame + ")";
                InspectorBuffer.Add("Observation", "shot", summary, SessionOf(), null, null,
                    null, null, "shot_" + status, ms, null);
            }
            catch (Exception) { }
        }

        private static string SessionOf()
        {
            try
            {
                return "world-" + SessionRegistry.WorldGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }
    }
}
