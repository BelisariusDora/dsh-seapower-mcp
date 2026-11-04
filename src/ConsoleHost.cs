using System;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// One console window, browser shaped: a title bar with minimise / maximise / close on the
    /// right, a tab row underneath, and the active page filling the rest. "N" opens or collapses
    /// the whole thing; switching tabs is a click, not a key.
    ///
    /// Why a shell instead of two windows with a floating strip (the 2026-11-02 first attempt):
    /// the operator's reference was explicitly a browser, so the tabs have to live inside the
    /// same frame as the content and the frame has to resize. A strip mounted on its own could
    /// be covered by whatever the game draws there, and each page carried a second set of window
    /// buttons, which reads as two overlapping windows.
    ///
    /// Why the pages are re-parented instead of rewritten: InspectorWindow and CommsWindow each
    /// build a complete tree and mount it themselves, and both have already proved their
    /// geometry, typing and painting rules in game. They now accept an alternate host and fill
    /// it (see FillContentArea on each page), so this shell owns only the frame.
    /// </summary>
    public static class ConsoleHost
    {
        public const string PageInspector = "inspector";
        public const string PageComms = "comms";

        private const string ShellName = "DshConsoleShell";
        private const double TitleH = 26.0;
        private const double FrameBorder = 1.0;
        private const double MaximizeInset = 4.0;
        [Flags]
        private enum ResizeEdge { Left = 1, Right = 2, Top = 4, Bottom = 8 }
        private const double TabH = 26.0;
        private const double MinW = 520.0;
        private const double MinH = 300.0;
        private const double DefaultW = 1180.0;
        private const double DefaultH = 700.0;

        /// The page the operator used last. Comms is the one a pilot reaches for; the
        /// inspector is the debugging view, so comms is the default tab.
        private static string _active = PageComms;

        /// Whether the console is open at all. False means "N collapsed it": the shell and
        /// both pages are unmounted, and pressing N brings back the last tab.
        private static bool _open;

        private static bool _maximized;
        /// Rolled up to the title bar. The operator pressed '-' expecting a minimise and
        /// got a close, so the shell keeps its title bar and collapses the rest instead.
        private static bool _minimized;
        private static double _w = DefaultW;
        private static double _h = DefaultH;
        private static Panel _host;
        private static FrameworkElement _tacticalRoot;
        private static Grid _resizeOverlay;
        private static Border _shell;
        private static Grid _content;
        private static Grid _rows;
        // Last geometry actually written to the tree. Re-assigning every frame forces a
        // full layout of a window that can hold hundreds of rows, which is what made a
        // maximised console feel frozen.
        private static double _appliedW = -1.0;
        private static double _appliedH = -1.0;
        private static double _appliedL = double.NaN;
        private static double _appliedT = double.NaN;
        private static bool _appliedMax;
        private static bool _appliedMin;
        // Content well size the active page was last filled at.
        private static double _lastContentW = -1.0;
        private static double _lastContentH = -1.0;
        private static Border _cellInspector;
        private static Border _cellComms;
        private static TextBlock _labelInspector;
        private static TextBlock _labelComms;
        private static TextBlock _linkText;
        private static TextBlock _genText;
        private static string _paintedKey;
        private static string _lastError = "";
        private static float _lastAttempt = -100f;
        private static bool _resizing;
        private static ResizeEdge _resizeEdge;
        private static double _resizeFromLeft;
        private static double _resizeFromTop;
        private static double _resizeFromW;
        private static double _resizeFromH;
        private static double _resizeFromX;
        private static double _resizeFromY;
        // Top-left of the frame inside the host. NaN means "never placed yet", which is how
        // the first paint knows to centre instead of landing at 0,0.
        private static double _left = double.NaN;
        private static double _top = double.NaN;
        private static bool _dragging;
        private static double _dragFromX;
        private static double _dragFromY;
        private static double _dragFromLeft;
        private static double _dragFromTop;
        // The element that captured the mouse. Capturing the shell instead would send every
        // move to the shell and the grip would never see another MouseMove - that is why the
        // first resize attempt felt dead.
        private static UIElement _capture;

        // ================================================== lifecycle

        public static void Tick()
        {
            try
            {
                EnsureShell();
                MountPages();
                SyncContentWell();
                Paint();
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>Called from the key poll for every key press. Returns true when used.</summary>
        public static bool HandleKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool typing = false;
            try { typing = UiBridge.Typing; }
            catch (Exception) { }
            if (typing) return false;

            if (string.Equals(name.Trim(), "N", StringComparison.OrdinalIgnoreCase))
            {
                // A rolled-up console answers N by unrolling, not by closing: otherwise the
                // title bar the operator can see would not be the thing the key acts on.
                if (_open && _minimized) Minimize();
                else Toggle();
                return true;
            }
            return false;
        }

        public static JObject Toggle()
        {
            _open = !_open;
            if (!_open) { _maximized = false; _minimized = false; }
            _lastAttempt = -100f;
            Tick();
            return State();
        }

        public static JObject Open()
        {
            _open = true;
            _lastAttempt = -100f;
            Tick();
            return State();
        }

        public static JObject Close()
        {
            _open = false;
            _maximized = false;
            _minimized = false;
            Tick();
            return State();
        }

        public static JObject Next()
        {
            return Show(Other(_active));
        }

        public static JObject Maximize()
        {
            _maximized = !_maximized;
            if (_maximized) _minimized = false;
            _paintedKey = null;
            ApplyShellGeometry();
            ResizeActivePage();
            Tick();
            return State();
        }

        /// <summary>Roll the shell up to its title bar. Pressing it again unrolls it.</summary>
        public static JObject Minimize()
        {
            _minimized = !_minimized;
            if (_minimized) _maximized = false;
            _paintedKey = null;
            ApplyShellGeometry();
            ResizeActivePage();
            Tick();
            return State();
        }

        /// <summary>Back to the default size, centred. Escapes a size that got unusable.</summary>
        public static JObject Restore()
        {
            _maximized = false;
            _minimized = false;
            _w = DefaultW;
            _h = DefaultH;
            _left = double.NaN;
            _top = double.NaN;
            _paintedKey = null;
            ApplyShellGeometry();
            ResizeActivePage();
            Tick();
            return State();
        }

        /// <summary>Explicit size. The shell has no other way to be told one from outside.</summary>
        public static JObject SetSize(JObject req)
        {
            double w = _w;
            double h = _h;
            try
            {
                if (req != null && req["width"] != null) w = Convert.ToDouble(req["width"]);
                if (req != null && req["height"] != null) h = Convert.ToDouble(req["height"]);
            }
            catch (Exception) { }
            if (w < MinW) w = MinW;
            if (h < MinH) h = MinH;
            _w = w;
            _h = h;
            _maximized = false;
            _minimized = false;
            _left = double.NaN;
            _top = double.NaN;
            _paintedKey = null;
            ApplyShellGeometry();
            ResizeActivePage();
            Tick();
            return State();
        }

        public static JObject Show(string page)
        {
            if (page != PageInspector && page != PageComms) return State();
            if (_active != page)
            {
                _paintedKey = null;
                Remove(Other(page));
            }
            _active = page;
            _open = true;
            _lastAttempt = -100f;
            Tick();
            return State();
        }

        public static JObject State()
        {
            JObject o = new JObject();
            o["host"] = "console";
            o["open"] = _open;
            o["active"] = _active;
            o["tabs"] = new JArray(new object[] { "AGENT INSPECTOR", "HARNESS COMMS" });
            o["switchBy"] = "click a tab in the window tab row";
            o["hotkey"] = "N";
            o["titleButtons"] = new JArray(new object[] { "minimize", "maximize", "close" });
            o["resizable"] = true;
            o["resizeBy"] = "drag any edge or corner; maximize fits the visible tactical area";
            o["maximized"] = _maximized;
            o["minimized"] = _minimized;
            o["rect"] = Rect();
            o["shellMounted"] = _shell != null && _shell.Parent != null;
            o["inspectorInstalled"] = IsInstalled(PageInspector);
            o["commsInstalled"] = IsInstalled(PageComms);
            if (_lastError.Length > 0) o["lastError"] = _lastError;
            return o;
        }

        public static JObject Dispatch(JObject req)
        {
            string op = req == null ? "" : Convert.ToString(req["op"]).Trim().ToLowerInvariant();
            if (op.Length == 0) op = "state";
            if (op == "state") return State();
            if (op == "toggle") return Toggle();
            if (op == "next" || op == "tab") return Next();
            if (op == "open") return Open();
            if (op == "close") return Close();
            if (op == "maximize") return Maximize();
            if (op == "minimize") return Minimize();
            if (op == "restore") return Restore();
            if (op == "size") return SetSize(req);
            if (op == "show")
            {
                string page = Convert.ToString(req["page"]);
                if (page != PageInspector && page != PageComms)
                {
                    throw new ApiException(400, "bad_value",
                        "page must be 'inspector' or 'comms'", false);
                }
                return Show(page);
            }
            throw new ApiException(404, "bad_op", "unknown console command: " + op
                + " (expected state, toggle, next, open, close, maximize, minimize, restore, size or show)", false);
        }

        private static JObject Rect()
        {
            JObject r = new JObject();
            r["w"] = Math.Round(_appliedW >= 0 ? _appliedW : _w);
            r["h"] = Math.Round(_appliedH >= 0 ? _appliedH : _h);
            double hostW = _host == null ? 0 : Math.Round((double)_host.ActualWidth);
            double hostH = _host == null ? 0 : Math.Round((double)_host.ActualHeight);
            r["hostW"] = hostW;
            r["hostH"] = hostH;
            r["left"] = double.IsNaN(_appliedL) ? JValue.CreateNull() : new JValue(Math.Round(_appliedL));
            r["top"] = double.IsNaN(_appliedT) ? JValue.CreateNull() : new JValue(Math.Round(_appliedT));
            return r;
        }

        private static string Other(string page)
        {
            return page == PageInspector ? PageComms : PageInspector;
        }

        // ================================================== shell

        private static void EnsureShell()
        {
            if (!_open)
            {
                Detach();
                return;
            }

            // A reloaded mission destroys the tactical view and takes the shell with it; the
            // leftover reference has no parent, so start over instead of painting a dead tree.
            if (_shell != null)
            {
                try
                {
                    if (_shell.Parent == null) Detach();
                }
                catch (Exception) { Detach(); }
                if (_shell != null) return;
            }

            FrameworkElement root = UiBridge.TacticalRootForInspector();
            if (root == null) return;
            object found = UiBridge.FindNamedForInspector(root, "DisplayAreaGrid");
            Panel host = found as Panel;
            if (host == null) return;

            _host = host;
            _tacticalRoot = root;
            BuildShell();
            if (_shell == null) return;
            try
            {
                _host.Children.Add(_shell);
                // Above both pages: the shell owns the frame and the tab row, so nothing the
                // content draws may cover it.
                Noesis.Panel.SetZIndex(_shell, 10003);
                _lastError = "";
            }
            catch (Exception ex)
            {
                _shell = null;
                _lastError = "shell mount failed: " + ex.GetType().Name;
            }
        }

        private static void Detach()
        {
            if (_shell == null) return;
            Remove(PageInspector);
            Remove(PageComms);
            try
            {
                Panel parent = _shell.Parent as Panel;
                if (parent != null) parent.Children.Remove(_shell);
            }
            catch (Exception) { }
            _shell = null;
            _tacticalRoot = null;
            _resizeOverlay = null;
            _content = null;
            _cellInspector = null;
            _cellComms = null;
            _labelInspector = null;
            _labelComms = null;
            _linkText = null;
            _genText = null;
            _rows = null;
            _paintedKey = null;
            _appliedW = -1.0;
            _appliedH = -1.0;
            _appliedL = double.NaN;
            _appliedT = double.NaN;
            _lastContentW = -1.0;
            _lastContentH = -1.0;
            try { CommsWindow.ShellHost = null; } catch (Exception) { }
            try { InspectorWindow.ShellHost = null; } catch (Exception) { }
        }

        private static void BuildShell()
        {
            Border frame = new Border();
            frame.Name = ShellName;
            frame.Background = UIBrush.Solid(Color.FromArgb(238, 7, 16, 26));
            frame.BorderBrush = UIBrush.Solid(Color.FromArgb(255, 64, 128, 176));
            frame.BorderThickness = new Thickness((float)FrameBorder);
            frame.ClipToBounds = true;
            // Placed by margin rather than centred by alignment: the operator drags the
            // title bar to move the window, so the shell has to own its own top-left.
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.VerticalAlignment = VerticalAlignment.Top;
            frame.IsHitTestVisible = true;

            Grid rows = new Grid();
            rows.RowDefinitions.Add(Row(TitleH));
            rows.RowDefinitions.Add(Row(TabH));
            rows.RowDefinitions.Add(Row(1, true));
            frame.Child = rows;

            // ---- title bar: callsign and link on the left, window buttons on the right
            Border title = new Border();
            title.Background = UIBrush.Solid(Color.FromArgb(255, 10, 26, 42));
            Grid.SetRow(title, 0);
            rows.Children.Add(title);

            title.IsHitTestVisible = true;
            title.MouseLeftButtonDown += OnTitleDown;
            title.MouseMove += OnTitleMove;
            title.MouseLeftButtonUp += OnTitleUp;

            Grid titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(Col(1, true));
            titleGrid.ColumnDefinitions.Add(Col(88));
            title.Child = titleGrid;

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.Margin = new Thickness(8, 0, 0, 0);
            left.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(left, 0);
            titleGrid.Children.Add(left);

            TextBlock name = new TextBlock();
            name.Text = "TACTICAL ADVISOR";
            name.Foreground = UIBrush.Solid(Color.FromArgb(255, 222, 238, 250));
            name.FontSize = 12;
            name.Margin = new Thickness(0, 0, 12, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(name);

            _linkText = new TextBlock();
            _linkText.Text = "LINK -";
            _linkText.Foreground = UIBrush.Solid(Color.FromArgb(255, 128, 180, 214));
            _linkText.FontSize = 11;
            _linkText.Margin = new Thickness(0, 0, 12, 0);
            _linkText.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(_linkText);

            _genText = new TextBlock();
            _genText.Text = "IDLE";
            _genText.Foreground = UIBrush.Solid(Color.FromArgb(255, 142, 202, 162));
            _genText.FontSize = 11;
            _genText.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(_genText);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Margin = new Thickness(0, 0, 10, 0);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons, 1);
            titleGrid.Children.Add(buttons);
            buttons.Children.Add(TitleButton("-", OnMinimize));
            buttons.Children.Add(TitleButton("O", OnMaximize));
            buttons.Children.Add(TitleButton("X", OnClose));

            // ---- tab row
            Border tabs = new Border();
            tabs.Background = UIBrush.Solid(Color.FromArgb(255, 8, 20, 33));
            Grid.SetRow(tabs, 1);
            rows.Children.Add(tabs);

            StackPanel tabRow = new StackPanel();
            tabRow.Orientation = Orientation.Horizontal;
            tabRow.Margin = new Thickness(5, 2, 0, 0);
            tabs.Child = tabRow;

            _labelInspector = new TextBlock();
            _labelComms = new TextBlock();
            _cellInspector = TabCell(_labelInspector, "AGENT INSPECTOR", OnClickInspector);
            tabRow.Children.Add(_cellInspector);
            _cellComms = TabCell(_labelComms, "HARNESS COMMS", OnClickComms);
            tabRow.Children.Add(_cellComms);

            // ---- content well: the active page fills this with its own chrome hidden
            Grid content = new Grid();
            Grid.SetRow(content, 2);
            rows.Children.Add(content);
            _content = content;

            _resizeOverlay = new Grid();
            Grid.SetRowSpan(_resizeOverlay, 3);
            Panel.SetZIndex(_resizeOverlay, 20);
            rows.Children.Add(_resizeOverlay);
            AddResizeGrip(ResizeEdge.Left);
            AddResizeGrip(ResizeEdge.Right);
            AddResizeGrip(ResizeEdge.Top);
            AddResizeGrip(ResizeEdge.Bottom);
            AddResizeGrip(ResizeEdge.Top | ResizeEdge.Left);
            AddResizeGrip(ResizeEdge.Top | ResizeEdge.Right);
            AddResizeGrip(ResizeEdge.Bottom | ResizeEdge.Left);
            AddResizeGrip(ResizeEdge.Bottom | ResizeEdge.Right);
            _rows = rows;

            _shell = frame;
            _paintedKey = null;
            ApplyShellGeometry();
        }

        private static void AddResizeGrip(ResizeEdge edge)
        {
            bool left = (edge & ResizeEdge.Left) != 0;
            bool right = (edge & ResizeEdge.Right) != 0;
            bool top = (edge & ResizeEdge.Top) != 0;
            bool bottom = (edge & ResizeEdge.Bottom) != 0;
            bool corner = (left || right) && (top || bottom);
            var grip = new Border();
            grip.Background = UIBrush.Solid(Color.FromArgb(1, 74, 126, 166));
            grip.HorizontalAlignment = left ? HorizontalAlignment.Left :
                right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            grip.VerticalAlignment = top ? VerticalAlignment.Top :
                bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            if (left || right) grip.Width = corner ? 12f : 5f;
            if (top || bottom) grip.Height = corner ? 12f : 5f;
            grip.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { OnGripDown(sender, e, edge); };
            grip.MouseMove += OnGripMove;
            grip.MouseLeftButtonUp += OnGripUp;
            _resizeOverlay.Children.Add(grip);
        }

        private static Border TitleButton(string text, MouseButtonEventHandler onClick)
        {
            Border b = new Border();
            // A Border with no Background is not hit tested. The first version's tabs could be
            // seen but not clicked for exactly this reason, so every clickable cell paints.
            b.Background = UIBrush.Solid(Color.FromArgb(255, 16, 34, 52));
            b.BorderBrush = UIBrush.Solid(Color.FromArgb(255, 58, 106, 146));
            b.BorderThickness = new Thickness(1);
            b.Width = 20f;
            b.Height = 17f;
            b.Margin = new Thickness(2, 0, 0, 0);
            b.IsHitTestVisible = true;
            b.MouseLeftButtonDown += onClick;

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11;
            t.Foreground = UIBrush.Solid(Color.FromArgb(255, 212, 230, 246));
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        private static Border TabCell(TextBlock label, string text, MouseButtonEventHandler onClick)
        {
            Border cell = new Border();
            cell.BorderThickness = new Thickness(1);
            cell.Padding = new Thickness(10, 2, 10, 3);
            cell.Margin = new Thickness(0, 0, 2, 0);
            cell.IsHitTestVisible = true;
            cell.MouseLeftButtonDown += onClick;

            label.Text = "    " + text;
            label.FontSize = 11;
            cell.Child = label;
            return cell;
        }

        // ================================================== painting

        private static void Paint()
        {
            if (_shell == null) return;
            ApplyShellGeometry();

            string key = _active + "|" + (IsInstalled(_active) ? "1" : "0") + "|" + _maximized;
            if (!string.IsNullOrEmpty(_paintedKey) && key == _paintedKey) return;
            _paintedKey = key;

            bool onInspector = _active == PageInspector;
            PaintTab(_cellInspector, _labelInspector, "AGENT INSPECTOR", onInspector);
            PaintTab(_cellComms, _labelComms, "HARNESS COMMS", !onInspector);

            JObject st = null;
            try { st = CommsBridge.State(); }
            catch (Exception) { }
            if (_linkText != null)
            {
                string link = st == null ? "" : Convert.ToString(st["link"]);
                if (string.IsNullOrEmpty(link)) link = "-";
                _linkText.Text = "LINK " + link.ToUpperInvariant();
            }
            if (_genText != null)
            {
                string gen = st == null ? "" : Convert.ToString(st["generation"]);
                if (string.IsNullOrEmpty(gen)) gen = "idle";
                string tool = st == null ? "" : Convert.ToString(st["lastTool"]);
                string tail = string.IsNullOrEmpty(tool) ? "" : " " + tool.ToUpperInvariant();
                _genText.Text = gen.ToUpperInvariant() + tail;
            }
        }

        private static void PaintTab(Border cell, TextBlock label, string text, bool on)
        {
            if (cell == null || label == null) return;
            cell.Background = UIBrush.Solid(on
                ? Color.FromArgb(255, 22, 48, 72)
                : Color.FromArgb(255, 12, 24, 38));
            cell.BorderBrush = UIBrush.Solid(on
                ? Color.FromArgb(255, 108, 176, 220)
                : Color.FromArgb(255, 40, 70, 96));
            label.Foreground = UIBrush.Solid(on
                ? Color.FromArgb(255, 238, 247, 255)
                : Color.FromArgb(255, 132, 168, 196));
            label.Text = (on ? "  \u25B8 " : "    ") + text;
        }

        private static Noesis.Rect VisibleHostBounds()
        {
            float w = _host == null ? 0 : (float)_host.ActualWidth;
            float h = _host == null ? 0 : (float)_host.ActualHeight;
            var bounds = new Noesis.Rect(0, 0, w, h);
            if (w <= 0 || h <= 0 || _tacticalRoot == null) return bounds;
            float rootW = (float)_tacticalRoot.ActualWidth;
            float rootH = (float)_tacticalRoot.ActualHeight;
            if (rootW <= 0 || rootH <= 0) return bounds;
            Noesis.Rect mapped = _host.TransformToAncestor(_tacticalRoot).TransformBounds(bounds);
            if (mapped.Width <= 0 || mapped.Height <= 0) return bounds;
            double left = Math.Max(0, -mapped.X * w / mapped.Width);
            double top = Math.Max(0, -mapped.Y * h / mapped.Height);
            double right = Math.Min(w, (rootW - mapped.X) * w / mapped.Width);
            double bottom = Math.Min(h, (rootH - mapped.Y) * h / mapped.Height);
            return new Noesis.Rect((float)left, (float)top,
                (float)Math.Max(0, right - left), (float)Math.Max(0, bottom - top));
        }

        private static void ApplyShellGeometry()
        {
            if (_shell == null) return;
            Noesis.Rect bounds = VisibleHostBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            double inset = _maximized && bounds.Width > 2 * MaximizeInset && bounds.Height > 2 * MaximizeInset
                ? MaximizeInset : 0.0;
            double w = _maximized ? bounds.Width - 2 * inset : Math.Min(Math.Max(_w, MinW), bounds.Width);
            double h = _maximized ? bounds.Height - 2 * inset : _minimized
                ? Math.Min(TitleH + 2 * FrameBorder, bounds.Height)
                : Math.Min(Math.Max(_h, MinH), bounds.Height);
            double left = _maximized ? bounds.X + inset : double.IsNaN(_left)
                ? bounds.X + (bounds.Width - w) / 2 : _left;
            double top = _maximized ? bounds.Y + inset : double.IsNaN(_top)
                ? bounds.Y + (bounds.Height - h) / 2 : _top;
            left = Math.Max(bounds.X, Math.Min(left, bounds.X + bounds.Width - w));
            top = Math.Max(bounds.Y, Math.Min(top, bounds.Y + bounds.Height - h));
            if (!_maximized)
            {
                _left = left;
                _top = top;
            }

            bool same = Math.Abs(w - _appliedW) < 0.5 && Math.Abs(h - _appliedH) < 0.5
                && Math.Abs(left - _appliedL) < 0.5 && Math.Abs(top - _appliedT) < 0.5
                && _appliedMax == _maximized && _appliedMin == _minimized;
            if (same) return;
            _appliedW = w;
            _appliedH = h;
            _appliedL = left;
            _appliedT = top;
            _appliedMax = _maximized;
            _appliedMin = _minimized;

            _rows.RowDefinitions[1].Height = _minimized ? new GridLength(0f) : new GridLength((float)TabH);
            _rows.RowDefinitions[2].Height = _minimized ? new GridLength(0f) : new GridLength(1f, GridUnitType.Star);
            _content.Visibility = _minimized ? Visibility.Collapsed : Visibility.Visible;
            _resizeOverlay.Visibility = _minimized || _maximized ? Visibility.Collapsed : Visibility.Visible;
            _shell.Width = (float)w;
            _shell.Height = (float)h;
            _shell.Margin = new Thickness((float)left, (float)top, 0, 0);
            try { _shell.UpdateLayout(); } catch (Exception) { }
        }

        // ================================================== clicks

        private static void OnMinimize(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Minimize();
        }

        private static void OnMaximize(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Maximize();
        }

        private static void OnClose(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Close();
        }

        private static void OnClickInspector(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Show(PageInspector);
        }

        private static void OnClickComms(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Show(PageComms);
        }

        // ================================================== move

        private static void OnTitleDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_shell == null || _maximized) return;
            _dragging = true;
            _dragFromLeft = _left;
            _dragFromTop = _top;
            Point p = e.GetPosition(_host);
            _dragFromX = p.X;
            _dragFromY = p.Y;
            _capture = sender as UIElement;
            try { if (_capture != null) _capture.CaptureMouse(); }
            catch (Exception) { }
        }

        private static void OnTitleMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _shell == null || _host == null) return;
            e.Handled = true;
            Point p = e.GetPosition(_host);
            Noesis.Rect bounds = VisibleHostBounds();
            double left = Math.Max(bounds.X, Math.Min(_dragFromLeft + (p.X - _dragFromX),
                bounds.X + bounds.Width - _appliedW));
            double top = Math.Max(bounds.Y, Math.Min(_dragFromTop + (p.Y - _dragFromY),
                bounds.Y + bounds.Height - _appliedH));
            if (Math.Abs(left - _left) < 1.0 && Math.Abs(top - _top) < 1.0) return;
            _left = left;
            _top = top;
            _paintedKey = null;
            ApplyShellGeometry();
        }

        private static void OnTitleUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            try { if (_capture != null) _capture.ReleaseMouseCapture(); }
            catch (Exception) { }
            _capture = null;
        }

        // ================================================== resize

        private static void OnGripDown(object sender, MouseButtonEventArgs e, ResizeEdge edge)
        {
            if (_shell == null || _host == null || _maximized || _minimized) return;
            e.Handled = true;
            _resizing = true;
            _resizeEdge = edge;
            _resizeFromLeft = _appliedL;
            _resizeFromTop = _appliedT;
            _resizeFromW = _appliedW;
            _resizeFromH = _appliedH;
            Point p = e.GetPosition(_host);
            _resizeFromX = p.X;
            _resizeFromY = p.Y;
            _capture = sender as UIElement;
            try { if (_capture != null) _capture.CaptureMouse(); }
            catch (Exception) { }
        }

        private static void ResizeAxis(ref double start, ref double end, double delta,
            double min, double lower, double upper, bool moveStart, bool moveEnd)
        {
            if (moveStart) start = Math.Max(lower, Math.Min(start + delta, end - min));
            if (moveEnd) end = Math.Min(upper, Math.Max(end + delta, start + min));
        }

        private static void OnGripMove(object sender, MouseEventArgs e)
        {
            if (!_resizing || _shell == null || _host == null) return;
            e.Handled = true;
            Point p = e.GetPosition(_host);
            Noesis.Rect bounds = VisibleHostBounds();
            double left = _resizeFromLeft;
            double top = _resizeFromTop;
            double right = left + _resizeFromW;
            double bottom = top + _resizeFromH;
            ResizeAxis(ref left, ref right, p.X - _resizeFromX, Math.Min(MinW, bounds.Width),
                bounds.X, bounds.X + bounds.Width,
                (_resizeEdge & ResizeEdge.Left) != 0, (_resizeEdge & ResizeEdge.Right) != 0);
            ResizeAxis(ref top, ref bottom, p.Y - _resizeFromY, Math.Min(MinH, bounds.Height),
                bounds.Y, bounds.Y + bounds.Height,
                (_resizeEdge & ResizeEdge.Top) != 0, (_resizeEdge & ResizeEdge.Bottom) != 0);
            _left = left;
            _top = top;
            _w = right - left;
            _h = bottom - top;
            ApplyShellGeometry();
            ResizeActivePage();
        }

        private static void OnGripUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _resizing = false;
            try { if (_capture != null) _capture.ReleaseMouseCapture(); }
            catch (Exception) { }
            _capture = null;
        }

        // ================================================== pages

        private static void MountPages()
        {
            if (!_open || _content == null) return;

            if (IsInstalled(Other(_active))) Remove(Other(_active));

            if (IsInstalled(_active))
            {
                ResizeActivePage();
                return;
            }

            float now = Now();
            if (now - _lastAttempt < 0.5f) return;
            _lastAttempt = now;
            JObject r = Install(_active);
            string err = r == null ? "no result" : Convert.ToString(r["error"]);
            _lastError = string.IsNullOrEmpty(err) ? "" : err;
            ResizeActivePage();
        }

        /// The page sizes itself from the content well, so when the well changes size (a
        /// maximise, a grip drag, a minimise) the page has to be filled again. Without this
        /// the page kept its pre-maximise size and painted into a clipped box.
        private static void SyncContentWell()
        {
            if (_content == null) return;
            double cw = (double)_content.ActualWidth;
            double ch = (double)_content.ActualHeight;
            if (Math.Abs(cw - _lastContentW) < 0.5 && Math.Abs(ch - _lastContentH) < 0.5) return;
            _lastContentW = cw;
            _lastContentH = ch;
            ResizeActivePage();
        }

        /// <summary>The page fills the content well; its own title row is collapsed.</summary>
        private static void ResizeActivePage()
        {
            if (_content == null || _minimized) return;
            try
            {
                if (_active == PageInspector) InspectorWindow.FillContentArea(_content);
                else CommsWindow.FillContentArea(_content);
                try { if (_content != null) _content.UpdateLayout(); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                _lastError = "fill: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static float Now()
        {
            try { return UnityEngine.Time.realtimeSinceStartup; }
            catch (Exception) { return 0f; }
        }

        private static bool IsInstalled(string page)
        {
            try
            {
                return page == PageInspector ? InspectorWindow.IsInstalled : CommsWindow.IsInstalled;
            }
            catch (Exception) { return false; }
        }

        private static JObject Install(string page)
        {
            try
            {
                return page == PageInspector ? InspectorWindow.Install() : CommsWindow.Install();
            }
            catch (ApiException ex)
            {
                JObject o = new JObject();
                o["error"] = ex.Code + ": " + ex.Message;
                return o;
            }
            catch (Exception ex)
            {
                JObject o = new JObject();
                o["error"] = ex.GetType().Name + ": " + ex.Message;
                return o;
            }
        }

        private static void Remove(string page)
        {
            try
            {
                if (page == PageInspector) InspectorWindow.Remove();
                else CommsWindow.Remove();
            }
            catch (Exception) { }
        }

        /// <summary>Footer text both pages print, so their tab row matches the shell.</summary>
        public static string Strip()
        {
            bool onInspector = _active == PageInspector;
            string a = onInspector ? " \u25B8 AGENT INSPECTOR" : "   AGENT INSPECTOR";
            string b = onInspector ? "   HARNESS COMMS" : " \u25B8 HARNESS COMMS";
            return "[" + a + "] [" + b + "]";
        }

        // ================================================== layout helpers

        private static RowDefinition Row(double h)
        {
            RowDefinition r = new RowDefinition();
            r.Height = new GridLength((float)h);
            return r;
        }

        private static RowDefinition Row(double star, bool starFlag)
        {
            RowDefinition r = new RowDefinition();
            float weight = (float)star;
            if (weight < 1.0f) weight = 1.0f;
            r.Height = new GridLength(weight, GridUnitType.Star);
            return r;
        }

        private static ColumnDefinition Col(double w)
        {
            ColumnDefinition c = new ColumnDefinition();
            c.Width = new GridLength((float)w);
            return c;
        }

        private static ColumnDefinition Col(double star, bool starFlag)
        {
            ColumnDefinition c = new ColumnDefinition();
            float weight = (float)star;
            if (weight < 1.0f) weight = 1.0f;
            c.Width = new GridLength(weight, GridUnitType.Star);
            return c;
        }
    }

    /// <summary>Brush factory, so the shell does not need a SolidColorBrush per call site.</summary>
    internal static class UIBrush
    {
        internal static SolidColorBrush Solid(Color c)
        {
            return new SolidColorBrush(c);
        }
    }
}
