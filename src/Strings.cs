using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// Fixed UI text for the inspector window.
    ///
    /// What is NOT translated on purpose: anything the agent produced. Summaries,
    /// request payloads, response bodies, bridge log lines, error codes, unit names and
    /// paths stay raw - they are the record the panel exists to show, and rewriting them
    /// would corrupt the very thing the player is reading. Only chrome the bridge itself
    /// draws goes through this table.
    ///
    /// Where the text lives follows the game's own layout, because a mod that invents a
    /// second convention makes the player learn two:
    ///     &lt;mod&gt;\language_en\ui.ini
    ///     &lt;mod&gt;\language_cn\ui.ini
    /// The folder the request named is accepted as well, searched after the game layout:
    ///     &lt;mod&gt;\localization\en.ini, cn.ini, zh.ini, zh-Hans.ini, zh-CN.ini
    ///
    /// The format is the game's: optional [Section] headers, then Key=Value split on the
    /// FIRST '='. UTF-8, a BOM is tolerated and never written. Lines starting with ';'
    /// or '#' are comments. A duplicated key is last-wins and reported in /state.
    ///
    /// Language is decided from the mod's own setting.ini, then the game's own
    /// usersettings.ini (the player already picked there), then the OS, then English.
    /// A key missing from the chosen table falls back per key: chosen file -> English ->
    /// the key name itself. It never falls back on the whole language because one key
    /// was missing.
    /// </summary>
    internal static class Strings
    {
        private const string DefaultLanguage = "en";
        private const string Section = "AgentInspector";

        private static readonly Dictionary<string, string> BuiltinEn = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> BuiltinZh = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> En = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<string> Notes = new List<string>();

        private static string _lang = DefaultLanguage;   // "en" or "zh"
        private static string _from = "builtin";         // setting | game | system | request | builtin
        private static string _tag = DefaultLanguage;    // the raw tag that decided it
        private static string _enFile = "";
        private static string _zhFile = "";
        private static string _modDir = "";
        private static int _enKeys;
        private static int _zhKeys;
        private static int _enFileKeys;
        private static int _zhFileKeys;
        private static bool _ready;

        static Strings()
        {
            Define(BuiltinEn, "title", "Agent Inspector");
            Define(BuiltinEn, "actions", "input & actions - commands the agent issued");
            Define(BuiltinEn, "actions.empty", "no agent commands yet");
            Define(BuiltinEn, "detail.select", "select a row above to read the full event");
            Define(BuiltinEn, "detail.page", "lines {0}-{1} of {2} - scroll to page");
        Define(BuiltinEn, "detail.pages", "page {0} of {1}   lines {2}-{3} of {4} - scroll to page");
            Define(BuiltinEn, "detail.capped", "(body truncated at the display cap)");
            Define(BuiltinEn, "status.starting", "inspector");
            Define(BuiltinEn, "status.line",
                "events {0}   shown {1}   cache {2}MB   dropped {3}   {4}   toggle {5}");
            Define(BuiltinEn, "status.hold_on", "hold on");
            Define(BuiltinEn, "status.hold_off", "hold off");

            // TR2-H page chrome. Same section, "comms." prefix: one table, one fallback
            // chain, and a missing key still shows itself instead of vanishing.
            Define(BuiltinEn, "comms.title", "HARNESS COMMUNICATION");
            Define(BuiltinEn, "comms.channel", "AGENT CHANNEL");
            Define(BuiltinEn, "comms.order", "CURRENT ORDER");
            Define(BuiltinEn, "comms.you", "YOU");
            Define(BuiltinEn, "comms.agent", "AGENT");
            Define(BuiltinEn, "comms.empty", "no messages yet - say something below");
            Define(BuiltinEn, "comms.hint", "type a message and press Enter");
            Define(BuiltinEn, "comms.noOrders", "NO ACTIVE ORDERS");
            Define(BuiltinEn, "comms.send", "SEND");
            Define(BuiltinEn, "comms.queued", "queued #{0} for the harness");
            Define(BuiltinEn, "comms.notWritten", "NOT WRITTEN - {0}");
            Define(BuiltinEn, "comms.linkUp", "LINK UP");
            Define(BuiltinEn, "comms.linkDown", "LINK DOWN");
            Define(BuiltinEn, "comms.linkNoSession", "NO SESSION");
            Define(BuiltinEn, "comms.running", "running {0}");
            Define(BuiltinEn, "comms.scroll", "scroll {0}/{1}");

            Define(BuiltinZh, "title", "\u667a\u80fd\u4f53\u51b3\u7b56\u68c0\u67e5\u5668");
            Define(BuiltinZh, "actions", "\u8f93\u5165\u4e0e\u64cd\u4f5c \u2014 \u667a\u80fd\u4f53\u4e0b\u8fbe\u8fc7\u7684\u547d\u4ee4");
            Define(BuiltinZh, "actions.empty", "\u667a\u80fd\u4f53\u5c1a\u672a\u4e0b\u8fbe\u547d\u4ee4");
            Define(BuiltinZh, "detail.select", "\u9009\u4e2d\u4e0a\u65b9\u4e00\u884c\uff0c\u67e5\u770b\u8be5\u4e8b\u4ef6\u7684\u5b8c\u6574\u5185\u5bb9");
            Define(BuiltinZh, "detail.page", "\u7b2c {0}-{1} \u884c / \u5171 {2} \u884c \u2014 \u6eda\u52a8\u7ffb\u9875");
        Define(BuiltinZh, "detail.pages", "\u7b2c {0}/{1} \u9875   \u7b2c {2}-{3} \u884c / \u5171 {4} \u884c \u2014 \u6eda\u52a8\u7ffb\u9875");
            Define(BuiltinZh, "detail.capped", "\uff08\u6b63\u6587\u8d85\u51fa\u663e\u793a\u4e0a\u9650\uff0c\u5df2\u622a\u65ad\uff09");
            Define(BuiltinZh, "status.starting", "\u68c0\u67e5\u5668");
            Define(BuiltinZh, "status.line",
                "\u4e8b\u4ef6 {0}   \u663e\u793a {1}   \u7f13\u5b58 {2}MB   \u4e22\u5f03 {3}   {4}   \u5207\u6362\u952e {5}");
            Define(BuiltinZh, "status.hold_on", "\u53ea\u8bfb \u5f00");
            Define(BuiltinZh, "status.hold_off", "\u53ea\u8bfb \u5173");

            Define(BuiltinZh, "comms.title", "\u667a\u80fd\u4f53\u901a\u4fe1");
            Define(BuiltinZh, "comms.channel", "\u667a\u80fd\u4f53\u4fe1\u9053");
            Define(BuiltinZh, "comms.order", "\u5f53\u524d\u6307\u4ee4");
            Define(BuiltinZh, "comms.you", "\u73a9\u5bb6");
            Define(BuiltinZh, "comms.agent", "\u667a\u80fd\u4f53");
            Define(BuiltinZh, "comms.empty", "\u6682\u65e0\u6d88\u606f \u2014 \u5728\u4e0b\u65b9\u8f93\u5165");
            Define(BuiltinZh, "comms.hint", "\u8f93\u5165\u5185\u5bb9\u540e\u6309 Enter \u53d1\u9001");
            Define(BuiltinZh, "comms.noOrders", "\u65e0\u6d3b\u52a8\u6307\u4ee4");
            Define(BuiltinZh, "comms.send", "\u53d1\u9001");
            Define(BuiltinZh, "comms.queued", "\u5df2\u6392\u961f #{0}\uff0c\u7b49\u5f85\u667a\u80fd\u4f53\u53d6\u8d70");
            Define(BuiltinZh, "comms.notWritten", "\u672a\u5199\u5165 \u2014 {0}");
            Define(BuiltinZh, "comms.linkUp", "\u8fde\u63a5\u5df2\u5efa\u7acb");
            Define(BuiltinZh, "comms.linkDown", "\u8fde\u63a5\u65ad\u5f00");
            Define(BuiltinZh, "comms.linkNoSession", "\u65e0\u4f1a\u8bdd");
            Define(BuiltinZh, "comms.running", "\u6267\u884c\u4e2d {0}");
            Define(BuiltinZh, "comms.scroll", "\u6eda\u52a8 {0}/{1}");

            // Settings page and its shell tab.
            Define(BuiltinEn, "console.inspector", "AGENT INSPECTOR");
            Define(BuiltinZh, "console.inspector", "决策记录");
            Define(BuiltinEn, "console.comms", "HARNESS COMMS");
            Define(BuiltinZh, "console.comms", "顾问通联");
            Define(BuiltinEn, "console.settings", "SETTINGS");
            Define(BuiltinZh, "console.settings", "设置");
            Define(BuiltinEn, "settings.title", "Player settings");
            Define(BuiltinZh, "settings.title", "玩家设置");
            Define(BuiltinEn, "settings.language", "Interface language");
            Define(BuiltinZh, "settings.language", "界面语言");
            Define(BuiltinEn, "settings.auto", "Follow game");
            Define(BuiltinZh, "settings.auto", "跟随游戏");
            Define(BuiltinEn, "settings.language.note", "Click to cycle: game / English / Chinese. Apply after saving.");
            Define(BuiltinZh, "settings.language.note", "点击切换：跟随游戏 / English / 简体中文。保存后应用。");
            Define(BuiltinEn, "settings.compact", "Compact MCP replies");
            Define(BuiltinZh, "settings.compact", "精简 MCP 回复");
            Define(BuiltinEn, "settings.compact.note", "Reduce fields in large state replies. Takes effect on the next query.");
            Define(BuiltinZh, "settings.compact.note", "减少大型态势回复的字段，从下次查询生效。");
            Define(BuiltinEn, "settings.mainOnly", "Use main agent only");
            Define(BuiltinZh, "settings.mainOnly", "仅使用主 Agent 操作");
            Define(BuiltinEn, "settings.mainOnly.note", "New tickets run in the main turn, without workers. May improve results but require more thinking time. Off by default.");
            Define(BuiltinZh, "settings.mainOnly.note", "新任务仍建单，由主 Agent 执行全部交互。或许效果更好，但思考耗时更长。默认关闭。");
            Define(BuiltinEn, "settings.shots", "Save screenshots at original resolution");
            Define(BuiltinZh, "settings.shots", "截图保存原始分辨率");
            Define(BuiltinEn, "settings.shots.note", "Save full-size images locally; images sent to the advisor stay small.");
            Define(BuiltinZh, "settings.shots.note", "本地截图保留原始分辨率；传给顾问的图像仍使用缩小版本。");
            Define(BuiltinEn, "settings.save", "Save");
            Define(BuiltinZh, "settings.save", "保存");
            Define(BuiltinEn, "settings.reload", "Discard / reload");
            Define(BuiltinZh, "settings.reload", "放弃修改 / 重新读取");
            Define(BuiltinEn, "settings.ready", "Preferences persist between missions. Changes require Save.");
            Define(BuiltinZh, "settings.ready", "设置跨对局保留，修改后需要点击保存。");
            Define(BuiltinEn, "settings.unsaved", "Unsaved changes");
            Define(BuiltinZh, "settings.unsaved", "修改尚未保存");
            Define(BuiltinEn, "settings.saved", "Saved. Applies immediately or on the next query / capture.");
            Define(BuiltinZh, "settings.saved", "已保存，立即或在下次查询 / 截图时生效。");
            Define(BuiltinEn, "settings.loadFailed", "Could not read settings; reload to try again.");
            Define(BuiltinZh, "settings.loadFailed", "无法读取设置，请重新读取后再试。");
            Define(BuiltinEn, "settings.saveFailed", "Could not apply settings. Your changes remain on this page.");
            Define(BuiltinZh, "settings.saveFailed", "设置应用失败，修改仍保留在本页。");
            Define(BuiltinEn, "settings.menuKey", "Menu open / close key");
            Define(BuiltinZh, "settings.menuKey", "菜单打开 / 关闭键");
            Define(BuiltinEn, "settings.pressKey", "Press a key…");
            Define(BuiltinZh, "settings.pressKey", "请按下新键…");
            Define(BuiltinEn, "settings.capture", "Press a letter, number or F1–F12. Escape cancels.");
            Define(BuiltinZh, "settings.capture", "请按字母、数字或 F1–F12；Esc 取消录制。");
            Define(BuiltinEn, "settings.menuKey.note", "Click to bind a single letter, number or F1–F12. Save to apply. Choose a key unused by game shortcuts.");
            Define(BuiltinZh, "settings.menuKey.note", "点击录制单个字母、数字或 F1–F12，保存后生效。请选择未被游戏快捷键占用的按键。");
        }

        private static void Define(Dictionary<string, string> table, string key, string value)
        {
            table[Section + "/" + key] = value;
        }

        // ---------------------------------------------------------------- lookups

        internal static string T(string key)
        {
            EnsureLoaded();
            string value;
            if (_lang == "zh" && TryGet(Zh, key, out value)) return value;
            if (TryGet(En, key, out value)) return value;
            return key;
        }

        /// <summary>
        /// Formatted lookup. A malformed template must not take the panel down, so the
        /// English template is tried before giving up on the key name.
        /// </summary>
        internal static string F(string key, params object[] args)
        {
            string template = T(key);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                string english;
                if (_lang == "zh" && TryGet(En, key, out english))
                {
                    try { return string.Format(CultureInfo.InvariantCulture, english, args); }
                    catch (FormatException) { }
                }
                Note("template '" + key + "' does not match its arguments");
                return template;
            }
        }

        private static bool TryGet(Dictionary<string, string> table, string key, out string value)
        {
            if (table.TryGetValue(Section + "/" + key, out value)) return true;
            if (table.TryGetValue(key, out value)) return true;
            value = null;
            return false;
        }

        // ---------------------------------------------------------------- language

        internal static void EnsureLoaded()
        {
            if (_ready) return;
            _ready = true;
            string modDir = "";
            try { modDir = Core.ModDir(); }
            catch (Exception ex) { Note("mod dir unknown: " + ex.GetType().Name); }
            ResolveLanguage(modDir);
            Load(modDir);
        }

        /// <summary>
        /// Change the language at runtime. Returns true when the table changed, so the
        /// caller knows whether the window has to be repainted. The window is not torn
        /// down: the text nodes it already built are re-labelled in place.
        /// </summary>
        internal static bool SetLanguage(string tag)
        {
            EnsureLoaded();
            if (tag == null || tag.Trim().Length == 0)
                throw new ApiException(400, "missing_field",
                    "language is required, for example \"en\", \"zh\", or \"auto\"", false);

            string low = tag.Trim().ToLowerInvariant();
            if (low == "auto" || low == "game" || low == "default")
            {
                // Hand the decision back to the chain. Without this a runtime override
                // sticks until the process restarts, so a player who tried "en" once
                // could never get their own language back without closing the game.
                string before = _lang;
                ResolveLanguage(_modDir);
                Load(_modDir);
                bool moved = _lang != before;
                Core.Log("inspector language -> " + _lang + " (auto, from " + _from + ", "
                    + (moved ? "changed" : "unchanged") + ")");
                return moved;
            }

            string normalized = Normalize(tag);
            if (normalized == null)
            {
                throw new ApiException(400, "bad_language",
                    "unknown language tag '" + tag + "'; use en, or one of zh / cn / zh-CN /"
                    + " zh-Hans, or auto to follow the game", false);
            }

            bool changed = normalized != _lang;
            _lang = normalized;
            _tag = tag;
            _from = "request";

            // Re-read the files. An external table exists so the player can edit it, and
            // a switch is exactly when they expect that edit to land - switching between
            // two tables already in memory would quietly ignore their change.
            Load(_modDir);

            Core.Log("inspector language -> " + _lang + " (tag '" + tag + "', "
                + (changed ? "changed" : "unchanged") + ")");
            return changed;
        }

        private static void ResolveLanguage(string modDir)
        {
            string tag = null;

            // 1. an explicit choice in the mod's own setting.ini wins over everything.
            if (!string.IsNullOrEmpty(modDir))
            {
                string ini = Path.Combine(modDir, "setting.ini");
                if (File.Exists(ini))
                {
                    try
                    {
                        Dictionary<string, string> table = ParseFile(ini, "setting.ini");
                        string value;
                        bool found = table.TryGetValue(Section + "/Language", out value);
                        if (!found) found = table.TryGetValue("Language", out value);
                        if (found && Normalize(value) != null)
                        {
                            tag = value;
                            _from = "setting";
                        }
                        else if (found && !string.IsNullOrEmpty(value == null ? null : value.Trim()))
                        {
                            // An EMPTY value is not an unknown language: it is how the file
                            // says "follow the game" (see Merge's comment). Writing a note
                            // about the default is noise that reads like a broken setting.
                            Note("setting.ini Language='" + value + "' is not one we have; ignoring it");
                        }
                    }
                    catch (Exception ex) { Note("setting.ini unreadable: " + ex.GetType().Name); }
                }
            }

            // 2. the game's own language - the player already answered this question.
            if (tag == null)
            {
                string game = ReadGameLanguage();
                if (game != null)
                {
                    if (Normalize(game) != null)
                    {
                        tag = game;
                        _from = "game";
                    }
                    else
                    {
                        Note("game language '" + game + "' has no table here; falling back");
                    }
                }
            }

            // 3. the operating system.
            if (tag == null && Core.IsMain)
            {
                try
                {
                    string system = Application.systemLanguage.ToString();
                    if (system.StartsWith("Chinese", StringComparison.Ordinal))
                    {
                        tag = "zh";
                        _from = "system";
                    }
                    else if (system.StartsWith("English", StringComparison.Ordinal))
                    {
                        tag = "en";
                        _from = "system";
                    }
                }
                catch (Exception) { }
            }

            if (tag == null)
            {
                _lang = DefaultLanguage;
                _tag = DefaultLanguage;
                _from = "builtin";
            }
            else
            {
                _tag = tag;
                _lang = Normalize(tag);
            }
        }

        /// <summary>
        /// Unity only: must never run off the main thread. Reading Application state from
        /// a background thread has already killed this process once.
        /// </summary>
        private static string ReadGameLanguage()
        {
            if (!Core.IsMain) return null;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "usersettings.ini");
                if (!File.Exists(path)) return null;
                Dictionary<string, string> table = ParseFile(path, "usersettings.ini");
                string value;
                if (table.TryGetValue("Language", out value)) return value;
                if (table.TryGetValue("Settings/Language", out value)) return value;
                foreach (KeyValuePair<string, string> kv in table)
                {
                    if (kv.Key.EndsWith("/Language", StringComparison.Ordinal)) return kv.Value;
                }
            }
            catch (Exception ex) { Note("the game language could not be read: " + ex.GetType().Name); }
            return null;
        }

        /// <summary>
        /// BCP 47 primary subtag, plus the aliases the game and the request both use.
        /// Only two tables exist, so region and script are accepted and then discarded -
        /// which is why zh-Hant is reported as zh and never claimed as traditional.
        /// </summary>
        private static string Normalize(string tag)
        {
            if (tag == null) return null;
            string t = tag.Trim().ToLowerInvariant().Replace('_', '-');
            if (t.Length == 0) return null;
            int dash = t.IndexOf('-');
            string primary = dash > 0 ? t.Substring(0, dash) : t;

            if (primary == "en" || primary == "english") return "en";
            if (primary == "zh" || primary == "cn" || primary == "chs" ||
                primary == "chinese") return "zh";
            return null;
        }

        // ---------------------------------------------------------------- files

        private static readonly string[] EnCandidates =
        {
            Path.Combine("language_en", "ui.ini"),
            Path.Combine("localization", "en.ini"),
            Path.Combine("localization", "en-US.ini")
        };

        private static readonly string[] ZhCandidates =
        {
            Path.Combine("language_cn", "ui.ini"),
            Path.Combine("language_zh", "ui.ini"),
            Path.Combine("localization", "cn.ini"),
            Path.Combine("localization", "zh.ini"),
            Path.Combine("localization", "zh-Hans.ini"),
            Path.Combine("localization", "zh-CN.ini")
        };

        private static void Load(string modDir)
        {
            _modDir = modDir;
            En.Clear();
            foreach (KeyValuePair<string, string> kv in BuiltinEn) En[kv.Key] = kv.Value;
            Zh.Clear();
            foreach (KeyValuePair<string, string> kv in BuiltinZh) Zh[kv.Key] = kv.Value;

            _enFile = "";
            _zhFile = "";

            if (!string.IsNullOrEmpty(modDir))
            {
                int enFromFile = 0;
                int zhFromFile = 0;
                _enFile = OverlayFile(modDir, EnCandidates, En, "en", out enFromFile);
                _zhFile = OverlayFile(modDir, ZhCandidates, Zh, "zh", out zhFromFile);
                _enFileKeys = enFromFile;
                _zhFileKeys = zhFromFile;
                EnsureDefaults(modDir);
            }

            _enKeys = LogicalCount(En);
            _zhKeys = LogicalCount(Zh);
        }

        /// <summary>
        /// One key per line, not one dictionary entry per line. A file key under a
        /// [Section] header is stored twice - qualified and bare - so the lookup can
        /// honour a header without losing a flat file, and a raw Count would report
        /// double what the player wrote.
        /// </summary>
        private static int LogicalCount(Dictionary<string, string> table)
        {
            int n = 0;
            foreach (KeyValuePair<string, string> kv in table)
            {
                if (kv.Key.StartsWith(Section + "/", StringComparison.Ordinal)) continue;
                n++;
            }
            return n;
        }

        private static string OverlayFile(string modDir, string[] candidates,
            Dictionary<string, string> table, string language, out int fileKeys)
        {
            fileKeys = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                string path = Path.Combine(modDir, candidates[i]);
                if (!File.Exists(path)) continue;
                try
                {
                    Dictionary<string, string> parsed = ParseFile(path, language + " table");
                    foreach (KeyValuePair<string, string> kv in parsed) table[kv.Key] = kv.Value;
                    fileKeys = LogicalCount(parsed);
                    Core.Log("inspector strings " + language + " <- " + candidates[i]
                        + " (" + fileKeys + " keys)");
                    return candidates[i];
                }
                catch (Exception ex)
                {
                    Note(candidates[i] + " could not be read: " + ex.GetType().Name);
                }
            }
            Core.Log("inspector strings " + language
                + " <- built in (no file under " + modDir + ")");
            return "";
        }

        /// <summary>
        /// Write the defaults out once, and never over a file the player may have edited:
        /// a hand-tuned translation that gets overwritten on the next launch is worse than
        /// no file at all.
        /// </summary>
        private static void EnsureDefaults(string modDir)
        {
            WriteDefault(modDir, Path.Combine("language_en", "ui.ini"), BuiltinEn,
                "English");
            WriteDefault(modDir, Path.Combine("language_cn", "ui.ini"), BuiltinZh,
                "\u4e2d\u6587");
        }

        private static void WriteDefault(string modDir, string relative,
            Dictionary<string, string> table, string caption)
        {
            try
            {
                string path = Path.Combine(modDir, relative);
                if (File.Exists(path)) return;

                StringBuilder text = new StringBuilder();
                text.Append("; agent inspector fixed text (").Append(caption).Append(")\r\n");
                text.Append("; Only chrome the bridge draws. Agent output, log lines, error\r\n");
                text.Append("; codes and unit names are shown raw and are not translated.\r\n");
                text.Append("; Key=Value, split on the first '='. A missing key falls back to\r\n");
                text.Append("; the English file, then to the key name. This file is only\r\n");
                text.Append("; written when it is missing - edit it freely.\r\n");
                text.Append("[").Append(Section).Append("]\r\n");
                foreach (KeyValuePair<string, string> kv in table)
                {
                    string key = kv.Key;
                    if (key.StartsWith(Section + "/", StringComparison.Ordinal))
                        key = key.Substring(Section.Length + 1);
                    text.Append(key).Append('=').Append(kv.Value).Append("\r\n");
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                Core.Log("inspector strings: wrote default " + relative);
            }
            catch (Exception ex)
            {
                Note(relative + " could not be written: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Reads the game's ini shape. Section headers are optional; a key under a header
        /// is stored both qualified ("Section/Key") and bare ("Key"), so the shipped files
        /// and a hand-written flat file both work. A duplicate is last-wins and reported.
        /// </summary>
        private static Dictionary<string, string> ParseFile(string path, string what)
        {
            Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
            string section = "";
            int duplicates = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;
                if (section.Length > 0) table[section + "/" + key] = value;
                if (table.ContainsKey(key) && table[key] != value) duplicates++;
                table[key] = value;
            }
            if (duplicates > 0) Note(what + ": " + duplicates + " duplicate key(s), last wins");
            return table;
        }

        private static void Note(string message)
        {
            if (Notes.Count < 32) Notes.Add(message);
            Core.Log("inspector strings: " + message);
        }

        // ---------------------------------------------------------------- readout

        internal static JObject Report()
        {
            EnsureLoaded();
            JObject o = new JObject();
            o["language"] = _lang;
            o["from"] = _from;
            o["tag"] = _tag;
            o["enFile"] = _enFile;
            o["zhFile"] = _zhFile;
            o["enKeys"] = _enKeys;
            o["zhKeys"] = _zhKeys;
            o["enFileKeys"] = _enFileKeys;
            o["zhFileKeys"] = _zhFileKeys;
            JArray notes = new JArray();
            for (int i = 0; i < Notes.Count; i++) notes.Add(Notes[i]);
            o["notes"] = notes;
            return o;
        }
    }
}
