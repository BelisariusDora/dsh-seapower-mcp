// TR2-H: the harness communication page.
//
// Layout contract with the owner's brief: an agent column on the left, the current
// order formatted as a naval message on the right, a player input row under the left
// column, and a status strip that never lies about which of the three states it is in
// (link / submission / generation).
//
// Why it is one full-area panel inside DisplayAreaGrid and not a new "page": this
// build has no page manager reachable from a mod, and the game's own SeapowerUI.Window
// Manager needs control templates that cannot be supplied from code. A Grid child with
// a high ZIndex, toggled by its own key, is the shape that is known to render here
// (the inspector proves that path; this page deliberately does not reuse its window
// frame because the brief says the two must be able to open and close independently).
//
// Why every row is rebuilt instead of appended in place: a Border/StackPanel is all
// this build paints reliably, and there is no ScrollViewer template to borrow. The
// inspector already paid for that lesson - paging by offset and rebuilding on a
// signature change is the pattern that works, so it is what this page does.
//
// What this page does NOT do: invent an agent sentence, show hidden reasoning, or claim
// a message was delivered. Text on the left is the tailer's decode of the transcript's
// visible assistant text; a player line is echoed the moment it is queued to the outbox
// and is labelled queued, not sent.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.IO;
using Noesis;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    public static class CommsWindow
    {
        public const string WindowName = "DshCommsPage";

        private const double MinW = 520;
        private const double MinH = 300;
        private const double PortraitSize = 66;
        private const double HeaderH = PortraitSize + 8;
        private const double RowH = 18;
        private const int TranscriptTailRows = 20;
        private const int MaxRowLines = 4;
        private static int _maxRowLines = 6;
        private const double ColSpine = 1;
        private const double AgentColumnShare = 0.65;
        private const double InputH = 30;
        private const double FooterH = 30;
        // float, not double: Noesis Thickness only takes floats, and a double here
        // turned every Padding/Margin into a CS1503 instead of a value.
        private const float Gutter = 8;

        private static Panel _host;
        private static Grid _wrap;
        private static Border _outer;
        private static TextBlock _title;
        private static TextBlock _linkText;
        private static TextBlock _genText;
        private static StackPanel _logPanel;
        private static Border _logViewport;
        private static TextBlock _logEmpty;
        private static TextBlock _logFooter;
        private static StackPanel _orderPanel;
        private static TextBox _input;
        private static Border _sendButton;
        private static TextBlock _sendLabel;
        private static TextBlock _statusText;
        private static TextBlock _badgeText;
        private static Border _stopButton;
        private static double _stopQueuedUntil;
        private static Border _portraitFrame;
        private static Image _portraitImage;
        private static UnityEngine.Texture2D _portraitTexture;
        private static string _target = "dsh";
        private static string _portrait = "Auto";

        private static bool _visible;
        private static bool _armed;
        private static int _boxClickFrame = -1;
        private static int _submitFrame = -1;

        internal static bool ClaimedClickThisFrame
        {
            get { return _armed && _boxClickFrame == UnityEngine.Time.frameCount; }
        }
        private static int _scrollBack;

        // The transcript is laid out in *rows*, not items: one assistant line can wrap to four
        // rows and a tool event adds its own row. Counting scroll in rows is what makes the
        // wheel stop at the oldest row instead of scrolling into blank space.
        private static bool _autoInstall;

        /// <summary>True once per second, so the auto-install retry cannot spin the main thread.</summary>
        private static bool _installTriedThisSecond()
        {
            int now = (int)DateTime.UtcNow.Ticks;
            // 10 000 ticks per millisecond: this is a cheap 1 s bucket, not a timer.
            int bucket = now / 10000000;
            if (bucket == _installBucket) return true;
            _installBucket = bucket;
            return false;
        }

        private static int _installBucket = -1;
        private static int _maxScrollBack;
        private static int _pageRows;
        private static TextBlock _chanHead;

        /// The console shell hands its content well in here; the page then fills it
        /// instead of centring itself in DisplayAreaGrid, and its own header row is
        /// collapsed because the shell already shows the callsign and the link state.
        internal static Panel ShellHost;
        private static bool _embedded;
        private static RowDefinition _headRow;
        private static string _status = "";
        private static long _signature = -1;
        private static int _drawn;
        private static string _paintSig = "";
        private static int _total;
        private static float _w;
        private static float _h;
        // The console host owns the single hotkey (N), so this page binds none of its
        // own; an empty name makes the old toggle branch inert instead of double-firing.
        private static string _keyName = "";
        private static string _lastSubmitNote = "";

        public static bool IsInstalled { get { return _wrap != null; } }
        public static bool IsVisible { get { return _visible && _wrap != null; } }
        public static string ToggleKeyName() { return _keyName; }

        // ================================================== install / remove / toggle
        public static JObject Install()
        {
            // Recorded first, on purpose: the first attempt usually fails with 409 because no
            // mission screen exists yet, and that failure is exactly when the retry must be armed.
            _autoInstall = true;
            if (_wrap != null)
            {
                JObject again = State();
                again["alreadyInstalled"] = true;
                return again;
            }

            FrameworkElement root = UiBridge.TacticalRootForInspector();
            if (root == null)
                throw new ApiException(409, "no_ui_host",
                    "no NoesisView currently shows SeapowerUI.MainGameView; the comms page "
                    + "only exists while a mission is on screen", false);

            object found = UiBridge.FindNamedForInspector(root, "DisplayAreaGrid");
            Panel host = found as Panel;
            if (host == null)
                throw new ApiException(409, "no_mount_point",
                    "DisplayAreaGrid was not found as a Panel under the tactical view; "
                    + "nothing was inserted", false);

            _embedded = ShellHost != null;
            if (_embedded) host = ShellHost;
            _host = host;
            _keyName = "";
            double mw = Convert.ToDouble(SettingFloat("MaxWidth", 1320f, 480f, 4000f), CultureInfo.InvariantCulture);
            double mh = Convert.ToDouble(SettingFloat("MaxHeight", 900f, 320f, 4000f), CultureInfo.InvariantCulture);
            if (mw > 0) _maxW = mw;
            if (mh > 0) _maxH = mh;
            Strings.EnsureLoaded();

            SetFullArea();
            BuildTree();
            if (_wrap == null)
                throw new ApiException(500, "ui_build_failed",
                    "the comms tree could not be built; see bridge.log", false);

            try
            {
                _host.Children.Add(_wrap);
                // 10002: the inspector sits at 10000 and the game's own overlays lower
                // still, so the two agent surfaces stay in their own order and neither
                // hides the other by accident.
                Noesis.Panel.SetZIndex(_wrap, 10002);
            }
            catch (Exception ex)
            {
                _wrap = null;
                throw new ApiException(500, "ui_mount_failed",
                    "adding the comms page to DisplayAreaGrid failed: "
                    + ex.GetType().Name, false);
            }

            _visible = true;
            _autoInstall = true;
            _signature = -1;
            ApplyGeometry();
            try { _wrap.UpdateLayout(); } catch (Exception) { }
            Refresh(true);
            Core.Log("comms page installed " + (int)_w + "x" + (int)_h
                + " into host " + Math.Round((double)_host.ActualWidth) + "x"
                + Math.Round((double)_host.ActualHeight));
            return State();
        }

        /// <summary>
        /// Fills the shell's content well. Called every frame while this page is the
        /// active tab, so it stays cheap: it only re-applies geometry.
        /// </summary>
        internal static void FillContentArea(Panel content)
        {
            if (content == null) return;
            ShellHost = content;
            _embedded = true;
            if (_wrap == null) return;
            Panel parent = _wrap.Parent as Panel;
            if (!ReferenceEquals(parent, content))
            {
                if (parent != null) parent.Children.Remove(_wrap);
                content.Children.Add(_wrap);
            }
            _host = content;
            ApplyGeometry();
        }

        public static JObject Remove()
        {
            _autoInstall = false;
            if (_wrap == null)
            {
                JObject o = new JObject();
                o["installed"] = false;
                o["removed"] = false;
                return o;
            }
            try
            {
                Panel parent = _wrap.Parent as Panel;
                if (parent != null) parent.Children.Remove(_wrap);
            }
            catch (Exception ex) { Core.Log("comms remove failed: " + ex.GetType().Name); }
            _wrap = null;
            _host = null;
            ShellHost = null;
            _embedded = false;
            _outer = null;
            _logPanel = null;
            _logViewport = null;
            _orderPanel = null;
            _input = null;
            _sendButton = null;
            if (_portraitImage != null) _portraitImage.Source = null;
            if (_portraitTexture != null) UnityEngine.Object.Destroy(_portraitTexture);
            _portraitTexture = null;
            _portraitImage = null;
            _portraitFrame = null;
            _stopButton = null;
            _stopQueuedUntil = 0;
            _visible = false;
            _armed = false;
            _signature = -1;
            ReleaseTyping("page removed");
            JObject r = new JObject();
            r["installed"] = false;
            r["removed"] = true;
            return r;
        }

        public static JObject Toggle()
        {
            if (_wrap == null) return Install();
            _visible = !_visible;
            try { _wrap.Visibility = _visible ? Visibility.Visible : Visibility.Collapsed; }
            catch (Exception) { }
            if (!_visible) ReleaseTyping("page hidden");
            return State();
        }

        public static JObject Show(bool on)
        {
            if (_wrap == null)
            {
                if (on) return Install();
                _autoInstall = false;
                return State();
            }
            _visible = on;
            try { _wrap.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }
            catch (Exception) { }
            if (!on) ReleaseTyping("page hidden");
            return State();
        }

        // ================================================== tree
        private static void BuildTree()
        {
            _paintSig = "";
            _wrap = new Grid();
            _wrap.Name = WindowName;
            _wrap.Background = new SolidColorBrush(Color.FromArgb(243, 6, 14, 24));

            _outer = new Border();
            _outer.Background = new SolidColorBrush(Color.FromArgb(255, 8, 18, 30));
            _outer.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 64, 128, 176));
            _outer.BorderThickness = new Thickness(1);
            _outer.IsHitTestVisible = true;

            Grid root = new Grid();
            _headRow = Row(HeaderH);
            root.RowDefinitions.Add(_headRow);
            root.RowDefinitions.Add(Row(0, true));
            root.RowDefinitions.Add(Row(InputH));
            root.RowDefinitions.Add(Row(FooterH));

            Border header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Star) });
            body.ColumnDefinitions.Add(Col(1));
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35, GridUnitType.Star) });

            Grid left = BuildAgentColumn();
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            Border spine = new Border();
            spine.Background = new SolidColorBrush(Color.FromArgb(255, 40, 78, 112));
            Grid.SetColumn(spine, 1);
            body.Children.Add(spine);

            Border right = BuildOrderColumn();
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            Border inputRow = BuildInputRow();
            Grid.SetRow(inputRow, 2);
            root.Children.Add(inputRow);

            Border footer = new Border();
            footer.Background = new SolidColorBrush(Color.FromArgb(255, 9, 20, 33));
            footer.Padding = new Thickness(Gutter, 2, Gutter, 2);
            _statusText = new TextBlock();
            _statusText.FontSize = 11;
            _statusText.TextWrapping = TextWrapping.NoWrap;
            _statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 130, 170, 200));
            _statusText.Text = "…";
            Grid footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(Col(150));
            footerGrid.ColumnDefinitions.Add(Col(124));
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ComboBox target = FooterSelector(footerGrid, 0, new[] { "DSH", "Codex (WIP)" }, _target == "codex" ? 1 : 0);
            target.SelectionChanged += delegate
            {
                _target = target.SelectedIndex == 1 ? "codex" : "dsh";
                _scrollBack = 0;
                UpdatePortrait();
                Refresh(true);
            };
            string[] portraits = { "Auto", "claude", "deepseek", "gemini", "glm", "gpt", "kimi", "Qwen" };
            ComboBox portrait = FooterSelector(footerGrid, 1, portraits, Math.Max(0, Array.IndexOf(portraits, _portrait)));
            portrait.SelectionChanged += delegate
            {
                if (portrait.SelectedIndex < 0) return;
                _portrait = portraits[portrait.SelectedIndex];
                UpdatePortrait();
            };
            _statusText.Margin = new Thickness(6, 0, 0, 0);
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_statusText, 2);
            footerGrid.Children.Add(_statusText);
            footer.Child = footerGrid;
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            _outer.Child = root;
            _wrap.Children.Add(_outer);
        }

        private static ComboBox FooterSelector(Grid grid, int column, string[] choices, int selected)
        {
            ComboBox selector = new ComboBox();
            selector.FontSize = 12;
            selector.Margin = new Thickness(0, 0, 4, 0);
            foreach (string choice in choices)
                selector.Items.Add(new ComboBoxItem { Content = (column == 0 ? "Target: " : "Portrait: ") + choice });
            selector.SelectedIndex = selected;
            Grid.SetColumn(selector, column);
            grid.Children.Add(selector);
            return selector;
        }

        private static void UpdatePortrait()
        {
            if (_portraitFrame == null) return;
            _portraitFrame.Child = _badgeText;
            _portraitImage.Source = null;
            if (_portraitTexture != null) UnityEngine.Object.Destroy(_portraitTexture);
            _portraitTexture = null;
            string name = _portrait == "Auto" ? (_target == "codex" ? "gpt" : "deepseek") : _portrait;
            try
            {
                string file = System.IO.Path.Combine(Core.ModDir(), "portrait", name + ".png");
                if (!File.Exists(file)) return;
                _portraitTexture = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
                if (!UnityEngine.ImageConversion.LoadImage(_portraitTexture, File.ReadAllBytes(file), true)) return;
                _portraitImage.Source = new TextureSource(_portraitTexture);
                _portraitFrame.Child = _portraitImage;
            }
            catch (Exception e) { Core.Log("comms portrait " + name + ": " + e.Message); }
        }

        private static Border BuildHeader()
        {
            Border bar = new Border();
            bar.Background = new SolidColorBrush(Color.FromArgb(255, 11, 26, 42));
            bar.Padding = new Thickness(Gutter, 3, Gutter, 3);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col(PortraitSize + 4));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(Col(210));

            Border badge = new Border();
            badge.Width = (float)PortraitSize;
            badge.Height = (float)PortraitSize;
            badge.Background = new SolidColorBrush(Color.FromArgb(255, 20, 54, 82));
            badge.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 170, 220));
            badge.BorderThickness = new Thickness(1);
            _badgeText = new TextBlock();
            _badgeText.FontSize = 11;
            _badgeText.HorizontalAlignment = HorizontalAlignment.Center;
            _badgeText.VerticalAlignment = VerticalAlignment.Center;
            _badgeText.Foreground = new SolidColorBrush(Color.FromArgb(255, 160, 220, 255));
            _badgeText.Text = CommsBridge.AgentCall;
            _portraitFrame = badge;
            _portraitImage = new Image { Stretch = Stretch.UniformToFill };
            UpdatePortrait();
            Grid.SetColumn(badge, 0);
            g.Children.Add(badge);

            _title = new TextBlock();
            _title.FontSize = 13;
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Margin = new Thickness(8, 0, 0, 0);
            _title.Foreground = new SolidColorBrush(Color.FromArgb(255, 205, 236, 255));
            _title.Text = CommsBridge.AgentName + " / OFFLINE";
            Grid.SetColumn(_title, 1);
            g.Children.Add(_title);

            Grid rightBits = new Grid();
            rightBits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rightBits.ColumnDefinitions.Add(Col(88));

            _linkText = new TextBlock();
            _linkText.FontSize = 11;
            _linkText.TextWrapping = TextWrapping.NoWrap;
            _linkText.HorizontalAlignment = HorizontalAlignment.Right;
            _linkText.VerticalAlignment = VerticalAlignment.Center;
            _linkText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 190, 90));
            _linkText.Text = "LINK …";
            Grid.SetColumn(_linkText, 0);
            rightBits.Children.Add(_linkText);

            _genText = new TextBlock();
            _genText.FontSize = 11;
            _genText.TextWrapping = TextWrapping.NoWrap;
            _genText.HorizontalAlignment = HorizontalAlignment.Right;
            _genText.VerticalAlignment = VerticalAlignment.Center;
            _genText.Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 210, 240));
            _genText.Text = "IDLE";
            Grid.SetColumn(_genText, 1);
            rightBits.Children.Add(_genText);

            // The panel no longer reaches the screen edge, so the game's own clock cannot sit on
            // top of the status block any more; a wide margin here only pushed the text out of
            // the header. Keep a small gap.
            rightBits.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(rightBits, 2);
            g.Children.Add(rightBits);

            bar.Child = g;
            return bar;
        }

        private static Grid BuildAgentColumn()
        {
            Grid col = new Grid();
            col.RowDefinitions.Add(Row(18));
            col.RowDefinitions.Add(Row(0, true));
            col.RowDefinitions.Add(Row(16));

            Border cap = Caption("AGENT CHANNEL");
            _chanHead = cap.Child as TextBlock;
            Grid.SetRow(cap, 0);
            col.Children.Add(cap);

            _logPanel = new StackPanel();
            _logPanel.Orientation = Orientation.Vertical;

            _logEmpty = new TextBlock();
            _logEmpty.FontSize = 12;
            _logEmpty.TextWrapping = TextWrapping.Wrap;
            _logEmpty.Margin = new Thickness(Gutter, 2, Gutter, 0);
            _logEmpty.Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 160, 190));
            _logEmpty.Text = Strings.T("comms.empty");

            Border box = new Border();
            box.Background = new SolidColorBrush(Color.FromArgb(255, 7, 16, 27));
            box.Padding = new Thickness(0);
            box.ClipToBounds = true;
            box.IsHitTestVisible = true;
            Canvas inner = new Canvas();
            inner.Children.Add(_logPanel);
            box.Child = inner;
            _logViewport = box;
            try { box.MouseWheel += new MouseWheelEventHandler(OnWheel); }
            catch (Exception ex) { Core.Log("comms wheel not wired: " + ex.GetType().Name); }
            Grid.SetRow(box, 1);
            col.Children.Add(box);

            _logFooter = new TextBlock();
            _logFooter.FontSize = 10;
            _logFooter.TextWrapping = TextWrapping.NoWrap;
            _logFooter.Margin = new Thickness(Gutter, 1, Gutter, 0);
            _logFooter.Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 140, 170));
            _logFooter.Text = "";
            Grid.SetRow(_logFooter, 2);
            col.Children.Add(_logFooter);

            return col;
        }

        private static Border BuildOrderColumn()
        {
            Border panel = new Border();
            panel.Background = new SolidColorBrush(Color.FromArgb(255, 8, 18, 30));
            panel.Padding = new Thickness(Gutter, 0, Gutter, 0);

            Grid col = new Grid();
            col.RowDefinitions.Add(Row(18));
            col.RowDefinitions.Add(Row(0, true));

            Border cap = Caption("CURRENT ORDER");
            Grid.SetRow(cap, 0);
            col.Children.Add(cap);

            _orderPanel = new StackPanel();
            _orderPanel.Orientation = Orientation.Vertical;

            Border box = new Border();
            box.ClipToBounds = true;
            box.Child = _orderPanel;
            Grid.SetRow(box, 1);
            col.Children.Add(box);

            panel.Child = col;
            return panel;
        }

        private static Border BuildInputRow()
        {
            Border row = new Border();
            row.Background = new SolidColorBrush(Color.FromArgb(255, 10, 22, 36));
            row.Padding = new Thickness(Gutter, 2, Gutter, 2);
            row.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 34, 66, 96));
            row.BorderThickness = new Thickness(0, 1, 0, 0);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(Col(6));
            g.ColumnDefinitions.Add(Col(72));

            _input = new TextBox();
            _input.Name = WindowName + "Input";
            _input.FontSize = 13;
            _input.Height = 24;
            // A box without a border is invisible on this dark background: the player could not
            // tell where the text goes. Border and padding only - the class handler still owns
            // every keystroke.
            _input.Background = new SolidColorBrush(Color.FromArgb(255, 8, 18, 30));
            _input.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 52, 104, 148));
            _input.BorderThickness = new Thickness(1);
            _input.VerticalAlignment = VerticalAlignment.Center;
            _input.Padding = new Thickness(6, 0, 6, 0);
            _input.Background = new SolidColorBrush(Color.FromArgb(255, 4, 10, 18));
            _input.Foreground = new SolidColorBrush(Color.FromArgb(255, 210, 238, 255));
            // No font family is set on purpose: naming one is what produced mojibake
            // for the inspector's Chinese labels; letting the game's default stand is
            // the configuration that rendered correctly.
            try
            {
                _input.PreviewMouseLeftButtonDown += new MouseButtonEventHandler(OnBoxClick);
                _input.PreviewKeyDown += new KeyEventHandler(OnBoxKey);
            }
            catch (Exception ex) { Core.Log("comms input not wired: " + ex.GetType().Name); }
            Grid.SetColumn(_input, 0);
            g.Children.Add(_input);

            _sendLabel = new TextBlock();
            _sendLabel.FontSize = 12;
            _sendLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _sendLabel.VerticalAlignment = VerticalAlignment.Center;
            _sendLabel.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 235, 255));
            _sendLabel.Text = "SEND";

            _sendButton = new Border();
            _sendButton.Name = WindowName + "Send";
            _sendButton.Background = new SolidColorBrush(Color.FromArgb(255, 18, 52, 80));
            _sendButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 170, 220));
            _sendButton.BorderThickness = new Thickness(1);
            _sendButton.IsHitTestVisible = true;
            _sendButton.Child = _sendLabel;
            try
            {
                _sendButton.MouseLeftButtonDown += new MouseButtonEventHandler(OnSendClick);
            }
            catch (Exception) { }
            Grid.SetColumn(_sendButton, 2);
            g.Children.Add(_sendButton);

            g.ColumnDefinitions.Add(Col(6));
            g.ColumnDefinitions.Add(Col(64));
            _stopButton = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 73, 33, 37)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 195, 107, 107)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "STOP",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 200, 200))
                }
            };
            _stopButton.MouseLeftButtonDown += OnStopClick;
            Grid.SetColumn(_stopButton, 4);
            g.Children.Add(_stopButton);

            row.Child = g;
            return row;
        }

        private static bool CanStop(JObject state)
        {
            string generation = AsStr(state["generation"]);
            return _target == "dsh" && AsStr(state["link"]) == "attached" &&
                !string.IsNullOrEmpty(AsStr(state["sessionId"])) &&
                (generation == "working" || generation == "executing");
        }

        private static void OnStopClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            JObject state = CommsBridge.State();
            if (!CanStop(state) || UnityEngine.Time.realtimeSinceStartup < _stopQueuedUntil) return;
            string temp = null;
            try
            {
                string run = System.IO.Path.Combine(Core.ModDir(), "comms", "run");
                System.IO.Directory.CreateDirectory(run);
                string requestFile = System.IO.Path.Combine(run, "dsh.stop.json");
                if (System.IO.File.Exists(requestFile) &&
                    DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(requestFile) > TimeSpan.FromSeconds(5))
                    System.IO.File.Delete(requestFile);

                JObject request = new JObject();
                request["sessionId"] = AsStr(state["sessionId"]);
                request["expiresAt"] = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds + 5000;
                temp = requestFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                System.IO.File.WriteAllText(temp, request.ToString(Newtonsoft.Json.Formatting.None));
                System.IO.File.Move(temp, requestFile);
                _stopQueuedUntil = UnityEngine.Time.realtimeSinceStartup + 5;
                _status = "DSH stop requested";
            }
            catch (Exception ex)
            {
                _status = "DSH stop request failed";
                Core.Log("comms stop: " + ex.Message);
            }
            finally
            {
                if (temp != null)
                {
                    try { System.IO.File.Delete(temp); }
                    catch (System.IO.IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            Refresh(true);
        }

        private static Border Caption(string text)
        {
            Border b = new Border();
            b.Background = new SolidColorBrush(Color.FromArgb(255, 12, 26, 42));
            b.Padding = new Thickness(Gutter, 1, Gutter, 1);
            TextBlock t = new TextBlock();
            t.FontSize = 10;
            t.TextWrapping = TextWrapping.NoWrap;
            t.Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 170, 205));
            t.Text = text;
            b.Child = t;
            return b;
        }

        // ================================================== geometry
        // The page is deliberately smaller than the host. Two measured reasons:
        //   * the host is not the screen - the game paints its own bottom bar and minimap
        //     toolbar over the lower part of it, and anything we put there is simply cut;
        //   * a communication panel that covers the whole map hides the thing being discussed.
        // So the default is a centred panel of about two thirds of the host, and setting.ini
        // can override width/height/left/top for anyone who wants another shape.
        private const double PageWidthFactor = 0.70;
        private const double PageHeightFactor = 0.80;
        private const double MaxPageW = 1180;
        private const double MaxPageH = 700;
        // setting.ini may override the caps; zero keeps the built-in default.
        private static double _maxW = MaxPageW;
        private static double _maxH = MaxPageH;

        private static void SetFullArea()
        {
            ApplyGeometry();
        }

        // internal (not private): the console shell places its tab strip from the same
        // setting.ini section, and must clamp it with the same rules.
        internal static float SettingFloat(string key, float dflt, float min, float max)
        {
            string raw = Setting(key, null);
            if (raw == null) return dflt;
            float parsed;
            if (!float.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                return dflt;
            if (parsed < min || parsed > max) return dflt;
            return parsed;
        }

        private static void ApplyGeometry()
        {
            if (_wrap == null) return;

            double hostW = _host == null ? 0 : (double)_host.ActualWidth;
            double hostH = _host == null ? 0 : (double)_host.ActualHeight;
            if (hostW < MinW) hostW = MinW;
            if (hostH < MinH) hostH = MinH;

            double wantW = hostW * PageWidthFactor;
            if (wantW > _maxW) wantW = _maxW;
            if (wantW < MinW) wantW = MinW;
            double wantH = hostH * PageHeightFactor;
            if (wantH > _maxH) wantH = _maxH;
            if (wantH < MinH) wantH = MinH;

            // 0 means "auto": the panel centres itself. Any explicit value is clamped into the
            // host so a settings typo can never push the page off screen.
            double setW = (double)SettingFloat("Width", 0f, 0f, 4000f);
            double setH = (double)SettingFloat("Height", 0f, 0f, 4000f);
            _w = (float)(setW > 0 ? Math.Min(setW, hostW) : wantW);
            _h = (float)(setH > 0 ? Math.Min(setH, hostH) : wantH);

            double left = (hostW - (double)_w) / 2.0;
            double top = (hostH - (double)_h) / 2.0;
            double setLeft = (double)SettingFloat("Left", -1f, -4000f, 4000f);
            double setTop = (double)SettingFloat("Top", -1f, -4000f, 4000f);
            if (setLeft >= 0) left = setLeft;
            if (setTop >= 0) top = setTop;
            if (left < 0) left = 0;
            if (top < 0) top = 0;
            if (left + (double)_w > hostW) left = Math.Max(0, hostW - (double)_w);
            if (top + (double)_h > hostH) top = Math.Max(0, hostH - (double)_h);

            // The shell owns the frame; the page still displays its portrait and target.
            if (_embedded)
            {
                _w = (float)hostW;
                _h = (float)hostH;
                left = 0;
                top = 0;

            }

            _wrap.Width = _w;
            _wrap.Height = _h;
            _wrap.HorizontalAlignment = HorizontalAlignment.Left;
            _wrap.VerticalAlignment = VerticalAlignment.Top;
            _wrap.Margin = new Thickness((float)Math.Round(left), (float)Math.Round(top), 0, 0);
            if (_outer != null)
            {
                _outer.Width = _w;
                _outer.Height = _h;
            }
        }

        // ================================================== input
        private static void OnBoxClick(object sender, MouseButtonEventArgs e)
        {
            _armed = true;
            _boxClickFrame = UnityEngine.Time.frameCount;
            JObject typing = UiBridge.SetTyping(true, "comms box click", _input);
            _status = (bool?)typing["ok"] == true ? "typing" : "typing lock unavailable";
            Refresh(true);
        }

        private static void OnSendClick(object sender, MouseButtonEventArgs e)
        {
            SubmitBox("send button");
        }

        // The root tunnel and the TextBox share the same submit path.
        private static void OnBoxKey(object sender, KeyEventArgs e)
        {
            try
            {
                if (e == null) return;
                string name = e.Key.ToString();
                if (name == "Escape" || name == "Enter" || name == "Return")
                    e.Handled = HandleKey(name);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Called from the root preview-key tunnel for every key press. Returns true when
        /// the key belonged to this page so the caller can stop worrying about it.
        /// </summary>
        public static bool HandleKey(string name)
        {
            if (name == null) return false;

            // The toggle works even when the page is not typing - it is a window action.
            if (!UiBridge.Typing && string.Equals(name, _keyName, StringComparison.OrdinalIgnoreCase))
            {
                Toggle();
                return true;
            }

            if (!_visible || _wrap == null) return false;
            bool typing = _armed && UiBridge.Typing;
            if (name == "Escape" && typing)
            {
                ReleaseTyping("escape");
                return true;
            }
            // Noesis reports the primary name Return; comparing only "Enter" is why the
            // inspector's Enter looked dead for two rounds.
            if ((name == "Enter" || name == "Return") && typing)
            {
                SubmitBox("enter key");
                return true;
            }
            if (typing) return false;
            // Scrolling must not need the mouse: the wheel handler only fires while the pointer
            // is over the panel, which is useless when the player is reading a long answer.
            if (name == "PageUp" || name == "Prior")
            {
                ScrollBy(_pageRows > 0 ? _pageRows : 1);
                return true;
            }
            if (name == "PageDown" || name == "Next")
            {
                ScrollBy(-(_pageRows > 0 ? _pageRows : 1));
                return true;
            }
            if (name == "Up") { ScrollBy(1); return true; }
            if (name == "Down") { ScrollBy(-1); return true; }
            if (name == "Home") { ScrollBy(1000000); return true; }
            if (name == "End") { ScrollBy(-1000000); return true; }
            return false;
        }

        private static void SubmitBox(string source)
        {
            if (_input == null || !_visible || _submitFrame == UnityEngine.Time.frameCount) return;
            _submitFrame = UnityEngine.Time.frameCount;
            string text = null;
            try { text = Convert.ToString(_input.Text); }
            catch (Exception) { }
            text = (text == null ? "" : text).Trim();
            if (text.Length == 0)
            {
                _status = "nothing typed";
                Refresh(true);
                return;
            }
            try
            {
                JObject req = new JObject();
                req["text"] = text;
                req["target"] = _target;
                JObject r = CommsBridge.Send(req);
                if ((bool?)r["queued"] == true)
                {
                    _lastSubmitNote = _target.ToUpperInvariant() + " queued #" + Convert.ToString(r["id"], CultureInfo.InvariantCulture);
                    _status = "queued for " + _target;
                    _input.Text = "";
                }
                else
                {
                    _lastSubmitNote = "";
                    _status = "send failed: " + ((string)r["error"] ?? "outbox write failed");
                    Core.Log("comms send failed (" + source + "): " + _status);
                }
            }
            catch (Exception ex)
            {
                _status = "send failed: " + ex.GetType().Name;
                Core.Log("comms send failed (" + source + "): " + ex.GetType().Name + ": " + ex.Message);
            }
            _scrollBack = 0;
            Refresh(true);
        }

        private static void ReleaseTyping(string why)
        {
            _armed = false;
            if (UiBridge.Typing) UiBridge.SetTyping(false, "comms " + why);
            _status = "";
            Refresh(true);
        }

        /// <summary>
        /// Moves the view by <paramref name="rows"/> rows towards older messages (positive)
        /// or newer ones (negative), clamped to what actually exists. Both the wheel and the
        /// keyboard go through here so the clamp cannot drift between the two paths.
        /// </summary>
        private static void ScrollBy(int rows)
        {
            int next = Math.Max(0, Math.Min(_maxScrollBack, _scrollBack + rows * (int)RowH));
            if (next == _scrollBack) return;
            _scrollBack = next;
            Refresh(true);
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;
            int steps = Math.Max(1, Math.Abs(e.Delta) / 120);
            ScrollBy(e.Delta > 0 ? 3 * steps : -3 * steps);
            e.Handled = true;
        }
        // ================================================== refresh
        private static long Signature(List<CommsItem> items, JObject st)
        {
            long sig = 1469598103934665603L;
            for (int i = 0; i < items.Count; i++)
            {
                sig = (sig ^ items[i].Seq) * 1099511628211L;
                if (items[i].Kind == "assistant")
                {
                    sig = (sig ^ (items[i].Text ?? "").GetHashCode()) * 1099511628211L;
                    sig = (sig ^ (items[i].Thinking ?? "").GetHashCode()) * 1099511628211L;
                }
            }
            sig = (sig ^ items.Count) * 1099511628211L;
            string link = Convert.ToString(st["link"]);
            for (int i = 0; link != null && i < link.Length; i++) sig = (sig ^ link[i]) * 1099511628211L;
            string gen = Convert.ToString(st["generation"]);
            for (int i = 0; gen != null && i < gen.Length; i++) sig = (sig ^ gen[i]) * 1099511628211L;
            string lastTool = Convert.ToString(st["lastTool"]);
            for (int i = 0; lastTool != null && i < lastTool.Length; i++) sig = (sig ^ lastTool[i]) * 1099511628211L;
            sig = (sig ^ _scrollBack) * 1099511628211L;
            sig = (sig ^ _armed.GetHashCode()) * 1099511628211L;
            for (int i = 0; i < _status.Length; i++) sig = (sig ^ _status[i]) * 1099511628211L;
            sig = (sig ^ _target.GetHashCode()) * 1099511628211L;
            if (_armed && _input != null)
                sig = (sig ^ (Convert.ToString(_input.Text) ?? "").GetHashCode()) * 1099511628211L;
            if (_logViewport != null)
            {
                sig = (sig ^ ((int)_logViewport.ActualHeight)) * 1099511628211L;
                sig = (sig ^ ((int)_logViewport.ActualWidth)) * 1099511628211L;
            }
            return sig;
        }

        public static void Tick()
        {
            if (_wrap == null)
            {
                // Nothing is mounted. If a mission is up and the operator asked for the page, mount
                // it now instead of making them repeat the request after the scenario loads.
                if (_autoInstall && !_installTriedThisSecond())
                {
                    try
                    {
                        FrameworkElement root = UiBridge.TacticalRootForInspector();
                        if (root != null) Install();
                    }
                    catch (Exception ex)
                    {
                        // Stay quiet while there is no mission: this path runs every tick and a
                        // log line per frame would bury everything else.
                        Core.Log("comms auto-install waiting: " + ex.GetType().Name);
                    }
                }
                return;
            }
            try
            {
                // The host reports its real size only after the game has finished arranging it.
                // Installing into a mission that is still settling therefore pins the page to the
                // earlier (larger) size, and the host clips the bottom rows - which is exactly
                // where the input box lives. Re-apply whenever the host size moved.
                if (_host != null)
                {
                    double hostW = (double)_host.ActualWidth;
                    double hostH = (double)_host.ActualHeight;
                    if (hostW >= MinW && Math.Abs(hostW - (double)_w) > 0.5)
                    {
                        ApplyGeometry();
                    }
                    else if (hostH >= MinH && Math.Abs(hostH - (double)_h) > 0.5)
                    {
                        ApplyGeometry();
                    }
                }
                Refresh(false);
            }
            catch (Exception ex)
            {
                _signature = -1;
                _paintSig = "";
                Core.Log("comms tick failed: " + ex.GetType().Name);
            }
        }

        private static void Refresh(bool force)
        {
            if (_wrap == null) return;
            JObject st = CommsBridge.State();
            List<CommsItem> items = CommsBridge.Snapshot();
            items.RemoveAll(item => item.Target != _target);
            if (_stopButton != null)
            {
                bool canStop = CanStop(st);
                if (!canStop && _target == "dsh") _stopQueuedUntil = 0;
                bool enabled = canStop && UnityEngine.Time.realtimeSinceStartup >= _stopQueuedUntil;
                _stopButton.Visibility = _target == "dsh" ? Visibility.Visible : Visibility.Collapsed;
                _stopButton.IsEnabled = enabled;
                _stopButton.Opacity = enabled ? 1.0f : 0.45f;
            }
            long sig = Signature(items, st);
            if (!force && sig == _signature) return;
            _signature = sig;

            ApplyGeometry();

            string link = Convert.ToString(st["link"]);
            string gen = Convert.ToString(st["generation"]);
            bool linked = link == "attached";

            if (_badgeText != null) _badgeText.Text = CommsBridge.AgentCall;
            if (_title != null)
                _title.Text = _target == "codex" ? "CODEX / DESKTOP" : CommsBridge.AgentName + (linked ? " / ONLINE" : " / OFFLINE");
            if (_linkText != null)
            {
                _linkText.Text = "DSH " + (link == null ? "?" : link.ToUpperInvariant());
                _linkText.Foreground = new SolidColorBrush(linked
                    ? Color.FromArgb(255, 120, 230, 160)
                    : Color.FromArgb(255, 255, 190, 90));
            }
            if (_genText != null)
            {
                // One short token: the status column is 88 px wide and "EXECUTING pwsh" was cut
                // at the panel edge. The tool name goes to the channel caption instead.
                string g = gen == null ? "idle" : gen.ToUpperInvariant();
                string tool = Convert.ToString(st["lastTool"]);
                if (g == "EXECUTING") g = "EXEC";
                else if (g == "WORKING") g = "BUSY";
                int pending = AsInt(st["pendingOutbox"]);
                if (pending > 0) g = g + " · " + pending + "Q";
                if (_chanHead != null)
                {
                    _chanHead.Text = string.IsNullOrEmpty(tool)
                        ? "AGENT CHANNEL"
                        : "AGENT CHANNEL · " + tool.ToUpperInvariant();
                }
                _genText.Text = g;
                _genText.Foreground = new SolidColorBrush(gen == "executing"
                    ? Color.FromArgb(255, 255, 210, 120)
                    : Color.FromArgb(255, 150, 210, 240));
            }

            PaintLog(items, st, sig);
            PaintOrder();

            if (_statusText != null)
            {
                string note = Convert.ToString(st["linkNote"]);
                string err = Convert.ToString(st["lastError"]);
                string s = "";
                if (!string.IsNullOrEmpty(_status)) s = _status;
                else if (!string.IsNullOrEmpty(_lastSubmitNote)) s = _lastSubmitNote;
                if (!string.IsNullOrEmpty(note)) s = s.Length == 0 ? note : s + "  ·  " + note;
                if (!string.IsNullOrEmpty(err)) s = s + "  ·  error: " + err;
                if (s.Length == 0) s = Strings.T("comms.hint");
                _statusText.Text = s;
            }
        }

        /// <summary>Width of the left column, which is where wrapping has to fit.</summary>
        private static double LeftColumnWidth()
        {
            double body = (double)_w - (double)ColSpine;
            double left = body * AgentColumnShare;
            if (left < 120.0) left = 120.0;
            return left;
        }

        /// <summary>
        /// Characters that fit on one wrapped line. Deliberately an estimate: this build has no
        /// reachable text measurement, and guessing narrow costs a wrapped row while guessing
        /// wide costs a truncated sentence - the second one is the bug being fixed.
        /// </summary>
        private static int CharsPerLine()
        {
            double usable = LeftColumnWidth() - 2.0 * (double)Gutter - 8.0;
            int n = (int)(usable / 7.4);
            if (n < 16) n = 16;
            return n;
        }

        private static string ClipToRows(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int limit = CharsPerLine() * _maxRowLines;
            if (text.Length <= limit) return text;
            // Say so instead of silently cutting: a clipped answer must not read as a short one.
            return text.Substring(0, limit - 2) + " …";
        }

        /// <summary>
        /// Flatten the transcript into display rows (prefix + colour). Cheap: strings only, no
        /// visual objects - the panel is then built for the visible slice alone.
        /// </summary>
        private static void CollectRows(List<CommsItem> items, List<string> texts, List<Color> colors)
        {
            for (int i = 0; i < items.Count; i++)
            {
                CommsItem it = items[i];
                if (it.Kind == "user")
                {
                    texts.Add("YOU>  " + it.Text);
                    colors.Add(Color.FromArgb(255, 255, 214, 130));
                }
                else if (it.Kind == "assistant")
                {
                    bool hasText = !string.IsNullOrEmpty(it.Text);
                    if (hasText)
                    {
                        texts.Add("TA>  " + it.Text);
                        colors.Add(Color.FromArgb(255, 214, 240, 255));
                    }
                    // The tool name is NOT history either: the step's reasoning becomes the one
                    // live thinking row, and the footer already names the running tool. Printing
                    // "▸ pwsh" per step is exactly the noise the player asked to be rid of.
                }
                // Tool calls and results are deliberately NOT history: the player asked to see
                // the agent thinking instead, and a wall of "… pwsh / ✓ tool done" pushes the
                // actual conversation off the panel. The footer carries the live tool instead.
                else if (it.Kind == "exec" || it.Kind == "execdone")
                {
                    continue;
                }
            }
        }

        private static void PaintLog(List<CommsItem> items, JObject st, long contentSignature)
        {
            if (_logPanel == null) return;

            // Rebuild only when the visible window actually changed: replacing the child set
            // every tick is wasted main-thread work and keeps the panel from settling.
            string sig = contentSignature + "|" + (int)_w + "|" + (int)_h;
            if (sig == _paintSig) return;
            _paintSig = sig;

            _logPanel.Children.Clear();
            Canvas.SetTop(_logPanel, 0);
            _total = items.Count;

            if (items.Count == 0 && !_armed)
            {
                _logPanel.Height = (float)(RowH * 2);
                _maxScrollBack = _scrollBack = 0;
                _pageRows = 0;
                if (_logEmpty != null) _logPanel.Children.Add(_logEmpty);
                if (_logFooter != null)
                    _logFooter.Text = Convert.ToString(st["linkNote"]);
                _drawn = 0;
                return;
            }

            // Rows, not items: wrapping means one item can be four rows tall, and a window that
            // pretends they are all RowH high clips the bottom of the newest message.
            List<string> texts = new List<string>();
            List<Color> colors = new List<Color>();
            CollectRows(items, texts, colors);

            double viewportH = _logViewport == null ? 0 : (double)_logViewport.ActualHeight;
            double boxH = (viewportH > 0 ? viewportH : (double)_h - HeaderH - InputH - FooterH - 40) - 2;
            string draft = "";
            if (_armed && _input != null)
            {
                string typed = Convert.ToString(_input.Text);
                draft = "YOU>  " + ClipToRows(string.IsNullOrEmpty(typed) ? "…" : typed);
            }

            // One live "thinking" row: the newest reasoning tail, replaced in place on every
            // rebuild so it never accumulates. Dimmer and prefixed so it can never be read as
            // something the agent said to the player.
            string thought = "";
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(items[i].Thinking)) { thought = items[i].Thinking; break; }
                if (items[i].Kind == "turn" && items[i].Phase == "end") break;
            }
            // Measured on this build: most assistant events carry an EMPTY reasoning part, so a
            // row that only shows reasoning would be blank most of the time. Fall back to the
            // live activity ("working · pwsh") instead of guessing or faking a thought.
            string liveText = "";
            if (!string.IsNullOrEmpty(thought)) liveText = "◌ " + ClipToRows(thought);
            else if (st != null && AsStr(st["generation"]) == "executing")
            {
                string busyTool = AsStr(st["lastTool"]);
                liveText = "◌ working" + (busyTool.Length > 0 ? " · " + busyTool : "");
            }
            int transcriptCount = texts.Count;
            if (draft.Length > 0)
            {
                texts.Add(draft);
                colors.Add(Color.FromArgb(255, 255, 214, 130));
            }
            else if (liveText.Length > 0)
            {
                texts.Add(liveText);
                colors.Add(Color.FromArgb(255, 118, 156, 184));
            }

            // Give the stack a wrapping width and let Noesis arrange its natural height.
            // Measuring detached text and fixing the stack to that estimate can clip the
            // last wrapped line before the viewport's bottom padding even begins.
            float width = _logViewport != null && _logViewport.ActualWidth > 0
                ? (float)_logViewport.ActualWidth : (float)LeftColumnWidth();
            _logPanel.Width = width;
            _logPanel.Height = float.NaN;
            double viewport = Math.Max(1, boxH);
            _pageRows = Math.Max(1, (int)(viewport / RowH));
            for (int i = 0; i < texts.Count; i++)
                _logPanel.Children.Add(Row(texts[i], colors[i]));
            // Keep the last glyphs clear of the content stack's bottom edge.
            _logPanel.Children.Add(new Border { Height = 6, IsHitTestVisible = false });
            Settle();

            double tailSpace = texts.Count > 0 ? Math.Min(TranscriptTailRows * RowH, Math.Max(0, viewport - 2 * RowH)) : 0;
            double maxTop = Math.Max(0, (double)_logPanel.ActualHeight + tailSpace - viewport);
            _maxScrollBack = (int)Math.Ceiling(maxTop);
            _scrollBack = Math.Min(_scrollBack, _maxScrollBack);
            double top = Math.Max(0, maxTop - _scrollBack);
            Canvas.SetTop(_logPanel, (float)-top);

            _drawn = 0;
            double rowTop = 0;
            for (int i = 0; i < transcriptCount && rowTop < top + viewport; i++)
            {
                TextBlock row = (TextBlock)_logPanel.Children[i];
                double rowBottom = rowTop + row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
                if (rowBottom > top) _drawn++;
                rowTop = rowBottom;
            }
            if (_logFooter != null)
            {
                // The tab strip rides on the footer: it is the one line both pages
                // always paint, so the operator can see which tab the hotkey will switch
                // to without either page growing its own chrome.
                string tail = ConsoleHost.Strip() + "   " + _drawn + " of " + transcriptCount + " messages · " + items.Count + " events";
                if (_scrollBack > 0) tail = tail + " · history (wheel down for latest)";
                else tail = tail + " · live";
                _logFooter.Text = tail;
            }

            Settle();

            // If the panel still has no height the rows were built before the content well
            // was arranged, and nothing would show until some unrelated click forced a layout
            // pass. Drop the signature so the next tick paints again against the real size.
            if (_logPanel != null && (double)_logPanel.ActualHeight <= 0.5)
            {
                _signature = -1;
                _paintSig = "";
            }
        }

        /// <summary>
        /// One layout pass. Called after a rebuild, never from a per-frame path that did not
        /// change anything: the cost is why the rows are only rebuilt on a signature change.
        /// </summary>
        private static void Settle()
        {
            try { if (_wrap != null) _wrap.UpdateLayout(); }
            catch (Exception) { }
        }

        private static string AsStr(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (t.Length > 1600) t = t.Substring(0, 1600) + " …";
            return t.Trim();
        }

        private static TextBlock Row(string text, Color color)
        {
            TextBlock t = new TextBlock();
            t.FontSize = 12;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(Gutter, 1, Gutter, 1);
            t.Foreground = new SolidColorBrush(color);
            t.Text = text;
            return t;
        }

        private static void PaintOrder()
        {
            if (_orderPanel == null) return;
            _orderPanel.Children.Clear();

            // Source of truth for "what did the agent ask the game to do" is the bridge's
            // own action log. When it has nothing, the panel says so instead of dressing
            // up the last chat line as an order - the brief forbids inventing state.
            List<InspectorEvent> actions = null;
            try { actions = InspectorBuffer.Snapshot("ActionIssued", null, null); }
            catch (Exception) { }
            InspectorEvent last = null;
            if (actions != null && actions.Count > 0) last = actions[actions.Count - 1];

            if (last == null)
            {
                _orderPanel.Children.Add(Field("MSG", Strings.T("comms.noOrders"), Color.FromArgb(255, 200, 150, 110)));
                _orderPanel.Children.Add(Field("STATUS", "N/A", Color.FromArgb(255, 140, 170, 195)));
                return;
            }

            string id = string.IsNullOrEmpty(last.EventId) ? "N/A" : last.EventId;
            string when = last.Timestamp == null ? "" : last.Timestamp;
            if (when.Length > 8) when = when.Substring(0, 8);
            _orderPanel.Children.Add(Field("MSG", id + (when.Length > 0 ? "   " + when : ""), Color.FromArgb(255, 210, 235, 250)));
            _orderPanel.Children.Add(Field("FROM", "HARNESS", Color.FromArgb(255, 190, 215, 235)));
            _orderPanel.Children.Add(Field("TO", "GAME BRIDGE", Color.FromArgb(255, 190, 215, 235)));
            _orderPanel.Children.Add(Field("PRIORITY", "N/A", Color.FromArgb(255, 150, 175, 200)));
            _orderPanel.Children.Add(Field("SUBJECT", string.IsNullOrEmpty(last.Summary) ? "N/A" : last.Summary, Color.FromArgb(255, 214, 240, 255)));
            string payload = last.InputPayload;
            _orderPanel.Children.Add(Field("BODY", string.IsNullOrEmpty(payload) ? "N/A" : Clean(payload), Color.FromArgb(255, 200, 226, 245)));
            string status = string.IsNullOrEmpty(last.Status) ? "" : last.Status;
            if (string.IsNullOrEmpty(status)) status = last.ErrorMessage == null ? "N/A" : last.ErrorMessage;
            _orderPanel.Children.Add(Field("STATUS", status, Color.FromArgb(255, 255, 214, 130)));
            if (last.DurationMs >= 0)
                _orderPanel.Children.Add(Field("ELAPSED", (long)last.DurationMs + " ms", Color.FromArgb(255, 150, 175, 200)));
        }

        private static TextBlock Field(string label, string value, Color color)
        {
            TextBlock t = new TextBlock();
            t.FontSize = 11;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 1, 0, 1);
            t.Foreground = new SolidColorBrush(color);
            t.Text = label + "  " + value;
            return t;
        }


        /// <summary>
        /// The page's real geometry, so a blank column can be diagnosed instead of guessed:
        /// per child its type, grid position, arranged size and text.
        /// </summary>
        public static JObject Tree()
        {
            JObject o = new JObject();
            o["installed"] = _wrap != null;
            JArray rows = new JArray();
            try { Walk(_wrap, rows, 0); }
            catch (Exception ex) { o["error"] = ex.GetType().Name + ": " + ex.Message; }
            o["nodes"] = rows;
            o["count"] = rows.Count;
            return o;
        }

        private static void Walk(FrameworkElement fe, JArray into, int depth)
        {
            if (fe == null || depth > 6 || into.Count > 400) return;
            JObject n = new JObject();
            n["depth"] = depth;
            n["type"] = fe.GetType().Name;
            n["name"] = fe.Name;
            try { n["row"] = Grid.GetRow(fe); n["col"] = Grid.GetColumn(fe); }
            catch (Exception) { }
            n["w"] = (double)fe.ActualWidth;
            n["h"] = (double)fe.ActualHeight;
            n["vis"] = fe.Visibility == Visibility.Visible;
            TextBlock tb = fe as TextBlock;
            if (tb != null)
            {
                string t = tb.Text;
                if (t == null) t = "";
                if (t.Length > 48) t = t.Substring(0, 48);
                n["text"] = t;
            }
            into.Add(n);
            Panel p = fe as Panel;
            if (p != null)
            {
                for (int i = 0; i < p.Children.Count && i < 40; i++)
                    Walk(p.Children[i] as FrameworkElement, into, depth + 1);
                return;
            }
            Border b = fe as Border;
            if (b != null) Walk(b.Child as FrameworkElement, into, depth + 1);
        }

        private static int AsInt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return (int)t; }
            catch (Exception) { return 0; }
        }

        private static RowDefinition Row(double px)
        {
            return new RowDefinition { Height = new GridLength((float)px) };
        }

        // A star row with weight 0 measures to zero height: GridLength(0, Star) means "0 of the
        // spare space", so every caller that wanted "the rest of the page" got nothing. The weight
        // is clamped to at least 1 so that Row(0, true) really means "whatever is left".
        private static RowDefinition Row(double star, bool starFlag)
        {
            float weight = (float)star;
            if (weight < 1.0f) weight = 1.0f;
            return new RowDefinition { Height = new GridLength(weight, GridUnitType.Star) };
        }

        private static ColumnDefinition Col(double px)
        {
            return new ColumnDefinition { Width = new GridLength((float)px) };
        }

        // ================================================== state
        public static JObject State()
        {
            JObject o = new JObject();
            o["installed"] = _wrap != null;
            o["visible"] = _visible;
            o["toggleKey"] = _keyName;
            o["armed"] = _armed;
            o["typingLock"] = UiBridge.Typing;
            o["status"] = _status;
            o["lastSubmit"] = _lastSubmitNote;
            o["drawn"] = _drawn;
            o["total"] = _total;
            o["scrollBack"] = _scrollBack;
            o["width"] = _wrap == null ? 0 : Math.Round((double)_w, 2);
            o["height"] = _wrap == null ? 0 : Math.Round((double)_h, 2);
            o["actualWidth"] = _wrap == null ? 0 : Math.Round((double)_wrap.ActualWidth, 2);
            o["actualHeight"] = _wrap == null ? 0 : Math.Round((double)_wrap.ActualHeight, 2);
            o["zIndex"] = _wrap == null ? 0 : Noesis.Panel.GetZIndex(_wrap);
            o["hostChildren"] = _host == null ? 0 : _host.Children.Count;
            o["inputText"] = _input == null ? null : Convert.ToString(_input.Text);
            o["agentName"] = CommsBridge.AgentName;
            o["agentCall"] = CommsBridge.AgentCall;
            o["note"] = "the page never fabricates an agent sentence: the left column is the "
                + "decoded transcript, the right column is the bridge's own action log";
            o["paintSig"] = _paintSig;
            return o;
        }

        // ================================================== setting.ini
        private static string Setting(string key, string dflt)
        {
            try
            {
                string dir = Core.ModDir();
                if (string.IsNullOrEmpty(dir)) return dflt;
                string path = System.IO.Path.Combine(dir, "setting.ini");
                if (!System.IO.File.Exists(path)) return dflt;
                string section = null;
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        section = close > 1 ? line.Substring(1, close - 1).Trim() : null;
                        continue;
                    }
                    if (section == null) continue;
                    if (!string.Equals(section, CommsBridge.Section, StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = line.Substring(eq + 1).Trim();
                    return v.Length == 0 ? dflt : v;
                }
            }
            catch (Exception) { }
            return dflt;
        }
    }
}
