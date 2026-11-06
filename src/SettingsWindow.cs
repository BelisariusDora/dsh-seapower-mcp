using System;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAgentBridge
{
    // Uses the same painted Border/TextBlock controls as the proven console tabs.
    // Native CheckBox and ScrollViewer templates are not guaranteed in this game view.
    public static class SettingsWindow
    {
        private static Grid _wrap;
        private static Panel _host;
        private static PlayerSettingsValues _draft;
        private static bool _dirty;
        private static int _page;
        private const int OptionCount = 5;
        private static int _perPage = OptionCount;
        private static bool _capturingKey;
        public static bool IsCapturingKey { get { return _capturingKey; } }
        private static string _status = "settings.ready";
        private static string _error = "";
        private static float _width, _height;

        public static bool IsInstalled { get { return _wrap != null; } }

        public static JObject State()
        {
            JObject state = new JObject();
            state["installed"] = IsInstalled;
            state["dirty"] = _dirty;
            state["capturingKey"] = _capturingKey;
            state["page"] = _page + 1;
            state["pages"] = (OptionCount + _perPage - 1) / _perPage;
            state["draft"] = _draft == null ? null : _draft.ToJson();
            state["status"] = _status;
            state["lastError"] = _error;
            return state;
        }

        public static JObject Install(Panel content)
        {
            if (content == null) throw new InvalidOperationException("Settings requires the console content well");
            _host = content;
            if (_draft == null || !_dirty) Reload();
            _wrap = new Grid();
            _wrap.Name = "DshSettingsPage";
            _wrap.ClipToBounds = true;
            _wrap.HorizontalAlignment = HorizontalAlignment.Left;
            _wrap.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(_wrap);
            _width = _height = -1;
            FillContentArea(content);
            return State();
        }

        public static void Remove()
        {
            CancelCapture();
            if (_wrap != null)
            {
                Panel parent = _wrap.Parent as Panel;
                if (parent != null) parent.Children.Remove(_wrap);
            }
            _wrap = null;
            _host = null;
        }

        internal static void FillContentArea(Panel content)
        {
            if (_wrap == null || content == null) return;
            float w = Math.Max(1, content.ActualWidth), h = Math.Max(1, content.ActualHeight);
            if (Math.Abs(w - _width) < .5f && Math.Abs(h - _height) < .5f) return;
            _width = w; _height = h;
            _wrap.Width = w; _wrap.Height = h;
            _perPage = Math.Max(1, Math.Min(OptionCount, (int)((h - 120) / 82)));
            _page = Math.Min(_page, (OptionCount + _perPage - 1) / _perPage - 1);
            Paint();
        }

        internal static void CancelCapture()
        {
            bool wasCapturing = _capturingKey;
            _capturingKey = false;
            if (_status == "settings.capture")
                _status = _dirty ? "settings.unsaved" : "settings.ready";
            if (wasCapturing && _wrap != null) Paint();
        }

        internal static bool PollKeyCapture()
        {
            if (!_capturingKey || _wrap == null || _draft == null) return false;
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
            { CancelCapture(); Paint(); return true; }
            if (!UnityEngine.Input.anyKeyDown) return true;
            foreach (UnityEngine.KeyCode candidate in Enum.GetValues(typeof(UnityEngine.KeyCode)))
            {
                string parsed;
                if (!PlayerSettings.TryMenuKey(candidate.ToString(), out parsed)) continue;
                if (!UnityEngine.Input.GetKeyDown(candidate)) continue;
                _draft.MenuToggleKey = parsed;
                Changed();
                return true;
            }
            return true;
        }

        private static void Reload()
        {
            try
            {
                CancelCapture();
                _draft = PlayerSettings.Load(Core.ModDir());
                ConsoleHost.SetToggleKey(_draft.MenuToggleKey);
                _dirty = false; _status = "settings.ready"; _error = "";
            }
            catch (Exception e)
            {
                _status = "settings.loadFailed"; _error = e.Message;
                // A failed read must not silently overwrite an unreadable configuration.
                _draft = null;
            }
        }

        private static TextBlock Text(string text, float size)
        {
            return new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
                Foreground = UIBrush.Solid(Color.FromArgb(255, 203, 224, 238)) };
        }

        private static Border Button(string text, Action action)
        {
            Border b = new Border { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 0),
                Background = UIBrush.Solid(Color.FromArgb(255, 19, 46, 65)),
                BorderBrush = UIBrush.Solid(Color.FromArgb(255, 75, 126, 156)), BorderThickness = new Thickness(1) };
            b.Child = Text(text, 12);
            b.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; action(); };
            return b;
        }

        private static void Changed()
        {
            _capturingKey = false;
            _dirty = true; _status = "settings.unsaved"; _error = ""; Paint();
        }

        private static void Save()
        {
            if (_draft == null) return;
            CancelCapture();
            try
            {
                PlayerSettings.Save(Core.ModDir(), _draft);
                ConsoleHost.SetToggleKey(_draft.MenuToggleKey);
                _dirty = false; _status = "settings.saved"; _error = "";
                Strings.SetLanguage(_draft.Language.Length == 0 ? "auto" : _draft.Language);
                InspectorWindow.ApplyLanguage();
            }
            catch (Exception e) { _status = "settings.saveFailed"; _error = e.Message; }
            Paint();
        }

        private static void Paint()
        {
            if (_wrap == null) return;
            _wrap.Children.Clear(); _wrap.RowDefinitions.Clear();
            _wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            _wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            _wrap.Background = UIBrush.Solid(Color.FromArgb(255, 7, 17, 28));
            TextBlock heading = Text(Strings.T("settings.title"), 16);
            heading.Margin = new Thickness(12, 10, 12, 4); _wrap.Children.Add(heading);
            StackPanel body = new StackPanel { Margin = new Thickness(12, 0, 12, 0), ClipToBounds = true };
            Grid.SetRow(body, 1); _wrap.Children.Add(body);
            if (_draft != null)
            {
                for (int i = _page * _perPage; i < Math.Min(OptionCount, (_page + 1) * _perPage); i++)
                {
                    StackPanel option = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                    if (i == 0)
                    {
                        string key = _draft.MenuToggleKey.StartsWith("Alpha") ? _draft.MenuToggleKey.Substring(5) : _draft.MenuToggleKey;
                        option.Children.Add(Button(Strings.T("settings.menuKey") + ": " +
                            (_capturingKey ? Strings.T("settings.pressKey") : key), delegate {
                                _capturingKey = !_capturingKey;
                                _status = _capturingKey ? "settings.capture" : _dirty ? "settings.unsaved" : "settings.ready";
                                _error = ""; Paint(); }));
                        option.Children.Add(Text(Strings.T("settings.menuKey.note"), 11));
                    }
                    if (i == 1)
                    {
                        string tag = _draft.Language == "" ? Strings.T("settings.auto") : _draft.Language == "en" ? "English" : "简体中文";
                        option.Children.Add(Button(Strings.T("settings.language") + ": " + tag, delegate {
                            _draft.Language = _draft.Language == "" ? "en" : _draft.Language == "en" ? "zh" : ""; Changed(); }));
                        option.Children.Add(Text(Strings.T("settings.language.note"), 11));
                    }
                    if (i == 2)
                    {
                        option.Children.Add(Button((_draft.CompactReplies ? "[x] " : "[ ] ") + Strings.T("settings.compact"),
                            delegate { _draft.CompactReplies = !_draft.CompactReplies; Changed(); }));
                        option.Children.Add(Text(Strings.T("settings.compact.note"), 11));
                    }
                    if (i == 3)
                    {
                        option.Children.Add(Button((_draft.ShotSaveFull ? "[x] " : "[ ] ") + Strings.T("settings.shots"),
                            delegate { _draft.ShotSaveFull = !_draft.ShotSaveFull; Changed(); }));
                        option.Children.Add(Text(Strings.T("settings.shots.note"), 11));
                    }
                    if (i == 4)
                    {
                        option.Children.Add(Button((_draft.MainAgentOnly ? "[x] " : "[ ] ") + Strings.T("settings.mainOnly"),
                            delegate { _draft.MainAgentOnly = !_draft.MainAgentOnly; Changed(); }));
                        option.Children.Add(Text(Strings.T("settings.mainOnly.note"), 11));
                    }
                    body.Children.Add(option);
                }
            }
            StackPanel footer = new StackPanel { Margin = new Thickness(12, 2, 12, 4) };
            Grid.SetRow(footer, 2); _wrap.Children.Add(footer);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            if (_draft != null) actions.Children.Add(Button(Strings.T("settings.save"), Save));
            actions.Children.Add(Button(Strings.T("settings.reload"), delegate { Reload(); Paint(); }));
            int pages = (OptionCount + _perPage - 1) / _perPage;
            if (pages > 1)
            {
                actions.Children.Add(Button("<", delegate { _page = Math.Max(0, _page - 1); Paint(); }));
                actions.Children.Add(Button(">", delegate { _page = Math.Min(pages - 1, _page + 1); Paint(); }));
                actions.Children.Add(Text((_page + 1) + " / " + pages, 11));
            }
            footer.Children.Add(actions);
            TextBlock status = Text(Strings.T(_status) + (_error.Length > 0 ? " · " + _error : ""), 11);
            status.Margin = new Thickness(0, 5, 0, 0); footer.Children.Add(status);
        }
    }
}
