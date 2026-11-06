using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    public sealed class PlayerSettingsValues
    {
        public string Language = "";
        public string MenuToggleKey = "N";
        public bool CompactReplies;
        public bool ShotSaveFull;
        public bool MainAgentOnly;

        public JObject ToJson()
        {
            return new JObject(new JProperty("menuToggleKey", MenuToggleKey), new JProperty("language", Language),
                new JProperty("compactReplies", CompactReplies), new JProperty("shotSaveFull", ShotSaveFull), new JProperty("mainAgentOnly", MainAgentOnly));
        }
    }

    // Player preferences; execution mode is frozen by the scheduler at ticket creation.
    // Pure parsing/merging is also exercised against the real built DLL offline.
    public static class PlayerSettings
    {
        public static PlayerSettingsValues Parse(string original)
        {
            List<string> inspectorLines = new List<string>();
            foreach (string line in Lines(original))
            {
                string key, raw;
                inspectorLines.Add(Pair(line, out key, out raw) &&
                    (key.Equals("Language", StringComparison.OrdinalIgnoreCase) || key.Equals("ShotSaveFull", StringComparison.OrdinalIgnoreCase) || key.Equals("ToggleKey", StringComparison.OrdinalIgnoreCase))
                    ? key + "=" + raw : line.TrimStart('\ufeff'));
            }
            InspectorConfig inspector = InspectorSettings.Parse(string.Join("\n", inspectorLines.ToArray()));
            PlayerSettingsValues value = new PlayerSettingsValues();
            string lang = (inspector.Language ?? "").Trim().ToLowerInvariant();
            value.Language = lang == "en" ? "en" :
                (lang == "zh" || lang == "cn" || lang == "zh-cn" || lang == "zh-hans" ? "zh" : "");
            value.ShotSaveFull = inspector.ShotSaveFull;
            string toggle;
            value.MenuToggleKey = TryMenuKey(inspector.ToggleKey, out toggle) ? toggle : "N";
            string section = "";
            foreach (string line in Lines(original))
            {
                string header = Header(line);
                if (header != null) { section = header; continue; }
                string key, raw;
                if (section.Equals("MCP", StringComparison.OrdinalIgnoreCase) && Pair(line, out key, out raw)
                    && key.Equals("CompactReplies", StringComparison.OrdinalIgnoreCase))
                    value.CompactReplies = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (section.Equals("Tickets", StringComparison.OrdinalIgnoreCase) && Pair(line, out key, out raw)
                    && key.Equals("MainAgentOnly", StringComparison.OrdinalIgnoreCase))
                    value.MainAgentOnly = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return value;
        }

        public static PlayerSettingsValues Load(string dir)
        {
            string file = Path.Combine(dir, "setting.ini");
            return Parse(File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "");
        }

        private static string[] Lines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string Header(string line)
        {
            string value = line.Trim().TrimStart('\ufeff');
            if (!value.StartsWith("[")) return null;
            int close = value.IndexOf(']');
            return close > 1 ? value.Substring(1, close - 1).Trim() : null;
        }

        private static bool Pair(string line, out string key, out string value)
        {
            key = value = "";
            string trimmed = line.Trim();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) return false;
            int equal = line.IndexOf('=');
            if (equal < 1) return false;
            key = line.Substring(0, equal).Trim();
            value = line.Substring(equal + 1);
            int comment = value.IndexOfAny(new char[] { ';', '#' });
            if (comment >= 0) value = value.Substring(0, comment);
            value = value.Trim();
            return true;
        }

        private static string Update(string original, string sectionName, string keyName, string value)
        {
            string newline = original.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            List<string> lines = new List<string>(Lines(original));
            bool inSection = false, foundSection = false, written = false;
            int insert = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string header = Header(lines[i]);
                if (header != null)
                {
                    if (inSection && insert < 0) insert = i;
                    inSection = header.Equals(sectionName, StringComparison.OrdinalIgnoreCase);
                    foundSection |= inSection;
                    continue;
                }
                if (!inSection) continue;
                string key, raw;
                if (!Pair(lines[i], out key, out raw) || !key.Equals(keyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (written)
                {
                    // Disable duplicate managed keys rather than leaving a last-wins surprise.
                    lines[i] = "; superseded setting: " + lines[i];
                    continue;
                }
                int equal = lines[i].IndexOf('=');
                string tail = lines[i].Substring(equal + 1);
                int comment = tail.IndexOfAny(new char[] { ';', '#' });
                lines[i] = lines[i].Substring(0, equal + 1) + value;
                // Existing Inspector/Strings readers treat the whole RHS as a value.
                // Keep an inline comment as its own line so all consumers agree.
                if (comment >= 0) { lines.Insert(i, tail.Substring(comment)); i++; }
                written = true;
            }
            if (!written)
            {
                if (!foundSection)
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                    lines.Add("[" + sectionName + "]");
                    lines.Add(keyName + "=" + value);
                }
                else lines.Insert(insert >= 0 ? insert : lines.Count, keyName + "=" + value);
            }
            return string.Join(newline, lines.ToArray());
        }

        // A single keyboard key. Modifiers, mouse buttons, Escape and Enter are not
        // menu bindings: Escape cancels capture and Enter belongs to message input.
        public static bool TryMenuKey(string raw, out string key)
        {
            key = null;
            string value = (raw ?? "").Trim().ToUpperInvariant();
            if (value.Length == 1 && value[0] >= 'A' && value[0] <= 'Z') { key = value; return true; }
            if (value.Length == 1 && value[0] >= '0' && value[0] <= '9') { key = "Alpha" + value; return true; }
            if (value.Length == 6 && value.StartsWith("ALPHA") && value[5] >= '0' && value[5] <= '9')
            { key = "Alpha" + value[5]; return true; }
            int number;
            if (value.StartsWith("F") && int.TryParse(value.Substring(1), out number) && number >= 1 && number <= 12)
            { key = "F" + number; return true; }
            return false;
        }

        public static string Merge(string original, PlayerSettingsValues value)
        {
            if (value.Language != "" && value.Language != "en" && value.Language != "zh")
                throw new ArgumentException("Unsupported UI language");
            string toggle;
            if (!TryMenuKey(value.MenuToggleKey, out toggle)) throw new ArgumentException("Unsupported menu key");
            string result = Update(original ?? "", "AgentInspector", "ToggleKey", toggle);
            result = Update(result, "AgentInspector", "Language", value.Language);
            result = Update(result, "AgentInspector", "ShotSaveFull", value.ShotSaveFull ? "true" : "false");
            result = Update(result, "MCP", "CompactReplies", value.CompactReplies ? "true" : "false");
            return Update(result, "Tickets", "MainAgentOnly", value.MainAgentOnly ? "true" : "false");
        }

        public static void Save(string dir, PlayerSettingsValues value)
        {
            string file = Path.Combine(dir, "setting.ini");
            string original = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
            string merged = Merge(original, value);
            string temporary = file + ".settings-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(merged);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
