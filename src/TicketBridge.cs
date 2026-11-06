using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    // Files are owned by the per-game DSH worker runtime. No Unity objects are read here.
    public static class TicketBridge
    {
        private static readonly object Gate = new object();
        private static string DirectoryPath { get { return Path.Combine(Core.ModDir(), "comms", "run", "tickets"); } }

        public static JArray Snapshot()
        {
            try
            {
                JObject state = JObject.Parse(Read(Path.Combine(DirectoryPath, "view.json")));
                string world = (string)CommsBridge.State()["worldKey"];
                if (world == null || (string)state["worldKey"] != world) return new JArray();
                return state["tickets"] as JArray ?? new JArray();
            }
            catch (Exception) { return new JArray(); }
        }

        private static string Read(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        public static JObject Destroy(string id)
        {
            JObject command = new JObject();
            command["op"] = "destroy";
            command["id"] = id;
            command["requestId"] = Guid.NewGuid().ToString("N");
            return Submit(command, false);
        }

        public static JObject Submit(JObject command, bool wait = true)
        {
            string world = (string)CommsBridge.State()["worldKey"];
            if (world == null) throw new ApiException(409, "no_mission", "Wait for a mission", true);
            string op = (string)command["op"];
            if (op != "create" && op != "destroy" && op != "report")
                throw new ApiException(400, "invalid_operation", "Expected create, destroy or report", false);
            if (command["worldKey"] != null && (string)command["worldKey"] != world)
                throw new ApiException(409, "world_changed", "Ticket belongs to another mission", false);
            string requestId = (string)command["requestId"];
            Guid parsed;
            if (!Guid.TryParse(requestId, out parsed))
                throw new ApiException(400, "invalid_request_id", "requestId must be a UUID", false);
            requestId = parsed.ToString("N");
            command = (JObject)command.DeepClone();
            command["requestId"] = requestId;
            command["worldKey"] = world;
            string text = command.ToString(Newtonsoft.Json.Formatting.None);
            if (Encoding.UTF8.GetByteCount(text) > 32768)
                throw new ApiException(400, "too_large", "Ticket request exceeds 32 KiB", false);
            string fingerprint;
            using (System.Security.Cryptography.SHA256 algorithm = System.Security.Cryptography.SHA256.Create())
                fingerprint = BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            lock (Gate)
            {
                JObject view;
                try { view = JObject.Parse(Read(Path.Combine(DirectoryPath, "view.json"))); }
                catch (Exception) { throw new ApiException(503, "ticket_runtime_offline", "Start the DSH ticket runtime", true); }
                double age = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds - (double)view["at"];
                if (age > 15000 || age < -5000 || (string)view["worldKey"] != world)
                    throw new ApiException(503, "ticket_runtime_offline", "DSH ticket runtime has no current heartbeat", true);
                if (view["available"] != null && (bool)view["available"] == false)
                    throw new ApiException(503, "ticket_context_pending",
                        "Ticket runtime waiting: " + (string)view["blockedReason"], true);
                string inbox = Path.Combine(DirectoryPath, "commands");
                System.IO.Directory.CreateDirectory(inbox);
                string file = Path.Combine(inbox, requestId + ".json");
                if (!File.Exists(file))
                {
                    string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush(true);
                        }
                        File.Move(temp, file);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            if (wait)
            {
                string responseFile = Path.Combine(DirectoryPath, "responses", requestId + ".json");
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(responseFile))
                    {
                        JObject response = JObject.Parse(Read(responseFile));
                        if ((string)response["requestFingerprint"] != fingerprint)
                            throw new ApiException(409, "request_conflict", "requestId belongs to different input", false);
                        response.Remove("requestFingerprint");
                        return response;
                    }
                    System.Threading.Thread.Sleep(50);
                }
                throw new ApiException(504, "ticket_pending", "Request persisted; retry with the same requestId", true);
            }
            JObject result = new JObject();
            result["queued"] = true;
            result["requestId"] = requestId;
            result["worldKey"] = world;
            result["status"] = "queued";
            return result;
        }

        public static JObject State()
        {
            JObject result = new JObject();
            result["tickets"] = Snapshot();
            return result;
        }
    }
}
