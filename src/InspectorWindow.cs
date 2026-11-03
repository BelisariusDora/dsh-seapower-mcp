// ACT-010 / I1: the Agent Inspector window, built entirely in code.
//
// Why not XAML: the first attempt went through Noesis.GUI.ParseXaml and the labels
// came out as mojibake on screen. Whatever parsed, the bytes that reached the
// renderer were not the bytes we sent, and there is no theme or resource pipeline
// reachable from here to fix it (Noesis.Application does not exist in this build).
// Building the tree by hand keeps every string a managed string until Noesis sees
// it, and makes every colour, size and cursor explicit instead of inherited.
//
// Why every draggable area is a Border and not a Thumb/GridSplitter/ScrollViewer:
// a Control only paints what its control template paints, and this build has no
// FrameworkElementFactory and no ControlTemplate.VisualTree, so a template cannot
// be supplied from code. Border and TextBlock are the two primitives that always
// render. So the title bar, the eight resize handles and the splitter are all
// Borders with explicit Background plus mouse capture; a StackPanel is scrolled by
// rebuilding its children at an offset instead of by a ScrollViewer.
//
// Why geometry is stored as ratios: setting.ini is written by a human and read on a
// different monitor resolution more often than not. Ratios land the window in the
// same relative place; pixels would put it off screen.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Noesis;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    /// <summary>
    /// The Inspector window. Read-only view over <see cref="InspectorBuffer"/> plus
    /// window chrome. It never issues a game command: everything it shows came from
    /// the buffer that Api already records, and the buttons only move the window.
    /// </summary>
    public static class InspectorWindow
    {
        public const string WindowName = "DshInspectorWindow";

        private const double TitleHeight = 26;
        private const double ToolbarHeight = 22;
        private const double SplitHeight = 5;
        private const double HandleThick = 6;
        private const double CornerSize = 12;
        // Big enough for the four stacked rows: title + toolbar + a few event rows
        // + the splitter + a usable detail pane. The first floor (320x160) was
        // reachable by dragging a corner and left the window painted but empty -
        // a solid block with nothing in it, which reads as "broken" rather than
        // "too small".
        // A 640 floor was being reached almost immediately on a 1920 wide viewport,
        // which read as "the resize is stuck". The panes truncate instead.
        private const double MinW = 520;
        private const double MinH = 320;
        private const double RowHeight = 17;
        private const int MaxRowsDrawn = 400;

        // ---- space budget (all in the window's own logical pixels) ----------
        // These are the numbers the split is derived from. They are subtracted from
        // the CONTENT area - the outer box minus the 1px border, the title bar and
        // the toolbar - because that is the only space the panes can actually use.
        // Deriving them from the outer box (the old way) let a 320px window ask the
        // timeline for 180px and leave the detail band 30px, which is what a pane
        // painted over by its neighbour looked like.
        private const double MinTimelinePx = 60;
        private const double MinDetailPx = 80;
        private const double MinDetailXPx = 160;
        private const double MinActionPx = 200;

        // ---- window state -------------------------------------------------
        private static Grid _wrap;
        private static Border _outer;
        private static Border _timelineBox;
        private static StackPanel _rowsPanel;
        private static Border _detailBox;
        private static TextBlock _detailText;
        // The detail pane is paged as a STREAM, not as a slide of source lines, and its
        // footer lives in its own grid row instead of being appended to the body. Astra,
        // round three: appending the footer to the text meant a full page had nowhere to
        // put it (it landed on the pane edge), and paging by whole source lines meant a
        // single very long line could never be continued - it was cut and its remainder
        // was unreachable. Both are fixed by paging the text itself: every page is a
        // contiguous slice of the body, so nothing can be skipped and everything is
        // reachable by scrolling forward.
        private static TextBlock _detailFooter;
        private static Grid _band;
        private static readonly System.Collections.Generic.List<DPage> _pages =
            new System.Collections.Generic.List<DPage>();
        private static int _pageIndex;
        // Rebuild key: the pages depend on the body text and on the pane's width and
        // height, so a resize re-pages on the next paint instead of showing a page that
        // was cut for the old size.
        private static string _pagesKey = "";
        private static TextBlock _statusText;
        private static TextBlock _titleText;
        // Caption over the action pane. Held as a field so a language switch can
        // relabel it in place instead of tearing the tree down and rebuilding it.
        private static TextBlock _actionsCaption;
        private static Panel _host;

        /// The console shell's content well. Non-null means "embedded": the page fills
        /// that well, its own title row folds (the shell draws minimise/maximise/close)
        /// and it no longer centres itself in DisplayAreaGrid.
        internal static Panel ShellHost;
        private static bool _embedded;
        private static RowDefinition _titleRow;
        // DisplayAreaGrid is the map area, not the whole screen: it is inset from the
        // right and bottom edges, so a window dragged past it gets clipped there.
        private static Panel _displayHost;

        private static double _left = 40, _top = 40, _w = 760, _h = 460;
        private static double _savedLeft = 40, _savedTop = 40, _savedW = 760, _savedH = 460;
        private static double _splitPx = 240;
        // What the player actually asked for. The two values above are the clamped,
        // effective ones; a window that shrinks past the bound must not overwrite the
        // wish, otherwise growing back does not restore the divider.
        private static double _splitPxPref = 240;
        // Width of the right-hand "input and action" pane inside the detail band.
        private static double _splitXPx = 360;
        private static double _splitXPxPref = 360;
        private static double _dragSplitX;
        private static string _fontFamily = "";
        private static int _actionsDrawn;
        private static Border _splitXBar;
        private static Border _splitYBar;
        private static StackPanel _actionPanel;
        private static ColumnDefinition _rightCol;
        private static bool _maximized;
        private static bool _collapsed;
        private static bool _visible;

        private static InspectorConfig _cfg;

        // ---- drag state ---------------------------------------------------
        private static string _dragMode = "";
        private static bool _gameInputOff;
        // Which maps were actually enabled when the drag started: only those get
        // turned back on, so a map the game had already disabled stays disabled.
        private static readonly System.Collections.Generic.List<string> _disabledMaps =
            new System.Collections.Generic.List<string>();
        private static readonly System.Collections.Generic.List<object> _disabledMapObjs =
            new System.Collections.Generic.List<object>();
        private static Point _dragStart;
        private static double _dragLeft, _dragTop, _dragW, _dragH, _dragSplit;
        private static UIElement _dragCapture;
        // One log line per drag when the requested size had to be clamped: a silent
        // clamp is indistinguishable from a stalled window.
        private static bool _clampLogged;

        // ---- list state ---------------------------------------------------
        private static List<InspectorEvent> _shown = new List<InspectorEvent>();
        private static int _selected = -1;
        // The event the detail pane is showing, independent of its row number.
        private static string _selectedEventId;
        private static int _scrollRows;
        // The detail pane pages by offset: a TextBlock cannot scroll and this build has
        // no control templates, so "scroll" means "paint a different page of the text".
        private const int DetailPageLines = 34;
        private const int DetailMaxChars = 20000;
        // The body is paged as a slice of the text; this only bounds how many pages are
        // built for one payload, so a pathological body cannot spin the main thread.
        private const int MaxDetailPages = 400;
        // One footer line plus its padding, reserved out of the body budget. The footer
        // hides itself when there is only one page, and hiding can only free space.
        private const double DetailFooterReserve = 20.0;
        private static string _detailFull = "";
        private static bool _detailCapped;
        private static int _lastSignature = -1;
        private static string _lastGeoError;
        // Set when Settle() could not force a layout pass; State() reports it so a caller
        // knows the sizes it got are the previous ones.
        private static string _layoutError;

        // ---- host tracking -------------------------------------------------
        // The mount host can change size without anyone calling us: resolution change,
        // UI scale change, windowed <-> fullscreen, or the game re-arranging its own
        // layout. Keeping the last size is what turns "the window is suddenly clipped"
        // into a re-fit that happens before the next frame is drawn.
        private static double _lastHostW = -1;
        private static double _lastHostH = -1;
        // Set by ClampSplit: below these the corresponding splitter is folded away
        // instead of being clamped into an unusable width.
        private static bool _compactW;
        private static bool _compactH;

        public static bool IsInstalled { get { return _wrap != null; } }
        public static bool IsVisible { get { return _visible && _wrap != null; } }
        public static bool IsMaximized { get { return _maximized; } }

        // ================================================== install / remove
        public static JObject Install()
        {
            if (_wrap != null)
            {
                JObject again = State();
                again["alreadyInstalled"] = true;
                return again;
            }

            FrameworkElement root = UiBridge.TacticalRootForInspector();
            if (root == null)
                throw new ApiException(409, "no_ui_host",
                    "no NoesisView currently shows SeapowerUI.MainGameView; the inspector "
                    + "window only exists while a mission is on screen", false);

            object found = UiBridge.FindNamedForInspector(root, "DisplayAreaGrid");
            Panel host = found as Panel;
            if (host == null)
                throw new ApiException(409, "no_mount_point",
                    "DisplayAreaGrid was not found as a Panel under the tactical view; "
                    + "nothing was inserted", false);

            _displayHost = host;
            if (ShellHost != null)
            {
                _embedded = true;
                _host = ShellHost;
            }
            else
            {
                _host = PickMountHost(root, host);
            }
            _cfg = LoadConfig();
            // Read the language tables on the main thread here, before anything draws:
            // deciding the language needs Application state, and calling that off the
            // main thread has already taken this process down once.
            Strings.EnsureLoaded();

            // Ratios -> pixels against the mount host, which is the authority: there is
            // no space a child of that host can paint into beyond its own box. If the
            // host has no layout box yet (install can land before the first arrange)
            // the tactical root is used only to pick a starting size - NormalizeGeometry
            // re-fits it on the next Tick, once the host reports its real one.
            double vw = (double)_host.ActualWidth;
            double vh = (double)_host.ActualHeight;
            if (vw < 1.0 || vh < 1.0)
            {
                vw = (double)root.ActualWidth;
                vh = (double)root.ActualHeight;
                Core.Log("inspector install: mount host has no layout box yet; sizing against the tactical root "
                    + Math.Round(vw) + "x" + Math.Round(vh));
            }
            if (vw < 1.0 || vh < 1.0) { vw = MinW; vh = MinH; }
            double rl = _cfg.Left, rt = _cfg.Top, rw = _cfg.Width, rh = _cfg.Height;
            if (rw >= 0.95 && rh >= 0.95 && rl <= 0.03 && rt <= 0.03)
            {
                // An older build persisted a maximise as 0,0,1,1. That is not a
                // preference, so it is treated as "nothing saved" and repaired here
                // instead of asking the player to delete the file by hand.
                rl = 0.11; rt = 0.11; rw = 0.78; rh = 0.78;
                Core.Log("inspector geometry looked like a persisted maximise; using defaults");
            }
            _left = (float)(rl * vw);
            _top = (float)(rt * vh);
            _w = (float)(rw * vw);
            _h = (float)(rh * vh);
            _savedLeft = _left;
            _savedTop = _top;
            _savedW = _w;
            _savedH = _h;
            _splitPx = (float)Math.Max(MinTimelinePx, _cfg.SplitY * _h);
            _splitXPx = (float)Math.Max(MinActionPx, Math.Min(_cfg.SplitX * _w, MaxSplitXPx()));
            _splitPxPref = _splitPx;
            _splitXPxPref = _splitXPx;
            ClampSplit();
            _maximized = false;
            _collapsed = false;
            _selected = -1;
            _selectedEventId = null;
            _scrollRows = 0;
            ResetDetailPage();
            _lastSignature = -1;
            ClampGeometry(vw, vh);

            BuildTree();
            if (_wrap == null)
                throw new ApiException(500, "ui_build_failed",
                    "the inspector tree could not be built; see bridge.log", false);

            try
            {
                _host.Children.Add(_wrap);
                // The panel proved this mount point renders, yet the window showed
                // nothing while /probe/ui2 counted it in the tree (childCount 13).
                // A sibling declared later, or one carrying its own ZIndex, draws over
                // us, so raise ours first - it is the cheapest way to rule that out.
                Noesis.Panel.SetZIndex(_wrap, 10000);
            }
            catch (Exception ex)
            {
                _wrap = null;
                throw new ApiException(500, "ui_mount_failed",
                    "adding the inspector window to DisplayAreaGrid failed: "
                    + ex.GetType().Name, false);
            }

            _visible = true;
            ApplyGeometry();
            // Force the arrange before reporting: reading ActualWidth straight after adding
            // to the tree returns zeros, and "every pane is 0x0" is indistinguishable from
            // "the window did not mount" - which is exactly the wrong answer for a caller
            // trying to tell those two apart.
            try { _wrap.UpdateLayout(); } catch (Exception) { }
            Refresh(true);
            // Record the size the first fit was made against, so the host-change check
            // does not immediately undo it with a second re-fit on the next frame.
            double hw0, hh0;
            if (AvailRect(out hw0, out hh0)) { _lastHostW = hw0; _lastHostH = hh0; }
            Core.Log("inspector window installed " + (int)_w + "x" + (int)_h
                + " into host " + Math.Round((double)_host.ActualWidth) + "x" + Math.Round((double)_host.ActualHeight));
            return State();
        }

        /// <summary>
        /// Fills the shell's content well. Every geometry rule the page already proved
        /// stays in force; embedding only changes who owns the frame.
        /// </summary>
        internal static void FillContentArea(Panel content)
        {
            if (content == null) return;
            ShellHost = content;
            _embedded = true;
            if (_wrap == null) return;
            if (!ReferenceEquals(_host, content))
            {
                try
                {
                    FrameworkElement old = _wrap.Parent as FrameworkElement;
                    if (old is Panel) ((Panel)old).Children.Remove(_wrap);
                }
                catch (Exception) { }
                try { content.Children.Add(_wrap); }
                catch (Exception) { }
                _host = content;
            }
            ApplyGeometry();
        }

        public static JObject Remove()
        {
            if (_wrap == null)
            {
                JObject o = new JObject();
                o["installed"] = false;
                o["removed"] = false;
                return o;
            }

            EndDrag("window removed");
            try
            {
                if (_host != null)
                {
                    _host.PreviewMouseMove -= OnHostMove;
                    _host.PreviewMouseLeftButtonUp -= OnHostUp;
                }
            }
            catch (Exception) { }

            int before = ChildCount(_host);
            try { if (_host != null) _host.Children.Remove(_wrap); }
            catch (Exception ex) { Core.Log("inspector remove failed: " + ex.GetType().Name); }

            _wrap = null;
            _outer = null;
            _timelineBox = null;
            _rowsPanel = null;
            _detailBox = null;
            _detailText = null;
            _statusText = null;
            _titleText = null;
            _visible = false;
            _dragMode = "";
            _shown = new List<InspectorEvent>();
            _selected = -1;
            _selectedEventId = null;
            ResetDetailPage();

            JObject r = new JObject();
            r["installed"] = false;
            r["removed"] = true;
            r["childrenBefore"] = before;
            r["childrenAfter"] = ChildCount(_host);
            return r;
        }

        // ================================================== commands
        /// <summary>Force the deferred arrange before reading sizes back. Noesis lays out on
        /// the next frame, so a command that resizes and then reports returns the sizes the
        /// window HAD - measured: asking for 520x320 reported 560x380 from the request while
        /// the picture was already correct. A diagnostics answer that is one edit behind is
        /// worse than no answer: it reads as "the resize did not happen".</summary>
        private static void Settle()
        {
            if (_wrap == null) return;
            try
            {
                _wrap.UpdateLayout();
                _layoutError = null;
            }
            catch (Exception ex)
            {
                // Not silent: a response that reports the old sizes because layout did not
                // run is worse than one that says it could not confirm the sizes.
                _layoutError = ex.GetType().Name;
                Core.Log("inspector layout settle failed: " + ex.GetType().Name);
            }
        }

        private static bool Flag(JObject req, string key)
        {
            JToken t = req == null ? null : req[key];
            if (t == null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return string.Equals(t.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Optional number field. A string holding a number is accepted because a
        /// hand written curl body very often sends "560"; anything else is a refusal, not a
        /// silent zero.</summary>
        private static bool TryNum(JObject req, string key, out double value)
        {
            value = 0;
            JToken t = req == null ? null : req[key];
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                value = (double)t;
            }
            else if (t.Type == JTokenType.String)
            {
                if (!double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new ApiException(400, "bad_number", key + " is not a number", false);
            }
            else
            {
                throw new ApiException(400, "bad_type", key + " must be a number", false);
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ApiException(400, "bad_number", key + " must be finite", false);
            return true;
        }

        public static JObject Command(JObject req)
        {
            string op = req == null ? null : (string)req["op"];
            if (string.IsNullOrEmpty(op)) op = "state";

            if (op == "install") return Install();
            if (op == "remove") return Remove();

            // Language is a setting, not a window operation. It is accepted before the
            // window exists, so a preference can be set first and the loaded tables can
            // be checked at any time - including from a menu with no mission on screen,
            // where install is impossible and the labels cannot be looked at.
            if (op == "language")
            {
                JToken lang = req["language"];
                if (lang == null) lang = req["tag"];
                string tag = (lang != null && lang.Type == JTokenType.String)
                    ? (string)lang : null;
                bool changed = Strings.SetLanguage(tag);
                if (_wrap == null)
                {
                    JObject pre = new JObject();
                    pre["installed"] = false;
                    pre["changed"] = changed;
                    pre["strings"] = Strings.Report();
                    return pre;
                }
                ApplyLanguage();
                JObject after = State();
                after["changed"] = changed;
                return after;
            }

            if (_wrap == null)
                throw new ApiException(409, "not_installed",
                    "the inspector window is not installed; POST {\"op\":\"install\"} first", false);

            if (op == "state") return State();
            if (op == "show") { _visible = true; ApplyGeometry(); return State(); }
            if (op == "hide") { _visible = false; ApplyGeometry(); return State(); }
            if (op == "toggle") { _visible = !_visible; ApplyGeometry(); return State(); }
            if (op == "maximize") { Maximize(); Settle(); return State(); }
            if (op == "restore") { Restore(); Settle(); return State(); }
            if (op == "collapse") { Collapse(true); Settle(); return State(); }
            if (op == "expand") { Collapse(false); Settle(); return State(); }
            // Agent-driven placement. Dragging needs a human at the mouse, and the one
            // question that matters - "does the window still fit at 560x380" - cannot be
            // answered by a human watching. Numbers are pixels; anything asked for goes
            // through the same NormalizeGeometry as a drag.
            if (op == "geometry")
            {
                bool touched = false;
                double v;
                if (TryNum(req, "width", out v)) { _w = v; touched = true; }
                if (TryNum(req, "height", out v)) { _h = v; touched = true; }
                if (TryNum(req, "left", out v)) { _left = v; touched = true; }
                if (TryNum(req, "top", out v)) { _top = v; touched = true; }
                if (!touched)
                    throw new ApiException(400, "missing_field",
                        "geometry needs at least one of left, top, width, height", false);
                if (_collapsed) _collapsed = false;   // a resize is a request to see the body
                _maximized = false;
                ApplyGeometry();
                Settle();
                JObject g = State();
                if (Flag(req, "save")) SaveGeometry("geometry");
                return g;
            }
            if (op == "split")
            {
                bool any = false;
                double v;
                if (TryNum(req, "y", out v)) { _splitPxPref = v; any = true; }
                if (TryNum(req, "x", out v)) { _splitXPxPref = v; any = true; }
                if (!any)
                    throw new ApiException(400, "missing_field",
                        "split needs y (timeline height in pixels) or x (action-pane width in pixels)", false);
                ApplyGeometry();
                Settle();
                JObject s = State();
                if (Flag(req, "save")) SaveGeometry("split");
                return s;
            }
            if (op == "reload") { _cfg = LoadConfig(); Refresh(true); return State(); }
            // Selecting a row is the only way to see the detail pane, and until now that took
            // a mouse. An agent reading this window needs the same view the human gets, so the
            // selection becomes addressable: index into the visible rows, -1 for none.
            if (op == "select")
            {
                double want;
                if (!TryNum(req, "index", out want))
                    throw new ApiException(400, "missing_field", "select needs index (0 based, -1 for none)", false);
                // A fraction used to be truncated: index 0.9 silently became 0, i.e. the
                // caller was told nothing and got a different row than it asked for.
                if (want != Math.Floor(want))
                    throw new ApiException(400, "bad_type",
                        "index must be a whole number (visible row, 0 based)", false);
                int idx = (int)want;
                if (idx < -1 || idx >= _shown.Count)
                    throw new ApiException(400, "bad_index",
                        "index must be -1 or 0.." + (_shown.Count - 1) + " (the visible rows)", false);
                _selected = idx;
                _selectedEventId = (idx >= 0 && idx < _shown.Count) ? _shown[idx].EventId : null;
                ResetDetailPage();
                _lastSignature = -1;
                Refresh(true);
                Settle();
                JObject sel = State();
                sel["selected"] = idx;
                return sel;
            }
            // The detail pane's page index. Same reason `select` exists: the pane is only
            // reachable with a mouse otherwise, so "does page forward reach the end and
            // skip nothing" is a question no test could ask.
            if (op == "detail_page")
            {
                double pwant;
                if (!TryNum(req, "index", out pwant))
                    throw new ApiException(400, "missing_field", "detail_page needs index (0 based page)", false);
                if (pwant != Math.Floor(pwant))
                    throw new ApiException(400, "bad_type", "index must be a whole number (page)", false);
                EnsurePages();
                int pidx = (int)pwant;
                if (pidx < 0 || pidx >= _pages.Count)
                    throw new ApiException(400, "bad_index",
                        "index must be 0.." + (_pages.Count - 1) + " (" + _pages.Count + " page(s))", false);
                _pageIndex = pidx;
                PaintDetail();
                Settle();
                return State();
            }
            if (op == "hosts") return HostChain();
            if (op == "remount")
            {
                JToken t = req["to"];
                return Remount(t != null && t.Type == JTokenType.String ? (string)t : null);
            }
            if (op == "font")
            {
                JToken fam = req["family"];
                string family = (fam != null && fam.Type == JTokenType.String)
                    ? (string)fam : null;
                bool global = false;
                JToken gflag = req["global"];
                if (gflag != null && gflag.Type == JTokenType.Boolean) global = (bool)gflag;
                return ApplyFontFamily(family, global);
            }
            throw new ApiException(404, "not_found", "unknown inspector window op: " + op, false);
        }

        public static JObject State()
        {
            JObject o = new JObject();
            o["installed"] = _wrap != null;
            // Everything below is what the LAYOUT answers, not our own variables, and
            // that is what separates "not mounted" from "mounted but invisible" - the
            // exact question a blank screen raises.
            try
            {
                o["wrapActualWidth"] = _wrap == null ? 0 : Math.Round(_wrap.ActualWidth, 2);
                o["wrapActualHeight"] = _wrap == null ? 0 : Math.Round(_wrap.ActualHeight, 2);
                o["wrapVisibility"] = _wrap == null ? null : _wrap.Visibility.ToString();
                o["wrapChildren"] = _wrap == null ? 0 : _wrap.Children.Count;
                o["outerActualWidth"] = _outer == null ? 0 : Math.Round(_outer.ActualWidth, 2);
                o["outerActualHeight"] = _outer == null ? 0 : Math.Round(_outer.ActualHeight, 2);
                o["outerHasChild"] = _outer != null && _outer.Child != null;
                o["zIndex"] = _wrap == null ? 0 : Noesis.Panel.GetZIndex(_wrap);
                o["mountHost"] = _host == null ? null : _host.GetType().Name + " " + _host.Name;
                o["mountHostSize"] = _host == null ? null : (Math.Round((double)_host.ActualWidth, 1) + "x" + Math.Round((double)_host.ActualHeight, 1));
                o["displayHostSize"] = _displayHost == null ? null : (Math.Round((double)_displayHost.ActualWidth, 1) + "x" + Math.Round((double)_displayHost.ActualHeight, 1));
                if (_wrap != null) o["hosts"] = HostChain();
                o["splitXPx"] = Math.Round(_splitXPx, 2);
                o["splitXPxPref"] = Math.Round(_splitXPxPref, 2);
                o["actionsDrawn"] = _actionsDrawn;
                o["actionRows"] = _actionPanel == null ? 0 : _actionPanel.Children.Count;
                o["fontFamily"] = _fontFamily;
                // Which table the chrome came from, and where it was read. A missing
                // translation has to be visible as data, not as a guess.
                o["strings"] = Strings.Report();
            // What the layout actually decided, not what we asked for.
            o["rootActualWidth"] = _rootGrid == null ? 0d : (double)_rootGrid.ActualWidth;
            o["rootActualHeight"] = _rootGrid == null ? 0d : (double)_rootGrid.ActualHeight;
            o["bodyActualWidth"] = _bodyGrid == null ? 0d : (double)_bodyGrid.ActualWidth;
            o["bodyActualHeight"] = _bodyGrid == null ? 0d : (double)_bodyGrid.ActualHeight;
            o["rowTimeline"] = _bodyRowTimeline == null ? "null" : _bodyRowTimeline.Height.ToString();
            o["rowDetail"] = _bodyRowDetail == null ? "null" : _bodyRowDetail.Height.ToString();

            // ---- readout group (a): what the layout settled on, not what we asked
            o["avail"] = AvailReadout();
            o["panes"] = PanesReadout();
            o["columns"] = ColumnsReadout();
            o["rows"] = RowsReadout();
            o["compact"] = CompactReadout();
            o["detailPage"] = DetailPageReadout();
            // ---- group (b): the window rect mapped into the tactical root's space
            o["mapped"] = MappedReadout();
            // ---- group (c): every ancestor that can clip, and its transform
            o["clips"] = ClipReadout();
            }
            catch (Exception ex) { o["layoutError"] = ex.GetType().Name; }
            o["layoutSynced"] = _layoutError == null;
            if (_layoutError != null) o["layoutSettleError"] = _layoutError;
            o["visible"] = IsVisible;
            o["maximized"] = _maximized;
            o["collapsed"] = _collapsed;
            o["left"] = Math.Round(_left, 2);
            o["top"] = Math.Round(_top, 2);
            o["width"] = Math.Round(_w, 2);
            o["height"] = Math.Round(_h, 2);
            o["splitPx"] = Math.Round(_splitPx, 2);
            o["splitPxPref"] = Math.Round(_splitPxPref, 2);
            o["minWidth"] = MinW;
            o["minHeight"] = MinH;
            o["viewportWidth"] = _host == null ? 0 : Math.Round(_host.ActualWidth, 2);
            o["viewportHeight"] = _host == null ? 0 : Math.Round(_host.ActualHeight, 2);
            o["layout"] = "border-and-textblock only (no control templates are reachable here)";
            o["dragMode"] = _dragMode;
            o["toggleKey"] = _cfg == null ? "N" : _cfg.ToggleKey;
            o["rowsDrawn"] = _shown.Count;
            o["scrollRows"] = _scrollRows;
            o["selected"] = _selected;
            o["bufferEvents"] = InspectorBuffer.Count;
            o["bufferBytes"] = InspectorBuffer.BytesUsed;
            o["bufferDropped"] = InspectorBuffer.Dropped;
            if (!string.IsNullOrEmpty(_lastGeoError)) o["geometryError"] = _lastGeoError;
            return o;
        }

        // ================================================== readouts
        // Three groups, because three different questions were being answered with
        // one number: "is the frame the size I asked for", "does a logical unit still
        // mean one pixel where it is mounted", and "what can cut it off".
        // Reading our own variables answers none of them - each of these reads the
        // layout, the root's transform or the ancestor chain instead.

        private static JObject SizeOf(FrameworkElement fe)
        {
            JObject r = new JObject();
            r["w"] = fe == null ? 0d : Math.Round((double)fe.ActualWidth, 2);
            r["h"] = fe == null ? 0d : Math.Round((double)fe.ActualHeight, 2);
            return r;
        }

        private static JObject RectJ(Rect r)
        {
            JObject o = new JObject();
            o["x"] = Math.Round((double)r.X, 2);
            o["y"] = Math.Round((double)r.Y, 2);
            o["w"] = Math.Round((double)r.Width, 2);
            o["h"] = Math.Round((double)r.Height, 2);
            o["right"] = Math.Round((double)r.Right, 2);
            o["bottom"] = Math.Round((double)r.Bottom, 2);
            return o;
        }

        private static JObject AvailReadout()
        {
            JObject o = new JObject();
            double aw, ah;
            bool ok = AvailRect(out aw, out ah);
            o["ok"] = ok;
            o["w"] = Math.Round(aw, 2);
            o["h"] = Math.Round(ah, 2);
            o["minUsedW"] = Math.Round(Math.Min(MinW, aw), 2);
            o["minUsedH"] = Math.Round(Math.Min(MinH, ah), 2);
            o["contentW"] = Math.Round(ContentW(), 2);
            o["contentH"] = Math.Round(ContentH(), 2);
            return o;
        }

        private static JObject PanesReadout()
        {
            JObject o = new JObject();
            o["timeline"] = SizeOf(_timelineBox);
            o["detail"] = SizeOf(_detailBox);
            o["actions"] = SizeOf(_actionPanel);
            o["rows"] = SizeOf(_rowsPanel);
            o["outer"] = SizeOf(_outer);
            return o;
        }

        private static JObject ColumnsReadout()
        {
            JObject o = new JObject();
            try
            {
                Grid band = _band;
                if (band == null || band.ColumnDefinitions.Count < 3)
                {
                    o["available"] = false;
                    return o;
                }
                o["detail"] = Math.Round((double)band.ColumnDefinitions[0].ActualWidth, 2);
                o["splitter"] = Math.Round((double)band.ColumnDefinitions[1].ActualWidth, 2);
                o["action"] = Math.Round((double)band.ColumnDefinitions[2].ActualWidth, 2);
                o["actionDeclared"] = band.ColumnDefinitions[2].Width.ToString();
                o["splitXPx"] = Math.Round(_splitXPx, 2);
                o["splitXPxPref"] = Math.Round(_splitXPxPref, 2);
            }
            catch (Exception ex) { o["error"] = ex.GetType().Name; }
            return o;
        }

        /// <summary>What the detail pane is actually showing: which page, how many pages,
        /// which source lines it covers, whether the footer is visible and what it says.
        /// Without this an agent can install, select and still be unable to tell whether
        /// the pane lost text - the pane was only readable by a human looking at it.</summary>
        private static JObject DetailPageReadout()
        {
            JObject o = new JObject();
            try
            {
                if (_selected >= 0 && _selected < _shown.Count)
                {
                    o["eventId"] = _shown[_selected].EventId;
                    o["eventType"] = _shown[_selected].EventType;
                    string sum = _shown[_selected].Summary ?? "";
                    o["summary"] = sum.Length > 120 ? sum.Substring(0, 120) : sum;
                }
                o["page"] = _pageIndex + 1;
                o["pages"] = _pages.Count;
                o["capped"] = _detailCapped;
                o["lines"] = PageLineTotal();
                o["charsPerLine"] = CharsPerLine();
                o["pageLines"] = DetailPage();
                o["rebuilt"] = _pagesKey;
                if (_pages.Count > 0 && _pageIndex >= 0 && _pageIndex < _pages.Count)
                {
                    DPage pg = _pages[_pageIndex];
                    o["firstLine"] = pg.FirstLine + 1;
                    o["lastLine"] = pg.LastLine + 1;
                    o["chars"] = pg.Text == null ? 0 : pg.Text.Length;
                }
                if (_detailFooter != null)
                {
                    o["footerVisible"] = _detailFooter.Visibility == Visibility.Visible;
                    o["footer"] = _detailFooter.Text == null ? "" : _detailFooter.Text;
                }
            }
            catch (Exception ex) { o["error"] = ex.GetType().Name; }
            return o;
        }

        private static JObject RowsReadout()
        {
            JObject o = new JObject();
            try
            {
                if (_bodyRowTimeline != null)
                {
                    o["timelineDeclared"] = _bodyRowTimeline.Height.ToString();
                    o["timelineActual"] = Math.Round((double)_bodyRowTimeline.ActualHeight, 2);
                }
                if (_bodyRowDetail != null)
                {
                    o["detailDeclared"] = _bodyRowDetail.Height.ToString();
                    o["detailActual"] = Math.Round((double)_bodyRowDetail.ActualHeight, 2);
                }
                o["splitPx"] = Math.Round(_splitPx, 2);
                o["splitPxPref"] = Math.Round(_splitPxPref, 2);
            }
            catch (Exception ex) { o["error"] = ex.GetType().Name; }
            return o;
        }

        private static JObject CompactReadout()
        {
            JObject o = new JObject();
            o["width"] = _compactW;
            o["height"] = _compactH;
            o["reason"] = _compactW
                ? "the action pane would fall below " + MinActionPx + "px, so it is folded"
                : (_compactH
                    ? "the detail band would fall below " + MinDetailPx + "px, so it is folded"
                    : "");
            return o;
        }

        /// <summary>
        /// Where the window actually lands in the tactical root's own coordinate
        /// space. If a scale transform sits between the host and the root, this rect
        /// is not the rect we asked for - and that is the difference between "the
        /// window is smaller than the host" and "every logical unit is 2 pixels here".
        /// </summary>
        private static JObject MappedReadout()
        {
            JObject o = new JObject();
            try
            {
                FrameworkElement root = UiBridge.TacticalRootForInspector();
                if (root == null || _wrap == null)
                {
                    o["available"] = false;
                    return o;
                }
                double eh = _collapsed ? TitleHeight : _h;
                Rect local = new Rect(0f, 0f, (float)_w, (float)eh);
                Matrix4 m = _wrap.TransformToAncestor((Visual)root);
                Rect mapped = m.TransformBounds(local);
                o["local"] = RectJ(local);
                o["inTacticalRoot"] = RectJ(mapped);
                o["scaleX"] = local.Width > 0f ? Math.Round((double)(mapped.Width / local.Width), 4) : 0d;
                o["scaleY"] = local.Height > 0f ? Math.Round((double)(mapped.Height / local.Height), 4) : 0d;
                o["offsetX"] = Math.Round((double)(mapped.X - local.X), 2);
                o["offsetY"] = Math.Round((double)(mapped.Y - local.Y), 2);
                JObject ra = new JObject();
                ra["w"] = Math.Round((double)root.ActualWidth, 2);
                ra["h"] = Math.Round((double)root.ActualHeight, 2);
                o["rootActual"] = ra;
            }
            catch (Exception ex) { o["error"] = ex.GetType().Name; }
            return o;
        }

        /// <summary>
        /// Every ancestor of the window with its clip state. A pane that is laid out
        /// correctly but painted over by a sibling, or cut by an ancestor's clip, shows
        /// the same picture as one that is outside the frame - this is what tells the
        /// two apart without guessing.
        /// </summary>
        private static JArray ClipReadout()
        {
            JArray rows = new JArray();
            try
            {
                object node = _wrap;
                int guard = 0;
                while (node != null && guard < 16)
                {
                    guard++;
                    JObject row = new JObject();
                    row["type"] = node.GetType().Name;
                    FrameworkElement fe = node as FrameworkElement;
                    row["name"] = fe == null ? null : fe.Name;
                    if (fe != null)
                    {
                        row["w"] = Math.Round((double)fe.ActualWidth, 2);
                        row["h"] = Math.Round((double)fe.ActualHeight, 2);
                    }
                    UIElement uie = node as UIElement;
                    if (uie != null)
                    {
                        row["clipToBounds"] = uie.ClipToBounds;
                        row["hasClip"] = uie.Clip != null;
                        row["opacity"] = Math.Round((double)uie.Opacity, 2);
                        Transform rt = uie.RenderTransform;
                        row["renderTransform"] = rt == null ? "none" : rt.GetType().Name;
                    }
                    rows.Add(row);
                    node = ParentOf(node);
                }
            }
            catch (Exception ex)
            {
                JObject err = new JObject();
                err["error"] = ex.GetType().Name;
                rows.Add(err);
            }
            return rows;
        }

        // ================================================== keyboard
        /// <summary>
        /// Called from the panel's root key listener. Never touches the window while
        /// the player is typing into the panel's text box: N has to stay typeable.
        /// </summary>
        public static void HandleKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            // Typing wins over every hotkey: the panel must be able to contain the very
            // key that opens it.
            if (UiBridge.Typing) return;

            // The console host owns the single hotkey now (N toggles the window, Tab
            // switches tab), so the inspector no longer answers to a key of its own.
            // Keeping the old binding would make one press toggle two windows.
            string toggle = "";
            if (string.IsNullOrEmpty(toggle)) return;

            if (_wrap == null)
            {
                // First press opens the window. Requiring an HTTP install before the key
                // works makes the key undiscoverable - from the pilot seat that reads as
                // "I pressed N and nothing happened", which is exactly what was reported.
                try
                {
                    Install();
                    _visible = true;
                    ApplyGeometry();
                    Core.Log("inspector installed by key " + name);
                }
                catch (Exception ex)
                {
                    Core.Log("inspector key install failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                return;
            }

            try
            {
                _visible = !_visible;
                ApplyGeometry();
                Core.Log("inspector toggled " + (_visible ? "on" : "off"));
            }
            catch (Exception ex)
            {
                Core.Log("inspector toggle failed: " + ex.GetType().Name);
            }
        }

        // ================================================== per frame
        public static void Tick()
        {
            if (_wrap == null) return;
            // Safety net for the drag input suppression: a release can be missed when
            // the pointer leaves the window, the game loses focus, or alt-tab happens.
            // Whatever the reason, a drag that is no longer in progress must never
            // leave the game's own bindings switched off.
            // Look at the physical mouse, not at _dragMode alone: when a release is
            // missed _dragMode stays set, so the old check could never fire and the
            // game's bindings stayed switched off.
            if (!string.IsNullOrEmpty(_dragMode))
            {
                // Poll instead of trusting move events: when the frame stalls the events
                // are dropped, and the window then stops following the pointer.
                try { ApplyDragTick(); }
                catch (Exception pex)
                {
                    Core.Log("inspector drag poll failed: " + pex.GetType().Name);
                    _dragMode = "";
                }
            }
            if (!string.IsNullOrEmpty(_dragMode) && !UnityEngine.Input.GetMouseButton(0))
            {
                EndDrag("watchdog: mouse is up while a drag is still marked active");
            }
            if (string.IsNullOrEmpty(_dragMode) && _gameInputOff) SetGameInput(true);
            // The host can change size behind our back (resolution, UI scale, windowed
            // <-> fullscreen, the game re-arranging its own layout). Nothing calls us
            // when it happens, so the size is compared here, once per frame, and only
            // a real change costs anything.
            try { CheckHostChange(); }
            catch (Exception hex) { Core.Log("inspector host re-fit failed: " + hex.GetType().Name); }
            try { Refresh(false); }
            catch (Exception ex) { Core.Log("inspector tick failed: " + ex.GetType().Name); }
            if (_fontDirty) { _fontDirty = false; ApplyFontToFreshNodes(); }
        }

        /// <summary>
        /// Re-fit when the mount host changed size, and end a drag first: the drag's
        /// origin was recorded in the old coordinate space, so continuing it would
        /// jump the window by the difference.
        /// </summary>
        private static void CheckHostChange()
        {
            double aw, ah;
            if (!AvailRect(out aw, out ah)) return;
            if (Math.Abs(aw - _lastHostW) < 0.5 && Math.Abs(ah - _lastHostH) < 0.5) return;

            bool first = _lastHostW < 0 || _lastHostH < 0;
            double fromW = _lastHostW, fromH = _lastHostH;
            _lastHostW = aw;
            _lastHostH = ah;
            if (first) return;   // install already fitted to this size

            if (!string.IsNullOrEmpty(_dragMode)) EndDrag("the viewport changed under the drag");
            ApplyGeometry();
            _lastSignature = -1;
            Core.Log("inspector host changed " + Math.Round(fromW) + "x" + Math.Round(fromH)
                + " -> " + Math.Round(aw) + "x" + Math.Round(ah)
                + "; window re-fitted to " + Math.Round(_left) + "," + Math.Round(_top)
                + " " + Math.Round(_w) + "x" + Math.Round(_h));
        }

        private static void Refresh(bool force)
        {
            if (_wrap == null) return;

            int count = InspectorBuffer.Count;
            // The usable row count belongs in the signature: a height change is a
            // content change, and a drag that does not change it must not rebuild.
            int sig = count * 31 + _selected * 7 + _scrollRows + FitRows() * 7919;
            if (!force && sig == _lastSignature) return;
            _lastSignature = sig;
            _fontDirty = true;   // rows were rebuilt: the new TextBlocks need the family

            if (_statusText != null)
            {
                double mb = InspectorBuffer.BytesUsed / 1048576.0;
                _statusText.Text = ConsoleHost.Strip() + "   " + Strings.F("status.line", count, _shown.Count,
                    mb.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    InspectorBuffer.Dropped,
                    UiBridge.Hold ? Strings.T("status.hold_on") : Strings.T("status.hold_off"),
                    "N / Tab");
            }

            RebuildRows();
            RenderDetail();
        }

        /// <summary>Keeps the detail pane pointed at the event it was pointed at. The row
        /// list is newest first, so every arrival renumbers it; without this the pane
        /// quietly showed a different event a moment later and its page count changed
        /// under the reader. If the anchored event has scrolled out of the buffer the
        /// selection is cleared rather than silently moved to a neighbour.</summary>
        private static void ReanchorSelection()
        {
            if (string.IsNullOrEmpty(_selectedEventId)) return;
            for (int i = 0; i < _shown.Count; i++)
            {
                if (string.Equals(_shown[i].EventId, _selectedEventId, StringComparison.Ordinal))
                {
                    _selected = i;
                    return;
                }
            }
            _selected = -1;
        }

        private static void RebuildRows()
        {
            if (_rowsPanel == null) return;

            List<InspectorEvent> all = InspectorBuffer.Snapshot(null, null, null);
            int capacity = FitRows();
            int end = all.Count - _scrollRows;
            if (end < 0) end = 0;
            int start = end - capacity;
            if (start < 0) start = 0;
            if (end - start > MaxRowsDrawn) start = end - MaxRowsDrawn;

            // Keep the anchored event inside the row window. Without this the window keeps
            // drifting as new traffic arrives and the anchored event leaves it within a few
            // events, which cleared the selection and emptied the detail pane while someone
            // was reading it - measured: select row 0, take two screenshots, pane is blank.
            if (!string.IsNullOrEmpty(_selectedEventId))
            {
                int ai = -1;
                for (int i = 0; i < all.Count; i++)
                {
                    if (string.Equals(all[i].EventId, _selectedEventId, StringComparison.Ordinal)) { ai = i; break; }
                }
                if (ai >= 0 && (ai < start || ai >= end))
                {
                    // Put the anchored row at the TOP of the window and keep the window full:
                    // pinning it to the newest edge made the list collapse to a single row when
                    // the reader was anchored on an old event (measured: 1 row on screen).
                    start = ai;
                    end = ai + capacity;
                    if (end > all.Count) { end = all.Count; start = end - capacity; }
                    if (start < 0) start = 0;
                    _scrollRows = all.Count - end;
                    if (_scrollRows < 0) _scrollRows = 0;
                }
            }

            _shown = new List<InspectorEvent>();
            for (int i = start; i < end; i++) _shown.Add(all[i]);
            // The selection is anchored to the EVENT, not to the row number. New traffic
            // shifts every index down, so an index-based selection silently retargeted the
            // detail pane at a different event while someone was reading it - measured: the
            // footer went from "page 1/109" to "page 16/16" without the body being touched.
            ReanchorSelection();

            _rowsPanel.Children.Clear();
            if (_shown.Count == 0)
            {
                Border empty = new Border();
                empty.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                TextBlock t = new TextBlock();
                t.FontSize = 12;
                t.Margin = new Thickness(6, 4, 0, 0);
                t.Text = all.Count == 0
                    ? "no observations yet - bridge calls are recorded as they happen"
                    : "no rows for the current offset";
                t.Foreground = new SolidColorBrush(Color.FromArgb(255, 130, 170, 200));
                empty.Child = t;
                _rowsPanel.Children.Add(empty);
                return;
            }

            for (int i = 0; i < _shown.Count; i++)
            {
                int index = i;
                InspectorEvent ev = _shown[i];
                bool sel = (index == _selected);

                Border row = new Border();
                row.Height = (float)RowHeight;
                row.Background = sel
                    ? new SolidColorBrush(Color.FromArgb(210, 30, 80, 130))
                    : new SolidColorBrush(Color.FromArgb((byte)(i % 2 == 0 ? 90 : 40), 20, 40, 60));
                row.IsHitTestVisible = true;
                try
                {
                    row.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                    {
                        _selected = index;
                        _selectedEventId = (index >= 0 && index < _shown.Count) ? _shown[index].EventId : null;
                        ResetDetailPage();
                        _lastSignature = -1;
                        Refresh(true);
                        if (e != null) e.Handled = true;
                    };
                }
                catch (Exception) { }

                Grid line = new Grid();
                // 96px fits HH:mm:ss.fff at 12pt; 66 was sized for the 8 character stamp
                // that only existed because the old ShortTime threw the hours away.
                line.ColumnDefinitions.Add(Col(96));
                line.ColumnDefinitions.Add(Col(96));
                line.ColumnDefinitions.Add(Col(0, true));

                TextBlock when = CellText(ShortTime(ev.Timestamp), 0);
                TextBlock kind = CellText(ev.EventType, 1);
                kind.Foreground = new SolidColorBrush(KindColor(ev.EventType));
                string tail = ev.Summary;
                if (ev.Repeat > 1) tail = tail + "   x" + ev.Repeat;
                TextBlock what = CellText(tail, 2);
                what.Foreground = new SolidColorBrush(StatusColor(ev.Status));

                line.Children.Add(when);
                line.Children.Add(kind);
                line.Children.Add(what);

                row.Child = line;
                _rowsPanel.Children.Add(row);
            }

            RebuildActions(all);
        }

        /// <summary>
        /// The right-hand pane: only events that carry a command id, i.e. the ones the
        /// agent actually sent towards the game. A filtered view of the same buffer
        /// rather than a second source, so the two can not disagree.
        /// </summary>
        private static void RebuildActions(List<InspectorEvent> all)
        {
            if (_actionPanel == null) return;
            _actionPanel.Children.Clear();
            int drawn = 0;
            for (int i = all.Count - 1; i >= 0 && drawn < 200; i--)
            {
                InspectorEvent ev = all[i];
                if (string.IsNullOrEmpty(ev.CommandId)) continue;
                Border row = new Border();
                row.Height = (float)RowHeight;
                row.Background = new SolidColorBrush(Color.FromArgb((byte)(drawn % 2 == 0 ? 90 : 40), 20, 40, 60));
                TextBlock t = new TextBlock();
                t.FontSize = 11;
                t.Margin = new Thickness(4, 1, 0, 0);
                t.Foreground = new SolidColorBrush(StatusColor(ev.Status));
                t.Text = ShortTime(ev.Timestamp) + "  " + ev.CommandId + "  " + (ev.Summary ?? "");
                row.Child = t;
                _actionPanel.Children.Add(row);
                drawn++;
            }
            if (drawn == 0)
            {
                TextBlock none = new TextBlock();
                none.FontSize = 11;
                none.Margin = new Thickness(6, 4, 0, 0);
                none.Foreground = new SolidColorBrush(Color.FromArgb(255, 130, 170, 200));
                none.Text = Strings.T("actions.empty");
                _actionPanel.Children.Add(none);
            }
            _actionsDrawn = drawn;
        }

        /// <summary>Back to the first page. Called whenever the shown body changes - a new
        /// selection, a cleared buffer, a fresh install - so a stale page index can never
        /// point at a page of the previous event.</summary>
        private static void ResetDetailPage()
        {
            _pageIndex = 0;
            _pagesKey = "";
        }

        private static void RenderDetail()
        {
            if (_detailText == null) return;
            if (_selected < 0 || _selected >= _shown.Count)
            {
                _detailFull = "";
                ResetDetailPage();
                _detailText.Text = Strings.T("detail.select");
                ShowFooter(false, "");
                return;
            }

            InspectorEvent e = _shown[_selected];
            string s = ""
                + "eventId    " + (e.EventId ?? "") + "\n"
                + "timestamp  " + (e.Timestamp ?? "") + "\n"
                + "eventType  " + (e.EventType ?? "") + "\n"
                + "source     " + (e.Source ?? "") + "\n"
                + "status     " + (e.Status ?? "") + "\n"
                + "duration   " + e.DurationMs + " ms\n"
                + "sessionId  " + (e.SessionId ?? "-") + "\n"
                + "stepId     " + (e.StepId ?? "-") + "\n"
                + "commandId  " + (e.CommandId ?? "-") + "\n"
                + "repeat     " + e.Repeat + (e.Truncated ? "   payload truncated" : "") + "\n"
                + "\nsummary\n" + (e.Summary ?? "") + "\n"
                + "\ninput\n" + (e.InputPayload ?? "") + "\n"
                + "\noutput\n" + (e.OutputPayload ?? "");
            if (!string.IsNullOrEmpty(e.ErrorMessage))
                s = s + "\n\nerror\n" + e.ErrorMessage;
            if (s.Length > DetailMaxChars)
            {
                s = s.Substring(0, DetailMaxChars);
                _detailCapped = true;
            }
            else
            {
                _detailCapped = false;
            }
            _detailFull = s;
            PaintDetail();
        }

        /// <summary>
        /// Paints one page of the selected event. A TextBlock cannot scroll, and this
        /// build has no control templates, so the pane is paged by offset instead of
        /// clipped: the footer says which lines are on screen and how many there are,
        /// so a long payload reads as "there is more, scroll" instead of "the panel cut
        /// my data". The whole body is still capped, and the cap is stated.
        /// </summary>
        private static void PaintDetail()
        {
            if (_detailText == null) return;
            EnsurePages();
            if (_pageIndex < 0) _pageIndex = 0;
            if (_pageIndex > _pages.Count - 1) _pageIndex = Math.Max(0, _pages.Count - 1);
            _detailText.Text = _pages[_pageIndex].Text;
            // Backstop for the width estimate above: whatever the arithmetic says, the block
            // may not grow past the pane. ClipToBounds stops it painting outside; MaxHeight
            // stops it from being cut mid-glyph in the first place.
            try
            {
                if (_detailBox != null && _detailBox.ActualHeight > 1.0)
                    _detailText.MaxHeight = (float)Math.Max(20.0, _detailBox.ActualHeight - 4.0);
            }
            catch (Exception) { }
            bool paged = _pages.Count > 1 || _detailCapped;
            if (!paged)
            {
                ShowFooter(false, "");
                return;
            }
            DPage pg = _pages[_pageIndex];
            string foot = Strings.F("detail.pages", _pageIndex + 1, _pages.Count,
                pg.FirstLine + 1, pg.LastLine + 1, PageLineTotal());
            if (_detailCapped) foot = foot + "  " + Strings.T("detail.capped");
            ShowFooter(true, foot);
        }

        /// <summary>The footer is a separate node in the same tree, so its space is
        /// reserved by the layout engine instead of being appended to a full page. Hiding
        /// it collapses its auto row, which can only ever give the body MORE room.</summary>
        private static void ShowFooter(bool visible, string text)
        {
            if (_detailFooter == null) return;
            try
            {
                _detailFooter.Text = text == null ? "" : text;
                _detailFooter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception) { }
        }

        private static int PageLineTotal()
        {
            string body = _detailFull ?? "";
            int n = 1;
            for (int i = 0; i < body.Length; i++) if (body[i] == '\n') n++;
            return n;
        }

        /// <summary>
        /// Splits the body into contiguous pages. Recomputed only when the body text or
        /// the pane's measured size changes. Because a page is a slice of the body, the
        /// whole document is reachable by scrolling forward and no character can be
        /// skipped - which is exactly what the old source-line paging could not promise.
        /// </summary>
        private static void EnsurePages()
        {
            int cpl = CharsPerLine();
            int budget = DetailPage();
            string key = cpl.ToString() + "x" + budget.ToString() + "x"
                + (_detailFull == null ? 0 : _detailFull.Length).ToString()
                + (_detailCapped ? "c" : "-");
            if (key == _pagesKey && _pages.Count > 0) return;
            _pagesKey = key;
            _pages.Clear();
            string[] lines = (_detailFull ?? "").Split('\n');
            int line = 0;
            int off = 0;
            while (line < lines.Length && _pages.Count < MaxDetailPages)
            {
                DPage p = new DPage();
                p.FirstLine = line;
                StringBuilder sb = new StringBuilder();
                int used = 0;
                bool first = true;
                while (line < lines.Length && used < budget)
                {
                    string raw = lines[line];
                    string s = raw == null ? "" : raw;
                    string rest = off > 0 ? (off >= s.Length ? "" : s.Substring(off)) : s;
                    int vis = VisualLines(rest, cpl);
                    int remain = budget - used;
                    if (vis <= remain)
                    {
                        if (!first) sb.Append('\n');
                        sb.Append(rest);
                        first = false;
                        used += vis;
                        p.LastLine = line;
                        line++;
                        off = 0;
                        if (used >= budget) break;
                    }
                    else
                    {
                        // The rest of this source line needs more room than is left, so the
                        // page keeps the part that fits and the next page resumes exactly
                        // where this one stopped. The old code always emitted the whole
                        // first line, which is how one long line became unreachable.
                        int roomW = remain * cpl;
                        if (roomW < 1) break;
                        string piece;
                        int took;
                        SliceByWidth(rest, roomW, out piece, out took);
                        if (piece.Length == 0) break;
                        if (!first) sb.Append('\n');
                        sb.Append(piece);
                        first = false;
                        used = budget;
                        p.LastLine = line;
                        off = off + took;
                        if (off >= s.Length)
                        {
                            line++;
                            off = 0;
                        }
                        break;
                    }
                }
                p.Text = sb.ToString();
                if (p.Text.Length == 0 && line >= lines.Length) break;
                _pages.Add(p);
                if (line >= lines.Length) break;
            }
            if (_pages.Count == 0)
            {
                DPage only = new DPage();
                only.Text = "";
                _pages.Add(only);
            }
            if (_pageIndex > _pages.Count - 1) _pageIndex = Math.Max(0, _pages.Count - 1);
        }

        /// <summary>Takes the widest prefix of s that fits in widthUnits, where an ASCII
        /// character costs one and anything wider (CJK, and by extension most non Latin
        /// text) costs two. Returns how many characters it consumed so the caller can
        /// resume at exactly that offset.</summary>
        private static void SliceByWidth(string s, int widthUnits, out string piece, out int consumed)
        {
            piece = "";
            consumed = 0;
            if (string.IsNullOrEmpty(s) || widthUnits < 1) return;
            int w = 0;
            int i = 0;
            while (i < s.Length)
            {
                int c = s[i] < 0x2E80 ? 1 : 2;
                if (w + c > widthUnits) break;
                w += c;
                i++;
            }
            if (i < 1) i = 1;   // always make progress, even in a 1-unit-wide pane
            piece = s.Substring(0, i);
            consumed = i;
        }

        private sealed class DPage
        {
            public string Text = "";
            public int FirstLine;
            public int LastLine;
        }

        /// <summary>How many text lines the detail BODY can show. Derived from the pane's
        /// measured height minus the footer's reserved row, never a constant: at a 520x320
        /// window the band is 80px tall, and the fixed 34-line page painted most of its
        /// lines past the bottom edge.</summary>
        private static int DetailPage()
        {
            double h = _detailBox == null ? 0.0 : _detailBox.ActualHeight;
            if (h < 1.0) return DetailPageLines;
            int n = (int)Math.Floor((h - 10.0 - DetailFooterReserve) / RowHeight);
            return n < 1 ? 1 : n;
        }

        /// <summary>Roughly how many characters share one visual line. Measured from the
        /// detail text's own width - not the band's, which also covers the action pane and
        /// made the wrap estimate twice too optimistic, so a page looked half empty while
        /// its last lines were drawn past the pane edge.</summary>
        private static int CharsPerLine()
        {
            double w = 0.0;
            try
            {
                if (_detailText != null && _detailText.ActualWidth > 1.0) w = _detailText.ActualWidth;
            }
            catch (Exception) { }
            if (w < 1.0 && _band != null)
            {
                try { w = Math.Max(0.0, (double)_band.ColumnDefinitions[0].ActualWidth - 12.0); }
                catch (Exception) { }
            }
            if (w < 1.0 && _detailBox != null) w = Math.Max(0.0, _detailBox.ActualWidth * 0.5 - 12.0);
            if (w < 1.0) return 40;
            int n = (int)Math.Floor(w / 7.0);
            return n < 8 ? 8 : n;
        }

        /// <summary>Visual lines one source line occupies once wrapped; CJK counts double
        /// width because it really is twice as wide at the same point size.</summary>
        private static int VisualLines(string s, int charsPerLine)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            int w = 0;
            for (int i = 0; i < s.Length; i++) w += s[i] < 0x2E80 ? 1 : 2;
            return (w + charsPerLine - 1) / charsPerLine < 1 ? 1 : (w + charsPerLine - 1) / charsPerLine;
        }

        private static void OnDetailWheel(object sender, MouseWheelEventArgs e)
        {
            if (e == null) return;
            EnsurePages();
            if (_pages.Count <= 1 && !_detailCapped)
            {
                // One page: the wheel belongs to the game camera, not to this pane.
                e.Handled = false;
                return;
            }
            _pageIndex = _pageIndex + (e.Delta > 0 ? -1 : 1);
            if (_pageIndex < 0) _pageIndex = 0;
            if (_pageIndex > _pages.Count - 1) _pageIndex = _pages.Count - 1;
            PaintDetail();
            e.Handled = true;
        }

        // ================================================== geometry
        // One normalisation order, used by install, remount, move, resize, restore,
        // maximize, collapse and a host resize. Previously each of those clamped what
        // it happened to know about: the resize branch re-raised the size to the
        // minimum AFTER clamping it to the viewport (so it could end up outside
        // again), and the split was only clamped from ClampGeometry, which the resize
        // branch never called. Anything that writes geometry now writes it here.
        //
        //   available rect -> size -> position -> inner space -> split -> layout

        /// <summary>
        /// The rect the window may occupy, in the space Mouse.GetPosition(_host)
        /// reports. It is the host's own layout box: there is no larger space a child
        /// of that host can paint into, so pretending the space is bigger (the old
        /// Math.Max(MinW + 8, host.ActualWidth)) is exactly how a window ends up
        /// parked outside the visible area on a small viewport.
        /// </summary>
        private static bool AvailRect(out double aw, out double ah)
        {
            aw = 0;
            ah = 0;
            if (_host == null) return false;
            aw = (double)_host.ActualWidth;
            ah = (double)_host.ActualHeight;
            return aw >= 1.0 && ah >= 1.0;
        }

        // Space inside the window border. The panes only ever get this much.
        private static double ContentW() { return _w - 2.0; }
        private static double ContentH() { return (_collapsed ? TitleHeight : _h) - 2.0; }

        // Upper bound for the vertical splitter, derived from the content area.
        // Uses the EXPANDED height on purpose. Deriving it from ContentH() while collapsed
        // (which is TitleHeight-2 = 24) makes the bound fall under MinTimelinePx, so every
        // collapse rewrote the real split down to the minimum and expanding could not bring
        // it back - measured live: 295.35 -> 60, then expand kept 60 and handed 441.96 to
        // the detail pane. Collapsing must not edit a value it is not even showing.
        private static double MaxSplitPx()
        {
            double contentH = _h - 2.0;
            double room = contentH - TitleHeight - ToolbarHeight - SplitHeight - MinDetailPx;
            return room < MinTimelinePx ? MinTimelinePx : room;
        }

        // Upper bound for the horizontal splitter: whatever the action pane may take
        // once the detail column keeps its minimum.
        private static double MaxSplitXPx()
        {
            double room = ContentW() - SplitHeight - MinDetailXPx;
            return room < MinActionPx ? MinActionPx : room;
        }

        private static void ClampSplit()
        {
            // Compact decisions come first: they decide what the split is allowed to
            // be. Both are one tree with a pane folded away, never a second layout.
            //
            // GUARD: for any viewport at or above the floor (MinW=520/MinH=320) the
            // smallest content area is 518x318, above both fold thresholds (373 wide, 193
            // tall), so neither branch can fire there - measured live: asking for 370x190
            // produced a 520x320 window reporting compact false/false.
            //
            // It is NOT unreachable in every case, and the earlier wording claimed too
            // much: NormalizeGeometry lets the window shrink to a viewport that is itself
            // smaller than the floor, and collapsing reduces ContentH() to the title bar,
            // so ContentH() below the tall threshold is reachable by collapsing. The
            // branches stay because the floor is a policy, not a law: if MinW ever drops
            // under ~375 the fold is what stops the two panes fighting over the last
            // pixels. Static gate C147 keeps this note and the constants in sync.
            _compactW = ContentW() < (MinDetailXPx + MinActionPx + SplitHeight + 8);
            _compactH = (ContentH() - TitleHeight - ToolbarHeight - SplitHeight)
                        < (MinTimelinePx + MinDetailPx);

            // _splitPx/_splitXPx are the EFFECTIVE values the layout uses; _splitPxPref and
            // _splitXPxPref are what the player asked for. Clamping only the effective pair
            // is what makes a shrink non-destructive: the probe showed a window at 640x420
            // turned a 295px timeline into 285, then 370x190 turned it into 185, and growing
            // back to 735x557 left it at 185 - the player's divider was simply eaten.
            _splitPx = ClampTo(_splitPxPref, MinTimelinePx, MaxSplitPx());
            if (_compactW) return;   // the horizontal splitter is folded; keep the value
            _splitXPx = ClampTo(_splitXPxPref, MinActionPx, MaxSplitXPx());
        }

        private static float ClampTo(double want, double lo, double hi)
        {
            if (want < lo) want = lo;
            if (want > hi) want = hi;
            return (float)want;
        }

        /// <summary>
        /// Turn "what was asked for" into "what fits". Returns false when the host has
        /// no layout box yet (the first install happens before the first arrange), in
        /// which case the caller must not write a layout that depends on the size.
        ///
        /// The minimum is relaxed to the available area instead of the area being
        /// inflated: forcing 520x320 into a 400x300 host is not a preference, it is a
        /// window hanging off the edge.
        /// </summary>
        private static bool NormalizeGeometry()
        {
            double aw, ah;
            if (!AvailRect(out aw, out ah)) return false;

            double wantH = _collapsed ? TitleHeight : _h;

            if (_maximized)
            {
                _left = 0;
                _top = 0;
                _w = aw;
                if (!_collapsed) _h = ah;
            }
            else
            {
                double minW = MinW < aw ? MinW : aw;
                double minH = MinH < ah ? MinH : ah;

                if (_w < minW) _w = minW;
                if (_w > aw) _w = aw;
                if (!_collapsed)
                {
                    if (_h < minH) _h = minH;
                    if (_h > ah) _h = ah;
                    wantH = _h;
                }

                // Position last, against the size that survived the clamp: clamping
                // position first and size second is what left the old code with a
                // rectangle that was inside the viewport for one step only.
                if (_left < 0) _left = 0;
                if (_top < 0) _top = 0;
                if (_left + _w > aw) _left = aw - _w;
                if (_top + wantH > ah) _top = ah - wantH;
                if (_left < 0) _left = 0;
                if (_top < 0) _top = 0;
            }

            ClampSplit();
            return true;
        }

        /// <summary>
        /// Kept for callers that still think in "clamp against this viewport". The
        /// viewport argument is ignored on purpose - the host is the only authority -
        /// so no caller can reintroduce a virtual, larger space.
        /// </summary>
        private static void ClampGeometry(double ignoredW, double ignoredH)
        {
            NormalizeGeometry();
        }

        // Real definitions for the rows and columns that take the leftover space.
        // They are kept so the splitter position can be written back into the actual
        // row height and so State() can report the grid's own answer instead of ours.
        private static RowDefinition _bodyRowTimeline;
        private static RowDefinition _bodyRowDetail;
        private static Grid _rootGrid;
        private static Grid _bodyGrid;
        private static bool _fontDirty;
        private static int _fontWalkLog = -1;

        // Walk the whole tree once and keep the largest panel: the map area is
        // inset, the container above it is not, and only the latter can hold a
        // window anywhere on screen without clipping it.
        private static Panel PickMountHost(FrameworkElement root, Panel named)
        {
            // The named map area is the authority. It was verified at 1920x1080 inside a
            // tactical view that also reports 1920x1080, while the chain above it
            // advertises DEVICE pixels (AdornerDecorator 3840x2160 on a 2x dpi host).
            // Picking a host by area compares numbers from two different scale spaces and
            // can mount into the doubled one, where every coordinate lands at twice its
            // size and the right and bottom leave the screen. Area is not evidence.
            if (named != null
                && (double)named.ActualWidth >= 1.0 && (double)named.ActualHeight >= 1.0)
                return named;

            // No layout box yet (install can land before the first arrange). Fall back to
            // the largest panel that still fits inside the tactical view, so at worst the
            // window is confined to the visible area.
            Panel best = named;
            double bestArea = named == null ? 0 : (double)named.ActualWidth * named.ActualHeight;
            try
            {
                int[] budget = new int[] { 4000 };
                Panel found = WalkLargest(root, 0, budget, ref bestArea);
                // Only accept a host that is not larger than the tactical view itself:
                // mounting into the bigger one doubles every coordinate.
                if (found != null && root != null
                    && (double)found.ActualWidth <= (double)root.ActualWidth + 1d
                    && (double)found.ActualHeight <= (double)root.ActualHeight + 1d)
                {
                    best = found;
                }
                else if (found != null)
                {
                    Core.Log("inspector mount host " + Math.Round((double)found.ActualWidth)
                        + "x" + Math.Round((double)found.ActualHeight)
                        + " is larger than the tactical view; keeping the map area");
                }
            }
            catch (Exception ex)
            {
                Core.Log("inspector mount host walk failed: " + ex.GetType().Name);
            }
            return best;
        }

        private static Panel WalkLargest(object node, int depth, int[] budget, ref double bestArea)
        {
            if (node == null || depth > 12 || budget[0] <= 0) return null;
            budget[0] = budget[0] - 1;
            Panel best = null;
            FrameworkElement fe = node as FrameworkElement;
            Panel panel = node as Panel;
            if (panel != null && fe != null)
            {
                double area = (double)fe.ActualWidth * fe.ActualHeight;
                if (area > bestArea + 1.0)
                {
                    bestArea = area;
                    best = panel;
                }
            }
            UIElementCollection kids = null;
            if (panel != null) kids = panel.Children;
            else
            {
                ContentControl cc = node as ContentControl;
                Border bd = node as Border;
                if (cc != null && cc.Content is UIElement) return WalkLargest(cc.Content, depth + 1, budget, ref bestArea);
                if (bd != null && bd.Child != null) return WalkLargest(bd.Child, depth + 1, budget, ref bestArea);
            }
            if (kids == null) return best;
            int n = kids.Count;
            for (int i = 0; i < n; i++)
            {
                Panel sub = WalkLargest(kids[i], depth + 1, budget, ref bestArea);
                if (sub != null) best = sub;
            }
            return best;
        }

        private static object ParentOf(object node)
        {
            if (node == null) return null;
            try
            {
                System.Reflection.PropertyInfo pi = node.GetType().GetProperty("Parent");
                if (pi != null) return pi.GetValue(node, null);
                pi = node.GetType().GetProperty("VisualParent");
                if (pi != null) return pi.GetValue(node, null);
            }
            catch (Exception) { }
            return null;
        }

        private static JObject HostChain()
        {
            JArray chain = new JArray();
            object node = _wrap;
            int guard = 0;
            while (node != null && guard < 16)
            {
                guard++;
                JObject row = new JObject();
                row["type"] = node.GetType().Name;
                FrameworkElement fe = node as FrameworkElement;
                row["name"] = fe == null ? null : fe.Name;
                row["actualWidth"] = fe == null ? 0 : Math.Round((double)fe.ActualWidth, 1);
                row["actualHeight"] = fe == null ? 0 : Math.Round((double)fe.ActualHeight, 1);
                chain.Add(row);
                node = ParentOf(node);
            }
            JObject o = new JObject();
            o["chain"] = chain;
            return o;
        }

        // Move the window between the map area and the full-screen container
        // without restarting the game.
        private static JObject Remount(string to)
        {
            Panel target = _host;
            if (!string.IsNullOrEmpty(to))
            {
                if (to == "display") target = _displayHost;
                else if (to == "full")
                {
                    Panel picked = PickMountHost(UiBridge.TacticalRootForInspector(), _displayHost);
                    target = picked;
                }
            }
            if (target == null) throw new ApiException(409, "no_host", "no mount host is available", false);
            if (target == _host && _wrap != null && _wrap.Parent == _host) return State();

            if (_wrap != null)
            {
                try { FrameworkElement old = _wrap.Parent as FrameworkElement; if (old is Panel) ((Panel)old).Children.Remove(_wrap); }
                catch (Exception ex) { Core.Log("inspector remount detach failed: " + ex.GetType().Name); }
            }
            _host = target;
            _host.Children.Add(_wrap);
            Noesis.Panel.SetZIndex(_wrap, 10000);
            // A different host means a different available rect, and possibly a
            // different coordinate scale. Forget the old one so the next fit is made
            // against this host instead of the previous host's box.
            _lastHostW = -1;
            _lastHostH = -1;
            _lastSignature = -1;
            ApplyGeometry();
            double hw, hh;
            if (AvailRect(out hw, out hh)) { _lastHostW = hw; _lastHostH = hh; }
            Core.Log("inspector remounted onto " + _host.GetType().Name + " " + _host.Name
                + " " + Math.Round((double)_host.ActualWidth) + "x" + Math.Round((double)_host.ActualHeight));
            return State();
        }

        private static void ApplyGeometry()
        {
            if (_wrap == null) return;
            // Every size change funnels through here, so the bounds are enforced here
            // instead of at each caller. Nothing else may write _left/_top/_w/_h/_split*
            // without going through NormalizeGeometry first; this is the single point
            // where "asked for" becomes "laid out".
            NormalizeGeometry();

            // Embedded: fill the content well and drop the title row. The shell owns the
            // window buttons now, so a second set here would be two title bars.
            if (_embedded && _host != null)
            {
                double hostW = (double)_host.ActualWidth;
                double hostH = (double)_host.ActualHeight;
                if (hostW >= MinW && hostH >= MinH)
                {
                    _left = 0;
                    _top = 0;
                    _w = hostW;
                    _h = hostH;
                    _collapsed = false;
                }
                if (_titleRow != null) _titleRow.Height = new GridLength(0f);
            }

            double eh = _collapsed ? TitleHeight : _h;
            _wrap.Visibility = _visible ? Visibility.Visible : Visibility.Collapsed;
            _wrap.Width = (float)_w;
            _wrap.Height = (float)eh;
            _wrap.Margin = new Thickness((float)_left, (float)_top, 0, 0);

            if (_outer != null)
            {
                _outer.Width = (float)_w;
                _outer.Height = (float)eh;
            }

            // Compact width: the action pane would be narrower than a readable column,
            // so it folds and the detail text takes the whole band. Same tree, one
            // column at zero - not a second layout, and dragging still works.
            if (_rightCol != null)
                _rightCol.Width = _compactW ? new GridLength(0) : new GridLength((float)_splitXPx);
            if (_splitXBar != null)
                _splitXBar.Visibility = _compactW ? Visibility.Collapsed : Visibility.Visible;

            // Compact height: no room for a timeline, a splitter and a readable detail
            // band, so the band folds and the timeline keeps the whole body.
            if (_bodyRowTimeline != null)
                _bodyRowTimeline.Height = _compactH
                    ? new GridLength(1f, GridUnitType.Star)
                    : new GridLength((float)_splitPx);
            if (_bodyRowDetail != null)
                _bodyRowDetail.Height = _compactH
                    ? new GridLength(0)
                    : new GridLength(1f, GridUnitType.Star);
            if (_splitYBar != null)
                _splitYBar.Visibility = _compactH ? Visibility.Collapsed : Visibility.Visible;
            if (_detailBox != null)
                _detailBox.Visibility = _compactH ? Visibility.Collapsed : Visibility.Visible;

            // Deliberately no signature reset here. Resetting it forced a full
            // RebuildRows() on every mouse move of a drag, which is what made the
            // frame resize hitch. Refresh() sees the new FitRows() by itself.
        }

        // True when the mount host is as big as the NoesisView it lives in, i.e.
        // maximising it cannot clip. Used only to explain the log line.
        private static bool HostIsScreenSized()
        {
            try
            {
                FrameworkElement root = UiBridge.TacticalRootForInspector();
                if (root == null || _host == null) return false;
                return (double)_host.ActualWidth >= (double)root.ActualWidth - 1.0
                    && (double)_host.ActualHeight >= (double)root.ActualHeight - 1.0;
            }
            catch (Exception) { return false; }
        }

        private static void Maximize()
        {
            if (_maximized) return;
            double aw, ah;
            if (!AvailRect(out aw, out ah))
                throw new ApiException(409, "no_viewport",
                    "the mount host has no layout box yet; the window can not be maximised", false);
            _savedLeft = _left;
            _savedTop = _top;
            _savedW = _w;
            _savedH = _h;
            _maximized = true;
            _collapsed = false;
            _left = 0;
            _top = 0;
            _w = aw;
            _h = ah;
            ApplyGeometry();
            // Deliberately NOT saved: the maximised rectangle is not a geometry the
            // player chose. Saving it made the next launch open at 0,0,1,1 and the
            // window looked like it ignored its own setting.
            Core.Log("inspector maximised onto host " + Math.Round(aw) + "x" + Math.Round(ah)
                + (HostIsScreenSized() ? "" : " (host may be smaller than the screen)")
                + "; restore size kept at " + (int)_savedW + "x" + (int)_savedH);
        }

        private static void Restore()
        {
            if (!_maximized && !_collapsed) return;
            _maximized = false;
            _collapsed = false;
            _left = _savedLeft;
            _top = _savedTop;
            _w = _savedW;
            _h = _savedH;
            // The saved geometry may no longer fit - a smaller viewport since it was
            // stored, or a different monitor. ApplyGeometry normalises it.
            ApplyGeometry();
            SaveGeometry("restore");
        }

        private static void Collapse(bool on)
        {
            if (on && !_collapsed && !_maximized)
            {
                _savedW = _w;
                _savedH = _h;
                _savedLeft = _left;
                _savedTop = _top;
            }
            _collapsed = on;
            ApplyGeometry();
            SaveGeometry(on ? "collapse" : "expand");
        }

        private static void SaveGeometry(string why)
        {
            try
            {
                if (_host == null) return;
                double vw = Math.Max(1, _host.ActualWidth);
                double vh = Math.Max(1, _host.ActualHeight);
                // While maximised, the on-screen rectangle is the viewport, not a
                // preference: persist the geometry the player had before maximising.
                double saveLeft = _maximized ? _savedLeft : _left;
                double saveTop = _maximized ? _savedTop : _top;
                double saveW = _maximized ? _savedW : _w;
                double saveH = _maximized ? _savedH : _h;
                InspectorConfig cfg = LoadConfig();
                cfg.Left = Clamp01(saveLeft / vw);
                cfg.Top = Clamp01(saveTop / vh);
                cfg.Width = Clamp01(saveW / vw);
                cfg.Height = Clamp01(saveH / vh);
                // Save the wish, not the emergency: a window that was shrunk to its floor
                // must not persist that floor as the player's preferred divider.
                //
                // Basis matters more than precision here. Install multiplies SplitY by the
                // OUTER height (_cfg.SplitY * _h), so saving against the content height
                // meant every save/restore round trip shrank the divider - a preference
                // that drifts each time the window is moved is worse than no preference.
                // Both sides now use _h / _w.
                cfg.SplitY = Clamp01(_splitPxPref / Math.Max(1.0, _h));
                cfg.SplitX = Clamp01(_splitXPxPref / Math.Max(1.0, _w));

                string dir = SettingsDir();
                string error;
                if (!InspectorSettings.Save(dir, cfg, out error))
                {
                    _lastGeoError = error;
                    Core.Log("inspector geometry not saved (" + why + "): " + error);
                    return;
                }
                _lastGeoError = null;
                Core.Log("inspector geometry saved (" + why + ")");
            }
            catch (Exception ex)
            {
                _lastGeoError = ex.GetType().Name;
                Core.Log("inspector geometry save failed: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// ACT-010 / I1: make the window's text render in a family that actually has
        /// CJK glyphs.
        ///
        /// The labels came out as boxes because the family in use has no Chinese
        /// coverage. Noesis exposes two levers, and neither can be assumed to work:
        /// SetFontFallbacks tells it which families to try when a glyph is missing, and
        /// FontFamily.GetNumFonts reports how many faces that family actually resolved
        /// to. Both are probed and reported instead of being trusted, because "the text
        /// looks right" is not evidence when a wrong answer renders boxes.
        /// </summary>
        private static readonly string[] FontCandidates = new string[]
        {
            "Microsoft YaHei", "SimHei", "SimSun", "Noto Sans CJK SC",
            "Microsoft JhengHei", "Arial Unicode MS", "Segoe UI"
        };

        /// <summary>
        /// Apply a family that actually has CJK glyphs, at install time.
        ///
        /// It has to happen here and not only on demand: a family the font service
        /// can not resolve leaves the labels blank, and "blank" is indistinguishable
        /// from "the pane is missing". Every candidate is probed with
        /// FontFamily.GetNumFonts and the first one that resolves wins; if none does
        /// that is logged too, because a silent failure here looks exactly like a
        /// layout bug.
        /// </summary>
        private static void ApplyFontAtInstall()
        {
            // Do NOT pick a family on our own. The UiBridge panel renders Chinese with
            // the Noesis default, so overriding the family here is how Chinese went
            // missing in the first place. Only an explicitly configured family wins.
            string want = _cfg == null ? null : _cfg.Font;
            if (string.IsNullOrEmpty(want))
            {
                _fontFamily = "";
                Core.Log("inspector font: using the Noesis default (no family configured)");
                return;
            }

            System.Collections.Generic.List<string> order = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(want)) order.Add(want);
            for (int i = 0; i < FontCandidates.Length; i++)
                if (!order.Contains(FontCandidates[i])) order.Add(FontCandidates[i]);

            for (int i = 0; i < order.Count; i++)
            {
                JObject r = ApplyFontFamily(order[i], false);
                int got = -1;
                try
                {
                    JToken jn = r["numFonts"];
                    if (jn != null && jn.Type != JTokenType.Null) got = (int)jn;
                }
                catch (Exception) { got = -1; }
                if (got > 0)
                {
                    _fontFamily = order[i];
                    if (_cfg != null && _cfg.Font != order[i]) { _cfg.Font = order[i]; SaveGeometry("font"); }
                    Core.Log("inspector font at install -> " + order[i] + " numFonts=" + got);
                    return;
                }
            }
            Core.Log("inspector font at install: none of the candidates resolved a face");
        }

        /// <summary>
        /// Re-label every fixed string in place after a language switch. The tree is
        /// deliberately NOT torn down: /inspector/window remove+install would drop the
        /// geometry, the selection and the drag state the player is looking at, and the
        /// labels are the only thing that changed.
        /// </summary>
        internal static void ApplyLanguage()
        {
            if (_titleText != null) _titleText.Text = Strings.T("title");
            if (_actionsCaption != null) _actionsCaption.Text = Strings.T("actions");
            if (_detailText != null && (_selected < 0 || _selected >= _shown.Count))
                _detailText.Text = Strings.T("detail.select");
            if (_wrap == null) return;
            _lastSignature = -1;   // the status line and the action pane are rebuilt
            Refresh(true);
        }

        internal static JObject ApplyFontFamily(string family, bool setGlobalFallback)
        {
            JObject o = new JObject();
            string list = string.IsNullOrEmpty(family) ? "Microsoft YaHei" : family;

            // SetFontFallbacks is global: it changes how the whole game resolves a
            // face, so it only runs when the caller asks for it by name.
            o["globalFallback"] = setGlobalFallback;
            if (setGlobalFallback)
            {
                try
                {
                    Noesis.GUI.SetFontFallbacks(new string[] { list });
                    o["fallbacks"] = list;
                }
                catch (Exception ex)
                {
                    o["fallbackError"] = ex.GetType().Name + ": " + ex.Message;
                }
            }

            o["tried"] = list;
            int fonts = -1;
            Noesis.FontFamily ff = null;
            try
            {
                Type t = typeof(Noesis.FontFamily);
                System.Reflection.ConstructorInfo ci = t.GetConstructor(new Type[] { typeof(string) });
                if (ci == null) o["familyCtor"] = "no (String) constructor";
                else
                {
                    ff = (Noesis.FontFamily)ci.Invoke(new object[] { list });
                    o["familyCtor"] = "ok";
                }
            }
            catch (Exception ex)
            {
                o["familyError"] = ex.GetType().Name + ": " + ex.Message;
            }

            if (ff != null)
            {
                try { fonts = (int)ff.GetNumFonts(); }
                catch (Exception ex) { o["numFontsError"] = ex.GetType().Name; }
                o["numFonts"] = fonts;
            }

            int touched = 0;
            if (ff != null && _wrap != null)
            {
                try { touched = ApplyFamilyWalk(_wrap, ff, 0, new int[1]); }
                catch (Exception ex) { o["walkError"] = ex.GetType().Name + ": " + ex.Message; }
            }
            o["textBlocksRetargeted"] = touched;
            o["verdict"] = fonts > 0
                ? "the family resolved to " + fonts + " face(s); look at the window"
                : "the family did not resolve; try another name from the tried list";
            Core.Log("inspector font -> " + list + " numFonts=" + fonts + " touched=" + touched);
            return o;
        }

        /// <summary>
        /// Retarget every TextBlock under root. Reflection rather than a cast because
        /// the concrete container types here are Panel / Decorator / ContentControl and
        /// picking one to cast to turns a "not found" into a compile error.
        /// </summary>
        private static int ApplyFamilyWalk(object node, Noesis.FontFamily ff, int depth, int[] budget)
        {
            if (node == null || depth > 14) return 0;
            if (++budget[0] > 3000) return 0;
            int n = 0;

            Type t = node.GetType();
            if (t.Name == "TextBlock" || t.Name == "Control" || t.Name == "ListBoxItem")
            {
                System.Reflection.PropertyInfo fp = t.GetProperty("FontFamily",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (fp != null && fp.CanWrite)
                {
                    try { fp.SetValue(node, ff, null); n++; }
                    catch (Exception) { }
                }
            }

            string[] carriers = new string[] { "Children", "Child", "Content", "Items" };
            for (int i = 0; i < carriers.Length; i++)
            {
                System.Reflection.PropertyInfo p = t.GetProperty(carriers[i],
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p == null) continue;
                object v = null;
                try { v = p.GetValue(node, null); }
                catch (Exception) { continue; }
                if (v == null) continue;
                System.Collections.IEnumerable seq = v as System.Collections.IEnumerable;
                if (seq == null || v is string)
                {
                    n += ApplyFamilyWalk(v, ff, depth + 1, budget);
                    continue;
                }
                foreach (object child in seq)
                    n += ApplyFamilyWalk(child, ff, depth + 1, budget);
            }
            return n;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        /// <summary>
        /// The configured toggle key, readable before the window is installed.
        /// A live session showed the Noesis key path delivering nothing at all
        /// (/ui/state keyLog stayed empty while the root listener was reported as
        /// attached), so the toggle is polled from the main thread instead. That path
        /// does not depend on which element holds UI focus.
        /// </summary>
        internal static string ToggleKeyName()
        {
            _cfg = LoadConfig();
            if (_cfg == null || string.IsNullOrEmpty(_cfg.ToggleKey)) return "N";
            return _cfg.ToggleKey;
        }

        private static InspectorConfig LoadConfig()
        {
            try { return InspectorSettings.Load(SettingsDir()); }
            catch (Exception) { return InspectorSettings.Defaults(); }
        }

        private static string SettingsDir()
        {
            try
            {
                string dir = Core.ModDir();
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            catch (Exception) { }
            return System.Environment.CurrentDirectory;
        }

        // ================================================== tree
        private static void BuildTree()
        {
            _wrap = new Grid();
            _wrap.Width = (float)_w;
            _wrap.Height = (float)_h;
            _wrap.Margin = new Thickness((float)_left, (float)_top, 0, 0);
            _wrap.HorizontalAlignment = HorizontalAlignment.Left;
            _wrap.VerticalAlignment = VerticalAlignment.Top;
            // Painted on the wrap as well as on the Border inside it: if that Border
            // ever fails to arrange, a solid block still says "mounted and visible",
            // where "nothing on screen" would be ambiguous.
            _wrap.Background = new SolidColorBrush(Color.FromArgb(250, 8, 18, 30));

            _outer = new Border();
            _outer.Name = WindowName;
            _outer.Background = new SolidColorBrush(Color.FromArgb(235, 8, 18, 30));
            _outer.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 200, 255));
            _outer.BorderThickness = new Thickness(1);
            _outer.Width = (float)_w;
            _outer.Height = (float)_h;
            _outer.HorizontalAlignment = HorizontalAlignment.Stretch;
            _outer.VerticalAlignment = VerticalAlignment.Stretch;
            _outer.IsHitTestVisible = true;

            Grid root = new Grid();
            _rootGrid = root;
            _titleRow = Row(TitleHeight);
            root.RowDefinitions.Add(_titleRow);
            root.RowDefinitions.Add(Row(0, true));

            Border title = BuildTitleBar();
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            Grid body = new Grid();
            _bodyGrid = body;
            _bodyRowTimeline = Row(0, true);
            _bodyRowDetail = Row(0, true);
            body.RowDefinitions.Add(Row(ToolbarHeight));
            body.RowDefinitions.Add(_bodyRowTimeline);
            body.RowDefinitions.Add(Row(SplitHeight));
            body.RowDefinitions.Add(_bodyRowDetail);

            Border toolbar = new Border();
            toolbar.Background = new SolidColorBrush(Color.FromArgb(255, 12, 26, 42));
            toolbar.Padding = new Thickness(6, 2, 6, 2);
            _statusText = new TextBlock();
            _statusText.FontSize = 12;
            _statusText.TextWrapping = TextWrapping.NoWrap;
            _statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 205, 240));
            _statusText.Text = Strings.T("status.starting");
            toolbar.Child = _statusText;
            Grid.SetRow(toolbar, 0);
            body.Children.Add(toolbar);

            _rowsPanel = new StackPanel();
            _rowsPanel.Orientation = Orientation.Vertical;

            _timelineBox = new Border();
            _timelineBox.Background = new SolidColorBrush(Color.FromArgb(255, 9, 20, 33));
            _timelineBox.Padding = new Thickness(0);
            _timelineBox.IsHitTestVisible = true;
            _timelineBox.Child = _rowsPanel;
            _timelineBox.ClipToBounds = true;
            try { _timelineBox.MouseWheel += new MouseWheelEventHandler(OnTimelineWheel); }
            catch (Exception ex) { Core.Log("inspector wheel not wired: " + ex.GetType().Name); }
            Grid.SetRow(_timelineBox, 1);
            body.Children.Add(_timelineBox);

            Border split = new Border();
            _splitYBar = split;
            split.Name = WindowName + "Split";
            split.Background = new SolidColorBrush(Color.FromArgb(255, 40, 80, 120));
            split.IsHitTestVisible = true;
            try { split.Cursor = Cursors.SizeNS; } catch (Exception) { }
            try { split.MouseLeftButtonDown += new MouseButtonEventHandler(OnSplitDown); }
            catch (Exception) { }
            Grid.SetRow(split, 2);
            body.Children.Add(split);

            _detailText = new TextBlock();
            _detailText.FontSize = 12;
            _detailText.TextWrapping = TextWrapping.Wrap;
            _detailText.Foreground = new SolidColorBrush(Color.FromArgb(255, 205, 230, 250));
            _detailText.Text = Strings.T("detail.select");

            // The band holds two of the design's four regions: the event detail on the
            // left and the "input and action" timeline on the right, with a splitter
            // between them. Both are views over the same buffer, so they can not
            // disagree about what happened.
            Grid band = new Grid();
            band.ColumnDefinitions.Add(Col(0, true));
            band.ColumnDefinitions.Add(Col(SplitHeight));
            _rightCol = Col(0, true);
            band.ColumnDefinitions.Add(_rightCol);

            Border detailPane = new Border();
            detailPane.Padding = new Thickness(6);
            detailPane.Child = _detailText;
            Grid.SetColumn(detailPane, 0);
            band.Children.Add(detailPane);

            _splitXBar = new Border();
            _splitXBar.Name = WindowName + "SplitX";
            _splitXBar.Background = new SolidColorBrush(Color.FromArgb(255, 40, 80, 120));
            _splitXBar.IsHitTestVisible = true;
            try { _splitXBar.Cursor = Cursors.SizeWE; } catch (Exception) { }
            try { _splitXBar.MouseLeftButtonDown += new MouseButtonEventHandler(OnSplitXDown); }
            catch (Exception) { }
            Grid.SetColumn(_splitXBar, 1);
            band.Children.Add(_splitXBar);

            Grid right = new Grid();
            right.RowDefinitions.Add(Row(18));
            right.RowDefinitions.Add(Row(0, true));

            Border rightTitle = new Border();
            rightTitle.Background = new SolidColorBrush(Color.FromArgb(255, 14, 30, 48));
            rightTitle.Padding = new Thickness(6, 1, 6, 1);
            TextBlock rightLabel = new TextBlock();
            rightLabel.FontSize = 11;
            rightLabel.Foreground = new SolidColorBrush(Color.FromArgb(255, 140, 195, 235));
            rightLabel.Text = Strings.T("actions");
            // NoWrap: a wrapping caption in a 160px column pushes the row taller than its
            // declared height and the extra line is what used to bleed over the frame.
            rightLabel.TextWrapping = TextWrapping.NoWrap;
            _actionsCaption = rightLabel;
            rightTitle.Child = rightLabel;
            Grid.SetRow(rightTitle, 0);
            right.Children.Add(rightTitle);

            _actionPanel = new StackPanel();
            _actionPanel.Orientation = Orientation.Vertical;

            Border actionBox = new Border();
            actionBox.Background = new SolidColorBrush(Color.FromArgb(255, 9, 20, 33));
            actionBox.Child = _actionPanel;
            // A pane paints inside its own box or not at all. Without this a wrapped line
            // that is one pixel too tall is drawn PAST the last row and lands on top of the
            // window frame - which is exactly what "the window is still clipped" looked like.
            actionBox.ClipToBounds = true;
            Grid.SetRow(actionBox, 1);
            right.Children.Add(actionBox);
            right.ClipToBounds = true;

            Grid.SetColumn(right, 2);
            band.Children.Add(right);

            _band = band;
            // The band and the paging footer live in the same tree, one above the other:
            // the footer keeps its own auto row, so a full page of body can never push it
            // past the pane edge (measured before the fix: the footer vanished exactly
            // when it was needed, on the longest payloads). Hiding it collapses the row.
            Grid detailStack = new Grid();
            detailStack.RowDefinitions.Add(Row(0, true));
            RowDefinition footerRow = new RowDefinition();
            footerRow.Height = GridLength.Auto;
            detailStack.RowDefinitions.Add(footerRow);
            Grid.SetRow(band, 0);
            detailStack.Children.Add(band);

            _detailFooter = new TextBlock();
            _detailFooter.FontSize = 11;
            _detailFooter.TextWrapping = TextWrapping.NoWrap;
            _detailFooter.Foreground = new SolidColorBrush(Color.FromArgb(255, 140, 190, 225));
            _detailFooter.Visibility = Visibility.Collapsed;
            Border footerPane = new Border();
            footerPane.Padding = new Thickness(6, 1, 6, 2);
            footerPane.Child = _detailFooter;
            Grid.SetRow(footerPane, 1);
            detailStack.Children.Add(footerPane);

            _detailBox = new Border();
            _detailBox.Background = new SolidColorBrush(Color.FromArgb(255, 10, 22, 36));
            _detailBox.Padding = new Thickness(0);
            _detailBox.Child = detailStack;
            _detailBox.ClipToBounds = true;
            // A long payload is paged by wheel, not clipped: the footer states which page
            // is on screen, so truncation reads as "there is more" instead of looking like
            // the panel lost the data.
            try { _detailBox.MouseWheel += new MouseWheelEventHandler(OnDetailWheel); }
            catch (Exception ex) { Core.Log("inspector detail wheel not wired: " + ex.GetType().Name); }
            Grid.SetRow(_detailBox, 3);
            body.Children.Add(_detailBox);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            _outer.Child = root;
            _wrap.Children.Add(_outer);

            AddHandles();

            try { ApplyFontAtInstall(); }
            catch (Exception ex) { Core.Log("inspector font at install failed: " + ex.GetType().Name); }

            // One place sees every move and every release, no matter which handle
            // captured the mouse: a Border only gets events while the pointer is
            // inside it, which is not enough for a drag.
            try { _host.PreviewMouseMove += new MouseEventHandler(OnHostMove); }
            catch (Exception ex) { Core.Log("inspector move hook failed: " + ex.GetType().Name); }
            try { _host.PreviewMouseLeftButtonUp += new MouseButtonEventHandler(OnHostUp); }
            catch (Exception ex) { Core.Log("inspector up hook failed: " + ex.GetType().Name); }
        }

        private static Border BuildTitleBar()
        {
            Border bar = new Border();
            bar.Background = new SolidColorBrush(Color.FromArgb(255, 16, 38, 60));
            bar.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 60, 120, 170));
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.IsHitTestVisible = true;
            try { bar.Cursor = Cursors.SizeAll; } catch (Exception) { }
            try { bar.MouseLeftButtonDown += new MouseButtonEventHandler(OnTitleDown); }
            catch (Exception) { }

            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col(0, true));
            g.ColumnDefinitions.Add(Col(72));

            _titleText = new TextBlock();
            _titleText.FontSize = 13;
            _titleText.Margin = new Thickness(8, 0, 0, 0);
            _titleText.VerticalAlignment = VerticalAlignment.Center;
            _titleText.TextWrapping = TextWrapping.NoWrap;
            _titleText.Text = Strings.T("title");
            _titleText.Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 220, 255));
            _titleText.IsHitTestVisible = false;
            Grid.SetColumn(_titleText, 0);
            g.Children.Add(_titleText);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.VerticalAlignment = VerticalAlignment.Center;
            buttons.Margin = new Thickness(0, 0, 4, 0);

            buttons.Children.Add(TitleButton("-", 22, delegate { Collapse(!_collapsed); }));
            buttons.Children.Add(TitleButton("O", 22, delegate { if (_maximized) Restore(); else Maximize(); }));
            buttons.Children.Add(TitleButton("x", 22, delegate { Remove(); }));

            Grid.SetColumn(buttons, 1);
            g.Children.Add(buttons);

            bar.Child = g;
            return bar;
        }

        private static Border TitleButton(string label, double width, Action onClick)
        {
            Border b = new Border();
            b.Width = (float)width;
            b.Height = 18;
            b.Margin = new Thickness(2, 0, 0, 0);
            b.Background = new SolidColorBrush(Color.FromArgb(255, 26, 54, 82));
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 70, 120, 165));
            b.BorderThickness = new Thickness(1);
            b.IsHitTestVisible = true;
            try { b.Cursor = Cursors.Hand; } catch (Exception) { }

            TextBlock t = new TextBlock();
            t.FontSize = 12;
            t.Text = label;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.IsHitTestVisible = false;
            t.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 225, 250));
            b.Child = t;

            try
            {
                b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    if (e != null) e.Handled = true;
                    try { onClick(); } catch (Exception ex) { Core.Log("inspector button failed: " + ex.GetType().Name); }
                };
            }
            catch (Exception) { }
            return b;
        }

        /// <summary>
        /// Eight resize handles, each a Border with its own cursor. They live as
        /// siblings of the window Border inside the wrap Grid, so they sit on the
        /// window's own edge and are declared after it, which puts them on top.
        /// </summary>
        private static void AddHandles()
        {
            AddHandle("n", HorizontalAlignment.Stretch, VerticalAlignment.Top,
                double.NaN, HandleThick, 0, 0, 0, 0, Cursors.SizeNS);
            AddHandle("s", HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                double.NaN, HandleThick, 0, 0, 0, 0, Cursors.SizeNS);
            AddHandle("w", HorizontalAlignment.Left, VerticalAlignment.Stretch,
                HandleThick, double.NaN, 0, 0, 0, 0, Cursors.SizeWE);
            AddHandle("e", HorizontalAlignment.Right, VerticalAlignment.Stretch,
                HandleThick, double.NaN, 0, 0, 0, 0, Cursors.SizeWE);
            AddHandle("nw", HorizontalAlignment.Left, VerticalAlignment.Top,
                CornerSize, CornerSize, 0, 0, 0, 0, Cursors.SizeNWSE);
            AddHandle("ne", HorizontalAlignment.Right, VerticalAlignment.Top,
                CornerSize, CornerSize, 0, 0, 0, 0, Cursors.SizeNESW);
            AddHandle("sw", HorizontalAlignment.Left, VerticalAlignment.Bottom,
                CornerSize, CornerSize, 0, 0, 0, 0, Cursors.SizeNESW);
            AddHandle("se", HorizontalAlignment.Right, VerticalAlignment.Bottom,
                CornerSize, CornerSize, 0, 0, 0, 0, Cursors.SizeNWSE);
        }

        private static void AddHandle(string mode, HorizontalAlignment ha, VerticalAlignment va,
            double width, double height, double ml, double mt, double mr, double mb, Cursor cursor)
        {
            Border h = new Border();
            h.Name = WindowName + "Handle_" + mode;
            h.HorizontalAlignment = ha;
            h.VerticalAlignment = va;
            if (!double.IsNaN(width)) h.Width = (float)width;
            if (!double.IsNaN(height)) h.Height = (float)height;
            h.Margin = new Thickness((float)ml, (float)mt, (float)mr, (float)mb);
            // A transparent brush still hit-tests; null does not.
            h.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            h.IsHitTestVisible = true;
            try { h.Cursor = cursor; } catch (Exception) { }

            string m = mode;
            try
            {
                h.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    BeginDrag(m, s as UIElement);
                    if (e != null) e.Handled = true;
                };
            }
            catch (Exception) { }

            _wrap.Children.Add(h);
        }

        // ================================================== drag
        private static void BeginDrag(string mode, UIElement source)
        {
            // A collapsed window is just the title bar, and that bar still has to be
            // draggable: requiring an expand first was a pointless round trip. Only the
            // modes that need the body stay blocked.
            if (_collapsed && mode != "move") return;
            if (_maximized) return;
            _dragMode = mode;
            SetGameInput(false);
            try { _dragStart = Mouse.GetPosition(_host); }
            catch (Exception) { _dragStart = new Point(0, 0); }
            _dragLeft = _left;
            _dragTop = _top;
            _dragW = _w;
            _dragH = _h;
            _dragSplit = _splitPx;
            _dragCapture = source;
            _clampLogged = false;
            // No Mouse.Capture call: it is an instance member here and the host's
            // preview handlers already deliver every move and the release for us.
        }

        private static void OnTitleDown(object sender, MouseButtonEventArgs e)
        {
            // A maximised window can be clipped past the screen edge, which puts the
            // restore button out of reach. Dragging the title bar therefore restores
            // first and moves afterwards: the same gesture as a desktop window, and a
            // way out that does not depend on any on-screen button.
            if (_maximized)
            {
                Core.Log("inspector restored by dragging the maximised title bar");
                Restore();
            }
            BeginDrag("move", sender as UIElement);
            if (e != null) e.Handled = true;
        }

        private static void OnSplitDown(object sender, MouseButtonEventArgs e)
        {
            if (_wrap == null || _collapsed) return;
            BeginDrag("split", sender as UIElement);
            if (e != null) e.Handled = true;
        }

        private static void OnSplitXDown(object sender, MouseButtonEventArgs e)
        {
            if (!_visible || _collapsed || _maximized) return;
            _dragSplitX = _splitXPx;
            BeginDrag("splitx", sender as UIElement);
            if (e != null) e.Handled = true;
        }

        private static void OnHostMove(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrEmpty(_dragMode)) return;
            try
            {
                ApplyDragTick();
                if (e != null) e.Handled = true;
            }
            catch (Exception ex)
            {
                Core.Log("inspector drag failed: " + ex.GetType().Name);
                _dragMode = "";
            }
        }

        private static void ApplyDragTick()
        {
            if (_host == null) return;
            Point p = Mouse.GetPosition(_host);
            ApplyDrag(p.X - _dragStart.X, p.Y - _dragStart.Y);
        }

        private static void ApplyDrag(double dx, double dy)
        {
            if (_dragMode == "splitx")
            {
                // Dragging left widens the action pane.
                _splitXPxPref = _dragSplitX - dx;
                ApplyGeometry();
                return;
            }

            if (_dragMode == "move")
            {
                // Only the origin is decided here. Clamping the origin and then the
                // size separately is what let the two disagree; ApplyGeometry ->
                // NormalizeGeometry clamps position last, against the final size.
                _left = _dragLeft + dx;
                _top = _dragTop + dy;
                ApplyGeometry();
                return;
            }

            if (_dragMode == "split")
            {
                // Drag writes the PREFERENCE; ClampSplit derives what the layout can show.
                _splitPxPref = _dragSplit + dy;
                ApplyGeometry();
                return;
            }

            double l = _dragLeft, t = _dragTop, w = _dragW, h = _dragH;
            if (_dragMode.IndexOf('e') >= 0) w = _dragW + dx;
            if (_dragMode.IndexOf('s') >= 0) h = _dragH + dy;
            if (_dragMode.IndexOf('w') >= 0) { l = _dragLeft + dx; w = _dragW - dx; }
            if (_dragMode.IndexOf('n') >= 0) { t = _dragTop + dy; h = _dragH - dy; }

            _left = l;
            _top = t;
            _w = w;
            _h = h;
            ApplyGeometry();

            // One log line per drag when the request had to be changed: a silent
            // clamp is indistinguishable from a window that stopped following the
            // pointer, and the host size is the number that explains it.
            if (!_clampLogged && (Math.Abs(_w - w) > 0.5 || Math.Abs(_h - h) > 0.5))
            {
                _clampLogged = true;
                double aw, ah;
                AvailRect(out aw, out ah);
                Core.Log("inspector resize clamped to " + Math.Round(_w) + "x" + Math.Round(_h)
                    + " (asked " + Math.Round(w) + "x" + Math.Round(h)
                    + ", host " + Math.Round(aw) + "x" + Math.Round(ah)
                    + ", min " + Math.Min(MinW, aw) + "x" + Math.Min(MinH, ah) + ")");
            }
        }

        /// <summary>
        /// ACT-010 / I1: stop the game's own camera pan while a handle or the title bar
        /// is held.
        ///
        /// The report was "resizing sometimes pans the view too", which is what happens
        /// when the game reads the mouse for itself - Noesis marking an event handled
        /// does not reach a different input system. The bindings are InputActionMaps, so
        /// the MainGame map is switched off for the duration of the drag and back on
        /// afterwards. Tick() restores it if a release is ever missed (alt-tab, focus
        /// change), so this can not leave the game deaf.
        /// </summary>
        private static void SetGameInput(bool on)
        {
            bool wantOff = !on;
            if (wantOff == _gameInputOff) return;
            try
            {
                object handler = FindInputHandlerInstance();
                if (handler == null) { Core.Log("inspector input suppression: no InputHandler"); return; }
                object actions = FieldOf(handler, "inputActions");
                if (actions == null) { Core.Log("inspector input suppression: no inputActions"); return; }

                if (wantOff)
                {
                    _disabledMaps.Clear();
                    // Both fields and properties: the generated wrapper keeps its maps
                    // in backing fields (m_MainGame, m_Player, ...), so a property-only
                    // scan finds nothing and the game keeps reading the mouse - which is
                    // exactly what "0 input map(s)" in the log meant.
                    System.Reflection.PropertyInfo[] props = actions.GetType().GetProperties(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    for (int i = 0; i < props.Length; i++)
                    {
                        if (props[i].GetIndexParameters().Length > 0) continue;
                        if (props[i].PropertyType.Name != "InputActionMap") continue;
                        object map = null;
                        try { map = props[i].GetValue(actions, null); } catch (Exception) { continue; }
                        SilenceMap(map, props[i].Name);
                    }
                    System.Reflection.FieldInfo[] fis = actions.GetType().GetFields(
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    for (int i = 0; i < fis.Length; i++)
                    {
                        if (fis[i].FieldType.Name != "InputActionMap") continue;
                        object map = null;
                        try { map = fis[i].GetValue(actions); } catch (Exception) { continue; }
                        SilenceMap(map, fis[i].Name);
                    }
                    _gameInputOff = true;
                    // The call count is not evidence. Read `enabled` back so the log
                    // says how many maps are actually off, which is what the camera
                    // actually depends on.
                    int reallyOff = 0;
                    for (int i = 0; i < _disabledMapObjs.Count; i++)
                    {
                        try
                        {
                            System.Reflection.PropertyInfo ep =
                                _disabledMapObjs[i].GetType().GetProperty("enabled");
                            if (ep == null) continue;
                            object v = ep.GetValue(_disabledMapObjs[i], null);
                            if (v is bool && !(bool)v) reallyOff++;
                        }
                        catch (Exception) { }
                    }
                    Core.Log("inspector drag disabled " + _disabledMaps.Count + " input map(s), confirmed off "
                             + reallyOff + ": " + string.Join(", ", _disabledMaps.ToArray()));
                }
                else
                {
                    int n = 0;
                    for (int i = 0; i < _disabledMapObjs.Count; i++)
                    {
                        object map = _disabledMapObjs[i];
                        System.Reflection.MethodInfo em = map.GetType().GetMethod("Enable", Type.EmptyTypes);
                        if (em == null) continue;
                        em.Invoke(map, null);
                        n++;
                    }
                    _disabledMapObjs.Clear();
                    _gameInputOff = false;
                    Core.Log("inspector drag re-enabled " + n + " input map(s)");
                }
            }
            catch (Exception ex)
            {
                Core.Log("inspector input suppression failed: " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        /// <summary>
        /// Disable one action map if it is currently enabled, and remember it so the
        /// release path re-enables exactly the ones that were on - a map the game had
        /// already disabled must stay disabled.
        /// </summary>
        private static void SilenceMap(object map, string name)
        {
            if (map == null) return;
            try
            {
                Type mt = map.GetType();
                System.Reflection.PropertyInfo pe = mt.GetProperty("enabled");
                object v = pe == null ? null : pe.GetValue(map, null);
                if (v is bool && !(bool)v) return;
                System.Reflection.MethodInfo dm = mt.GetMethod("Disable", Type.EmptyTypes);
                if (dm == null) return;
                dm.Invoke(map, null);
                _disabledMaps.Add(name);
                _disabledMapObjs.Add(map);
            }
            catch (Exception) { }
        }

        private static object FindInputHandlerInstance()
        {
            Type t = FindTypeLocal("SeaPower.InputHandler");
            if (t == null) return null;
            try { return UnityEngine.Object.FindObjectOfType(t); }
            catch (Exception) { return null; }
        }

        private static Type FindTypeLocal(string fullName)
        {
            try
            {
                System.Reflection.Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = asms[i].GetType(fullName, false);
                    if (t != null) return t;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static object FieldOf(object target, string name)
        {
            if (target == null) return null;
            try
            {
                Type t = target.GetType();
                while (t != null)
                {
                    System.Reflection.FieldInfo f = t.GetField(name,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(target);
                    t = t.BaseType;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// The single way a drag ends. Release, watchdog, removal and host loss all go
        /// through here so the game can never be left with its bindings switched off.
        /// </summary>
        private static void EndDrag(string why)
        {
            if (string.IsNullOrEmpty(_dragMode) && !_gameInputOff) return;
            string mode = string.IsNullOrEmpty(_dragMode) ? "none" : _dragMode;
            _dragMode = "";
            _dragCapture = null;
            SetGameInput(true);
            if (mode != "none") SaveGeometry(mode);
            Core.Log("inspector drag ended (" + why + ") mode=" + mode
                     + " inputOff=" + (_gameInputOff ? "true" : "false"));
        }

        private static Noesis.FontFamily ResolveFamily(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                System.Reflection.ConstructorInfo ci =
                    typeof(Noesis.FontFamily).GetConstructor(new Type[] { typeof(string) });
                if (ci == null) return null;
                return (Noesis.FontFamily)ci.Invoke(new object[] { name });
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Rows are rebuilt on every refresh, so the family set at install time never
        /// reached the event TextBlocks. Re-run the same walk over the live tree; only
        /// when a rebuild happened, and only when a family is actually configured.
        /// </summary>
        private static void ApplyFontToFreshNodes()
        {
            if (_wrap == null || string.IsNullOrEmpty(_fontFamily)) return;
            Noesis.FontFamily ff = ResolveFamily(_fontFamily);
            if (ff == null) return;
            int[] budget = new int[] { 800 };
            int n = 0;
            try { n = ApplyFamilyWalk(_wrap, ff, 0, budget); }
            catch (Exception ex) { Core.Log("inspector font walk failed: " + ex.GetType().Name); return; }
            if (n != _fontWalkLog)
            {
                _fontWalkLog = n;
                Core.Log("inspector font reapplied to " + n + " node(s) after refresh (" + _fontFamily + ")");
            }
        }

        private static void OnHostUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(_dragMode)) return;
            EndDrag("pointer released");
        }

        private static void OnTimelineWheel(object sender, MouseWheelEventArgs e)
        {
            if (e == null) return;
            int step = e.Delta > 0 ? -3 : 3;
            int all = InspectorBuffer.Count;
            int fit = FitRows();
            int maxScroll = Math.Max(0, all - fit);
            _scrollRows = _scrollRows + step;
            if (_scrollRows < 0) _scrollRows = 0;
            if (_scrollRows > maxScroll) _scrollRows = maxScroll;
            _lastSignature = -1;
            Refresh(true);
            e.Handled = true;
        }

        private static int FitRows()
        {
            if (_wrap == null) return 10;
            double avail = _splitPx;
            int n = (int)Math.Floor(avail / RowHeight);
            if (n < 3) n = 3;
            if (n > MaxRowsDrawn) n = MaxRowsDrawn;
            return n;
        }

        // ================================================== small helpers
        private static RowDefinition Row(double px)
        {
            RowDefinition r = new RowDefinition();
            r.Height = new GridLength((float)px);
            return r;
        }

        private static RowDefinition Row(double star, bool starFlag)
        {
            RowDefinition r = new RowDefinition();
            // A 0 weight star takes ZERO of the leftover space. Every stretching row
            // here was written as Row(0, true) = "0*", so the whole body collapsed to
            // the pixel-sized title bar and only that line ever appeared on screen.
            double w = star <= 0d ? 1d : star;
            r.Height = GridLength.Parse(w.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "*");
            return r;
        }

        private static ColumnDefinition Col(double px)
        {
            ColumnDefinition c = new ColumnDefinition();
            c.Width = new GridLength((float)px);
            return c;
        }

        private static ColumnDefinition Col(double star, bool starFlag)
        {
            ColumnDefinition c = new ColumnDefinition();
            // Same trap as Row(): Col(0, true) is "0*" and gets no width at all.
            double w = star <= 0d ? 1d : star;
            c.Width = GridLength.Parse(w.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "*");
            return c;
        }

        private static TextBlock CellText(string text, int column)
        {
            TextBlock t = new TextBlock();
            t.FontSize = 12;
            t.Margin = new Thickness(5, 0, 4, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextWrapping = TextWrapping.NoWrap;
            t.Text = text ?? "";
            t.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 220, 245));
            Grid.SetColumn(t, column);
            return t;
        }

        private static string ShortTime(string ts)
        {
            if (string.IsNullOrEmpty(ts)) return "";
            // The buffer stamps yyyy-MM-ddTHH:mm:ss.fffZ (InspectorBridge). The previous
            // version returned the LAST 8 characters of that, i.e. ":49.219Z" - the date, the
            // hours and the minutes were thrown away and the seconds lost their first digit.
            // Everything in this window happens inside one session, so hours:minutes:seconds
            // plus milliseconds is the useful part and the date is not.
            DateTime dt;
            if (DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            int cut = ts.IndexOf('T');
            if (cut < 0) cut = ts.IndexOf(' ');
            if (cut >= 0 && ts.Length >= cut + 13) return ts.Substring(cut + 1, 12);
            return ts;
        }

        /// <summary>
        /// The kind column says what the row IS, and the colour says whether an operator
        /// needs to do something about it. "Rejected" is the bridge refusing a request that
        /// broke the contract - that is the bridge working, so it is amber, not red.
        /// </summary>
        private static Color KindColor(string eventType)
        {
            string s = eventType == null ? "" : eventType.ToLowerInvariant();
            if (s == "error") return Color.FromArgb(255, 250, 150, 150);
            if (s == "rejected") return Color.FromArgb(255, 250, 210, 140);
            if (s == "actionissued") return Color.FromArgb(255, 175, 225, 250);
            return Color.FromArgb(255, 190, 220, 245);
        }
        private static Color StatusColor(string status)
        {
            // Colour carries the status so a glance does not need to read the column.
            string s = status == null ? "" : status.ToLowerInvariant();
            if (s == "ok" || s == "completed" || s == "confirmed")
                return Color.FromArgb(255, 150, 235, 170);
            if (s == "error" || s == "failed" || s == "fail")
                return Color.FromArgb(255, 250, 150, 150);
            if (s == "refused" || s == "rejected" || s == "unknown")
                return Color.FromArgb(255, 250, 210, 140);
            // The status text is "http_400:bad_value" style, so the exact-match cases above
            // never fired for a real request. A rejected call is amber, a broken bridge is red.
            if (s.StartsWith("http_4", StringComparison.Ordinal))
                return Color.FromArgb(255, 250, 210, 140);
            if (s.StartsWith("http_5", StringComparison.Ordinal))
                return Color.FromArgb(255, 250, 150, 150);
            return Color.FromArgb(255, 175, 205, 235);
        }

        private static int ChildCount(Panel p)
        {
            if (p == null) return 0;
            try
            {
                var kids = p.Children;
                return kids == null ? 0 : kids.Count;
            }
            catch (Exception) { return 0; }
        }
    }
}
