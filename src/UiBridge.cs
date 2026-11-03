// ACT-004 / T06: a real, interactive insertion into the game's tactical UI.
//
// Design notes that matter more than the code:
//
//  * Primitives carry the layout (Border / TextBlock / StackPanel). Noesis controls
//    are drawn by a control template that comes from the game's theme; there is no
//    FrameworkElementFactory and ControlTemplate exposes no VisualTree, so a
//    template cannot be built from code. Whether the game's theme happens to
//    provide one is an empirical question - ProbeControls() answers it with
//    numbers, and /ui/state reports what the live TextBox resolved to.
//
//  * The HOLD toggle is a Border + TextBlock with a mouse handler rather than a
//    Button, so it renders whether or not any theme is in play. Clicking it and
//    POSTing /ui/hold end in the same SetHold(), so the two paths cannot drift.
//
//  * Typed characters are mirrored into a TextBlock. If the TextBox turns out to be
//    invisible here, the player can still see what was typed - the interaction is
//    not hostage to one control type.
//
//  * Hit testing: a read-only strip could be IsHitTestVisible=false. Once it
//    carries a toggle it cannot be, because hit testing stops at a parent that
//    refuses it. The strip accepts clicks inside its own rectangle only.
//
//  * Tree traversal is reflection on Children/Child/Content: a wrong guess about
//    Noesis's hierarchy degrades to "not found" instead of a compile error.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAgentBridge
{
    public static class UiBridge
    {
        public const string PanelName = "DshAgentPanel";
        public const string HoldName = "DshHoldToggle";
        public const string InputName = "DshInputBox";
        public const string SendName = "DshSubmitButton";
        public const string ReleaseName = "DshReleaseButton";

        private static Border _panel;
        private static TextBlock _text;
        private static TextBlock _echo;
        private static TextBlock _holdText;
        private static Border _releaseButton;
        private static TextBlock _releaseText;
        private static Border _sendButton;
        private static TextBlock _sendText;
        private static Border _holdButton;
        private static Border _holdDot;
        private static StackPanel _holdRow;
        private static TextBlock _holdDotText;
        private static TextBox _input;
        private static Panel _host;

        // HOLD is not stored here any more: it is CommsBridge state, so hiding, removing
        // or rebuilding this panel cannot clear it. The dot only paints it. The last
        // painted value lets a hold set over HTTP show up without a click here.
        private static bool _paintedHold;

        // Typed text is buffered here and mirrored to _echo. Reading it back from
        // the HTTP side is what makes "the player typed this" observable.
        private static string _pending = "";
        private static string _lastKey = "";
        // Every key either listener saw, newest last, capped. This exists so the next
        // live run answers "did the event arrive at all" with a reading instead of a
        // theory -- the Enter failure cost a round trip precisely because we had none.
        private static readonly List<string> _keyLog = new List<string>();
        private static string _keyMode = "not-wired";
        private static string _rootKeyMode = "not-wired";
        private static bool _typingElementOk;
        // Instrumentation for the lock itself. The Enter/caret round trips cost
        // three deploys because there was no way to read back "was SetTyping
        // even called, by whom, and did the field take".
        private static int _typingCalls;
        private static string _lastTypingCall = "";
        private static bool _typingActiveField;
        private static string _boxClickMode = "not-wired";
        // The root key listener used to be attached only by Install(). The inspector
        // window is opened by a key press, so a listener that waits for an install can
        // never open it: nothing is installed, so no key arrives, so nothing installs.
        private static bool _rootWired;
        // Measured on build 151552: the focus properties report false while the
        // box really owns the caret, so they cannot arm the lock. Kept as a flag
        // rather than deleted so a future build can be re-tested without a patch.
        private static bool _autoLockFromFocus;
        private static readonly List<string> _submitted = new List<string>();

        // The list is capped so a long session cannot grow it, but the service needs
        // a cursor it can trust. The counter therefore counts submissions since this
        // process started, not entries currently held. Conflating the two made the
        // contract impossible to honour: once the list hit 20 the counter stopped.
        private static int _submittedTotal;

        // Bumped on every Install. Without it a rebuilt panel looks exactly like
        // "the same panel with no new text", and the service would happily reconcile
        // against a cursor that means nothing any more.
        private static int _panelInstance;

        // ------------------------------------------------------------ public API

        public static bool Hold { get { return CommsBridge.Hold; } }
        private static bool _typing;
        public static bool Typing { get { return _typing; } }

        public static JObject State()
        {
            JObject o = new JObject();
            FrameworkElement root = FindTacticalRoot();
            o["installed"] = _panel != null;
            o["panelName"] = PanelName;
            o["uiHost"] = root != null ? (JToken)root.GetType().FullName : JValue.CreateNull();
            o["uiHostName"] = root != null ? (JToken)root.Name : JValue.CreateNull();
            o["mountPoint"] = _panel != null ? (JToken)"DisplayAreaGrid" : JValue.CreateNull();
            o["mountPointChildren"] = _host != null ? (JToken)_host.Children.Count : JValue.CreateNull();
            // Caret-driven lock: these three are what prove the interaction model
            // rather than describing it.
            o["inputFocused"] = InputFocused();
            o["isKeyboardFocused"] = InputFocusSelf();
            o["isKeyboardFocusWithin"] = InputFocusWithin();
            o["keyboardFocus"] = KeyboardFocusName();
            o["cursorVisible"] = _typing;
            o["releaseChord"] = "Escape";
            o["holdTogglePresent"] = _holdButton != null;
            o["holdDotPresent"] = _holdDot != null;
            o["holdDotOn"] = CommsBridge.Hold;
            o["releaseButtonPresent"] = _releaseButton != null;
            o["panelWidth"] = _panel != null ? (JToken)(double)_panel.ActualWidth : JValue.CreateNull();
            o["panelHeight"] = _panel != null ? (JToken)(double)_panel.ActualHeight : JValue.CreateNull();
            o["text"] = _text != null ? (JToken)_text.Text : JValue.CreateNull();
            o["hitTestVisible"] = _panel != null ? (JToken)_panel.IsHitTestVisible : JValue.CreateNull();
            o["hold"] = CommsBridge.Hold;
            o["holdSource"] = CommsBridge.HoldState()["holdSource"];
            o["holdLabel"] = _holdText != null ? (JToken)_holdText.Text : JValue.CreateNull();
            o["holdWidth"] = _holdButton != null ? (JToken)(double)_holdButton.ActualWidth : JValue.CreateNull();
            o["holdHeight"] = _holdButton != null ? (JToken)(double)_holdButton.ActualHeight : JValue.CreateNull();
            o["inputPresent"] = _input != null;
            o["pending"] = _pending;
            o["echo"] = _echo != null ? (JToken)_echo.Text : JValue.CreateNull();
            o["lastKey"] = _lastKey;
            o["keyLog"] = new JArray(_keyLog.ToArray());
            o["keyMode"] = _keyMode;
            o["rootKeyMode"] = _rootKeyMode;
            o["typingElementOk"] = _typingElementOk;
            o["typing"] = _typing;
            o["typingCalls"] = _typingCalls;
            o["lastTypingCall"] = _lastTypingCall;
            o["typingActiveField"] = _typingActiveField;
            o["autoLockFromFocus"] = _autoLockFromFocus;
            o["boxClickMode"] = _boxClickMode;
            o["releaseWidth"] = _releaseButton != null ? (JToken)(double)_releaseButton.ActualWidth : JValue.CreateNull();
            o["releaseLabel"] = _releaseText != null ? (JToken)_releaseText.Text : JValue.CreateNull();
            o["sendWidth"] = _sendButton != null ? (JToken)(double)_sendButton.ActualWidth : JValue.CreateNull();
            o["sendHeight"] = _sendButton != null ? (JToken)(double)_sendButton.ActualHeight : JValue.CreateNull();
            o["sendLabel"] = _sendText != null ? (JToken)_sendText.Text : JValue.CreateNull();
            o["typingOwner"] = _typingOwner;
            o["submittedCount"] = _submittedTotal;
            o["panelInstance"] = _panelInstance;
            o["submitted"] = new JArray();
            int from = _submitted.Count > 10 ? _submitted.Count - 10 : 0;
            for (int i = from; i < _submitted.Count; i++) ((JArray)o["submitted"]).Add(_submitted[i]);
            if (_input != null)
            {
                o["inputText"] = _input.Text;
                o["inputWidth"] = (double)_input.ActualWidth;
                o["inputHeight"] = (double)_input.ActualHeight;
                o["inputTemplateResolved"] = _input.Template != null;
                o["inputStyleResolved"] = _input.Style != null;
                o["inputPartContentHost"] = InputHasPart();
            }
            o["note"] = "the strip is drawn with primitives, so a non-zero "
                + "panelWidth/panelHeight is real layout evidence";
            return o;
        }

        public static JObject Install(string message)
        {
            if (_panel != null)
            {
                JObject again = State();
                again["alreadyInstalled"] = true;
                return again;
            }

            FrameworkElement root = FindTacticalRoot();
            if (root == null)
                throw new ApiException(409, "no_ui_host",
                    "no NoesisView currently shows SeapowerUI.MainGameView; install "
                    + "only works while a mission is on screen", false);

            object found = FindNamed(root, "DisplayAreaGrid", 0);
            Panel host = found as Panel;
            if (host == null)
                throw new ApiException(409, "no_mount_point",
                    "DisplayAreaGrid was not found as a Panel under the tactical view; "
                    + "nothing was inserted", false);

            Border panel = new Border();
            panel.Name = PanelName;
            panel.Background = new SolidColorBrush(Color.FromArgb(190, 8, 30, 52));
            panel.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 170, 220));
            panel.BorderThickness = new Thickness(1);
            panel.Padding = new Thickness(10);
            panel.Margin = new Thickness(24);
            panel.HorizontalAlignment = HorizontalAlignment.Left;
            panel.VerticalAlignment = VerticalAlignment.Top;

            StackPanel stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;

            TextBlock text = new TextBlock();
            text.Text = message;
            text.FontSize = 16;
            text.TextWrapping = TextWrapping.Wrap;
            text.MaxWidth = 420;
            text.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 235, 255));

            TextBox input = new TextBox();
            input.Name = InputName;
            input.Width = 300;
            input.Height = 26;
            input.FontSize = 14;
            input.Margin = new Thickness(0, 8, 0, 0);
            WireKeys(input);

            TextBlock echo = new TextBlock();
            echo.FontSize = 14;
            echo.TextWrapping = TextWrapping.Wrap;
            echo.MaxWidth = 420;
            echo.Margin = new Thickness(0, 4, 0, 0);
            echo.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 240, 190));
            echo.Text = "";   // RefreshEcho() right after _echo is assigned owns this text

            // Submit by mouse as well as by Enter. The live runs proved clicks arrive
            // (HOLD toggles), so this path does not depend on how the UI library
            // treats a key that its TextBox wants for itself.
            Border sendBtn = new Border();
            sendBtn.Name = SendName;
            sendBtn.Margin = new Thickness(0, 8, 0, 0);
            sendBtn.Padding = new Thickness(8, 4, 8, 4);
            sendBtn.BorderThickness = new Thickness(1);
            sendBtn.HorizontalAlignment = HorizontalAlignment.Left;
            sendBtn.IsHitTestVisible = true;
            sendBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 200, 255));
            sendBtn.Background = new SolidColorBrush(Color.FromArgb(200, 20, 50, 90));

            TextBlock sendLabel = new TextBlock();
            sendLabel.FontSize = 14;
            sendLabel.Text = "SUBMIT";
            sendLabel.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 235, 255));
            sendBtn.Child = sendLabel;

            try
            {
                sendBtn.MouseLeftButtonDown += new MouseButtonEventHandler(OnSendClick);
            }
            catch (Exception ex)
            {
                Core.Log("ui send handler not wired: " + ex.GetType().Name);
            }

            // The owner asked for one small control instead of two big rows: a dot that
            // shows the bridge's hold state and toggles it. Border rather than Button so
            // nothing about it depends on a control template being available.
            Border holdDot = new Border();
            holdDot.Name = "DshHoldDot";
            holdDot.Width = 12;
            holdDot.Height = 12;
            holdDot.VerticalAlignment = VerticalAlignment.Center;
            holdDot.BorderThickness = new Thickness(1);
            holdDot.IsHitTestVisible = true;

            TextBlock holdLabel = new TextBlock();
            holdLabel.FontSize = 12;
            holdLabel.Margin = new Thickness(6, 0, 0, 0);
            holdLabel.VerticalAlignment = VerticalAlignment.Center;
            holdLabel.Text = "HOLD";

            StackPanel holdRow = new StackPanel();
            holdRow.Name = "DshHoldRow";
            holdRow.Orientation = Orientation.Horizontal;
            holdRow.Margin = new Thickness(0, 0, 0, 6);
            holdRow.IsHitTestVisible = true;
            holdRow.Children.Add(holdDot);
            holdRow.Children.Add(holdLabel);
            try
            {
                holdRow.MouseLeftButtonDown += new MouseButtonEventHandler(OnHoldDotClick);
            }
            catch (Exception ex)
            {
                Core.Log("ui hold dot handler not wired: " + ex.GetType().Name);
            }

            stack.Children.Add(holdRow);
            stack.Children.Add(text);
            stack.Children.Add(input);
            stack.Children.Add(echo);
            stack.Children.Add(sendBtn);
            panel.Child = stack;

            int before = host.Children.Count;
            host.Children.Add(panel);
            try { host.UpdateLayout(); }
            catch (Exception) { }

            _panel = panel;
            _panelInstance++;
            WireRootKeys(host);
            // Tell the service that anything it reconciled before this moment belongs
            // to a previous panel: the cursor resets, nothing is replayed.
            _submittedTotal = 0;
            _submitted.Clear();
            _text = text;
            _echo = echo;
            RefreshEcho();   // one source of truth for the hint text
            // HOLD is backend state now; the strip carries no toggle.
            _holdButton = null;
            _holdText = null;
            _holdDot = holdDot;
            _holdRow = holdRow;
            _holdDotText = holdLabel;
            PaintHoldDot();
            _sendButton = sendBtn;
            _sendText = sendLabel;
            _releaseButton = null;
            _releaseText = null;
            _input = input;
            _host = host;

            // Focus so typing lands in the box without the player having to click it.
            // A failure here is not fatal: clicking the strip achieves the same thing.
            // Keyboard.Focus is an instance method bound to an element's dispatcher, so
            // the element's own Keyboard property is the way in.
            // No auto-focus. The lock follows the caret: it starts released, the
            // player clicks the box to get a caret, Escape hands the keyboard back.
            // Taking focus on install would lock the game keys without being asked.
            SetTyping(false, "panel installed");

            Core.Log("ui panel inserted into DisplayAreaGrid (" + before + " -> "
                + host.Children.Count + " children)");

            JObject o = State();
            o["inserted"] = true;
            o["mountPointChildrenBefore"] = before;
            o["interactive"] = true;
            o["hitTestNote"] = "the strip carries an input box and a submit bar, so it accepts "
                + "clicks inside its own rectangle only; the plot outside it is untouched";
            return o;
        }

        public static JObject Remove()
        {
            JObject o = new JObject();
            if (_panel == null || _host == null)
            {
                o["removed"] = false;
                o["reason"] = "not installed";
                return o;
            }

            string removedName = _panel.Name;
            int before = _host.Children.Count;
            bool ok = false;
            try
            {
                ok = _host.Children.Remove(_panel);
            }
            catch (Exception ex)
            {
                Core.Log("ui panel remove failed: " + ex.GetType().Name);
            }
            int after = _host.Children.Count;

            _panel = null;
            _text = null;
            _echo = null;
            _holdDot = null;
            _holdRow = null;
            _holdDotText = null;
            _sendButton = null;
            _sendText = null;
            _releaseButton = null;
            _releaseText = null;
            if (_typing) SetTyping(false, "panel removed");
            _holdButton = null;
            _holdText = null;
            _input = null;
            _host = null;

            o["removed"] = ok;
            o["removedName"] = removedName;
            o["childrenBefore"] = before;
            o["childrenAfter"] = after;
            o["note"] = "only the instance this bridge added was removed; the "
                + "game's own children were never touched";
            Core.Log("ui panel removed: " + ok + " (" + before + " -> " + after + ")");
            return o;
        }

        // Programmatic HOLD. The dot click and the HTTP route both end in CommsBridge, so
        // there is one field and the panel cannot drift away from the gate.
        public static JObject SetHold(bool on, string source)
        {
            JObject o = CommsBridge.SetHold(on, source);
            PaintHoldDot();
            o["label"] = _holdText != null ? (JToken)_holdText.Text : JValue.CreateNull();
            return o;
        }

        // The same buffer the keyboard writes into, so the pipeline can be exercised
        // without a keyboard. This is NOT evidence that typing works - only the
        // player's own keystrokes are.
        public static JObject TypeText(string what, string source)
        {
            // The box is the authority now, so this has to write through it. Writing
            // _pending directly would be erased by the very next Tick().
            SetBoxText(BoxText() + (what == null ? "" : what));
            SyncFromInput();
            JObject o = new JObject();
            o["pending"] = _pending;
            o["inputText"] = BoxText();
            o["source"] = source;
            o["note"] = "programmatic input; real keystrokes are the only evidence that "
                + "the keyboard path works";
            return o;
        }

        public static JObject Submit(string source)
        {
            string fromBox = _input != null ? Convert.ToString(_input.Text) : null;
            string text = (fromBox != null && fromBox.Length > 0) ? fromBox : _pending;
            text = (text ?? "").Trim();

            JObject o = new JObject();
            o["source"] = source;
            o["text"] = text;
            if (text.Length == 0)
            {
                o["submitted"] = false;
                o["reason"] = "nothing typed";
                return o;
            }

            _submitted.Add(text);
            _submittedTotal++;
            if (_submitted.Count > 20) _submitted.RemoveAt(0);
            _pending = "";
            if (_input != null)
            {
                try { _input.Text = ""; }
                catch (Exception) { }
            }
            RefreshEcho();

            o["submitted"] = true;
            o["submittedCount"] = _submittedTotal;
            o["panelInstance"] = _panelInstance;
            o["note"] = "the text reached the bridge; turning it into a proposal is T13's "
                + "job, not this panel's";
            Core.Log("ui submit (" + source + "): " + text.Length + " chars");
            return o;
        }

        // Answers one question with numbers instead of opinion: can a Noesis control
        // render here? Every child it adds is removed again before returning, and the
        // strip installed by /ui/install is never touched.
        public static JObject ProbeControls()
        {
            FrameworkElement root = FindTacticalRoot();
            if (root == null)
                throw new ApiException(409, "no_ui_host",
                    "no NoesisView currently shows SeapowerUI.MainGameView", false);
            object found = FindNamed(root, "DisplayAreaGrid", 0);
            Panel host = found as Panel;
            if (host == null)
                throw new ApiException(409, "no_mount_point",
                    "DisplayAreaGrid was not found as a Panel under the tactical view", false);

            JObject o = new JObject();
            o["mountPoint"] = "DisplayAreaGrid";
            o["productionNote"] = "every child this probe adds is removed again; the strip "
                + "installed by /ui/install is left untouched";
            o["measureNote"] = "each probe element gets an explicit Width/Height, because a "
                + "child of a Grid stretches to the grid and ActualWidth would then measure the "
                + "grid, not the control";

            TextBlock tb = new TextBlock();
            tb.Text = "probe";
            tb.FontSize = 14;
            o["textBlock"] = MeasureOne(host, tb, 0, 0);

            Border bd = new Border();
            TextBlock inner = new TextBlock();
            inner.Text = "probe";
            inner.FontSize = 14;
            bd.Child = inner;
            o["border"] = MeasureOne(host, bd, 0, 0);

            TextBox box = new TextBox();
            box.FontSize = 14;
            try { box.Text = "probe"; }
            catch (Exception) { }
            JObject boxReport = MeasureOne(host, box, 260, 26);
            boxReport["templateResolved"] = box.Template != null;
            boxReport["styleResolved"] = box.Style != null;
            ControlChecks(boxReport, box);
            o["textBox"] = boxReport;

            Button btn = new Button();
            btn.FontSize = 14;
            try { btn.Content = "probe"; }
            catch (Exception) { }
            JObject btnReport = MeasureOne(host, btn, 120, 26);
            btnReport["templateResolved"] = btn.Template != null;
            btnReport["styleResolved"] = btn.Style != null;
            ControlChecks(btnReport, btn);
            o["button"] = btnReport;

            o["conclusion"] = Conclusion(boxReport, btnReport);
            return o;
        }

        // ------------------------------------------------------------- internals

        private static JObject MeasureOne(Panel host, FrameworkElement fe,
                                          float width, float height)
        {
            JObject r = new JObject();
            r["type"] = fe.GetType().FullName;
            if (width > 0) fe.Width = width;
            if (height > 0) fe.Height = height;
            r["explicitWidth"] = width;
            r["explicitHeight"] = height;

            bool added = false;
            try
            {
                host.Children.Add(fe);
                added = true;
                host.UpdateLayout();
            }
            catch (Exception ex)
            {
                r["addError"] = ex.GetType().Name + ": " + ex.Message;
            }
            try
            {
                r["actualWidth"] = (double)fe.ActualWidth;
                r["actualHeight"] = (double)fe.ActualHeight;
                r["desiredWidth"] = (double)fe.DesiredSize.Width;
                r["desiredHeight"] = (double)fe.DesiredSize.Height;
            }
            catch (Exception)
            {
                r["actualWidth"] = JValue.CreateNull();
                r["actualHeight"] = JValue.CreateNull();
            }
            if (added)
            {
                try { host.Children.Remove(fe); }
                catch (Exception) { }
            }
            return r;
        }

        private static void ControlChecks(JObject r, Control c)
        {
            try
            {
                r["applyTemplateReturned"] = c.ApplyTemplate();
            }
            catch (Exception ex)
            {
                r["applyTemplateError"] = ex.GetType().Name + ": " + ex.Message;
            }
            try
            {
                r["partContentHost"] = c.GetTemplateChild("PART_ContentHost") != null;
            }
            catch (Exception ex)
            {
                r["partContentHostError"] = ex.GetType().Name;
            }
            try
            {
                r["logicalChildren"] = CountChildren(c);
            }
            catch (Exception) { }
        }

        private static bool InputHasPart()
        {
            if (_input == null) return false;
            try { return _input.GetTemplateChild("PART_ContentHost") != null; }
            catch (Exception) { return false; }
        }

        private static int CountChildren(object node)
        {
            IEnumerable kids = ChildrenOf(node);
            if (kids == null) return 0;
            int n = 0;
            foreach (object k in kids) { if (k != null) n++; }
            return n;
        }

        private static string Conclusion(JObject box, JObject btn)
        {
            bool boxOk = Lays(box);
            bool btnOk = Lays(btn);
            if (boxOk && btnOk)
                return "both controls got a template from the game's theme; the text box path "
                    + "is usable and typing can go straight into it";
            if (boxOk)
                return "the text box laid out but the button did not; keep using a Border for "
                    + "the toggle and a TextBox for input";
            if (btnOk)
                return "the button laid out but the text box did not; keep primitives for the "
                    + "toggle and draw the input field by hand";
            return "neither control laid out; the theme does not template them here, so build "
                + "the input field from primitives";
        }

        private static bool Lays(JObject r)
        {
            if (r == null) return false;
            double w = Num(r["actualWidth"]);
            double h = Num(r["actualHeight"]);
            bool templated = false;
            JToken t = r["templateResolved"];
            if (t != null) templated = Convert.ToBoolean(t.ToString());
            return w > 0 && h > 0 && templated;
        }

        private static double Num(JToken t)
        {
            if (t == null) return 0.0;
            try { return Convert.ToDouble(t.ToString()); }
            catch (Exception) { return 0.0; }
        }

        // The dot is the only hold control on the strip. It paints state and nothing
        // else: no focus, no typing change. Taking the keyboard here would steal the
        // caret the player just placed.
        private static void PaintHoldDot()
        {
            if (_holdDot == null) return;
            bool held = CommsBridge.Hold;
            _paintedHold = held;
            if (held)
            {
                _holdDot.Background = new SolidColorBrush(Color.FromArgb(255, 210, 60, 50));
                _holdDot.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 150, 120));
                if (_holdDotText != null)
                {
                    _holdDotText.Text = "HOLD ON  (click to release)";
                    _holdDotText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 200, 180));
                }
            }
            else
            {
                _holdDot.Background = new SolidColorBrush(Color.FromArgb(160, 60, 70, 80));
                _holdDot.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 150, 170));
                if (_holdDotText != null)
                {
                    _holdDotText.Text = "HOLD off  (click to hold)";
                    _holdDotText.Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 200, 220));
                }
            }
        }

        // Clicking the dot toggles the same bridge state POST /ui/hold sets, so the mouse
        // path and the HTTP path cannot drift. Proven-mouse only: no key handling here.
        private static void OnHoldDotClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                SetHold(!CommsBridge.Hold, "hold dot click");
            }
            catch (Exception ex)
            {
                Core.Log("ui hold dot click failed: " + ex.GetType().Name);
            }
        }

        private static void RefreshEcho()
        {
            if (_echo == null) return;
            _echo.Text = _pending.Length == 0
                ? "click the box to type; SUBMIT sends; Esc gives the keyboard back"
                : "typed: " + _pending + "    [click SUBMIT]";
        }

        // The TextBox is the authority for what has been typed, so the bridge mirrors it
        // instead of collecting characters itself. Called from the main-thread Update the
        // bridge already runs; a string compare is the whole cost.
        //
        // Why not a TextInput handler: in the owner's live run the box showed
        // "hello world" while /ui/state still reported an empty buffer -- the control had
        // consumed the text. A mirror cannot desync from the control, a buffer can.
        public static void Tick()
        {
            // Before the panel guard on purpose: the key listener has to exist for the
            // inspector window to be openable at all.
            try { EnsureRootKeys(); }
            catch (Exception ex) { Core.Log("ui root keys failed: " + ex.GetType().Name); }
            if (_input == null) return;
            try { SyncFromInput(); }
            catch (Exception ex) { Core.Log("ui tick failed: " + ex.GetType().Name); }
            try { SyncTypingWithFocus(); }
            catch (Exception ex) { Core.Log("ui typing sync failed: " + ex.GetType().Name); }
            if (_paintedHold != CommsBridge.Hold)
            {
                try { PaintHoldDot(); }
                catch (Exception ex) { Core.Log("ui hold repaint failed: " + ex.GetType().Name); }
            }
        }

        /// <summary>
        /// Attach the preview key listener to the tactical UI root as soon as that root
        /// exists. Preview events tunnel from the root towards the focused element, so a
        /// listener on the root sees the key no matter which part of the tactical screen
        /// currently holds focus. A listener on DisplayAreaGrid would only see keys while
        /// focus is inside that grid, which is why the toggle looked dead.
        /// </summary>
        internal static void EnsureRootKeys()
        {
            if (_rootWired) return;
            FrameworkElement root = FindTacticalRoot();
            if (root == null) return;
            WireRootKeys(root);
            Core.Log("ui root key listener attached (" + root.GetType().FullName + ")");
        }

        private static string BoxText()
        {
            if (_input == null) return _pending;
            string t = _input.Text;
            return t == null ? "" : t;
        }

        private static void SetBoxText(string value)
        {
            if (_input == null) return;
            _input.Text = value == null ? "" : value;
        }

        private static void SyncFromInput()
        {
            string t = BoxText();
            if (t == _pending) return;
            _pending = t;
            RefreshEcho();
        }

        // ---------------------------------------------------------------- typing lock
        // The game already has the mechanism we need: SeaPower.InputHandler carries
        // `typingActive` and `typingElement`, and its own console input box must drive
        // them. Claiming the same flag is the difference between "the panel fights the
        // keybindings" and "the panel is a text field". Everything here is reflective
        // and optional: if the type or a field is missing, the panel still works and
        // simply does not get the keyboard.
        private static UnityEngine.Object _handler;

        // Local copy of the type lookup: UiBridge must not depend on Api internals.
        private static Type FindType(string full)
        {
            if (string.IsNullOrEmpty(full)) return null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < asms.Length; a++)
            {
                try
                {
                    Type ty = asms[a].GetType(full, false);
                    if (ty != null) return ty;
                }
                catch (Exception) { }
            }
            return null;
        }
        private static string _typingOwner = "";

        private static void PushKey(string what)
        {
            try
            {
                _keyLog.Add(what);
                if (_keyLog.Count > 8) _keyLog.RemoveAt(0);
            }
            catch (Exception) { }
        }

        // A TextBox keeps the keyboard on its template child, so the box itself
        // reports IsKeyboardFocused=false even while a caret is blinking in it and
        // its own key handler is running. Either flag means "the player is typing".
        private static bool InputFocused()
        {
            try
            {
                if (_input == null) return false;
                if (_input.IsKeyboardFocused) return true;
                return _input.IsKeyboardFocusWithin;
            }
            catch (Exception) { return false; }
        }

        private static bool InputFocusWithin()
        {
            try { return _input != null && _input.IsKeyboardFocusWithin; }
            catch (Exception) { return false; }
        }

        private static bool InputFocusSelf()
        {
            try { return _input != null && _input.IsKeyboardFocused; }
            catch (Exception) { return false; }
        }

        // Enter is handled by the TextBox's own class handler before a bubbling
        // listener runs, and the live run proved a plain instance PreviewKeyDown
        // handler is skipped as well. handledEventsToo=true is the only form that is
        // still invoked once an event is marked handled.
        private static void WireKeys(TextBox input)
        {
            try
            {
                // This build has no handledEventsToo overload, so a handler here only
                // runs for keys the TextBox itself does not consume (Escape, letters).
                // Enter is consumed by its class handler and is therefore taken care of
                // by the root listener instead -- see WireRootKeys.
                input.PreviewKeyDown += new KeyEventHandler(OnKeyDown);
                _keyMode = "preview on box";
                // Preview, not bubbling: the TextBox class handler consumes the
                // bubbling MouseLeftButtonDown while it positions the caret, so a
                // plain listener on the element is never called (measured -- the
                // SUBMIT strip received its click, the box never did).
                input.PreviewMouseLeftButtonDown +=
                    new MouseButtonEventHandler(OnBoxClick);
                _boxClickMode = "preview click on box";
            }
            catch (Exception ex)
            {
                _keyMode = "preview on box failed: " + ex.GetType().Name;
                Core.Log("ui input handlers not wired: " + _keyMode);
            }
        }

        // Clicking the box is what locks the keyboard. The live run proved the focus
        // properties report false even while the box visibly owns the caret, so focus
        // cannot be the trigger; the click is the intent, so the click is the event.
        // The TextBox still positions the caret itself: this handler does not mark
        // the event handled, it only borrows the keyboard from the game.
        private static void OnBoxClick(object sender, MouseButtonEventArgs e)
        {
            Core.Log("ui box click (lock request)");
            SetTyping(true, "box click");
        }

        // Runs before the box's own click handler. Only a lock this click path armed
        // is released here, so an explicit /ui/typing lock is not dropped by a stray
        // click on the map.
        // Reads a property without a compile-time dependency on the type. A wrong name has
        // to degrade to null, never to a crash: this runs on every host click.
        private static object UiProp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                System.Reflection.PropertyInfo pi = o.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                return pi == null ? null : pi.GetValue(o, null);
            }
            catch (Exception) { return null; }
        }

        // Walks the routed event's source up to whoever was clicked. Bounded depth: a wrong
        // guess must stop, not spin.
        private static bool IsWithinHoldControl(MouseButtonEventArgs e)
        {
            if (_holdRow == null && _holdDot == null) return false;
            object node = UiProp(e, "OriginalSource");
            if (node == null) node = UiProp(e, "Source");
            for (int i = 0; i < 16 && node != null; i++)
            {
                if (object.ReferenceEquals(node, _holdRow)) return true;
                if (object.ReferenceEquals(node, _holdDot)) return true;
                object up = UiProp(node, "Parent");
                if (up == null) up = UiProp(node, "VisualParent");
                if (up == null) up = UiProp(node, "TemplatedParent");
                node = up;
            }
            return false;
        }

        private static void OnHostClick(object sender, MouseButtonEventArgs e)
        {
            if (!_typing) return;
            if (_typingOwner != "box click") return;
            // B33: the hold dot is a control, not the map. Clicking it used to release the
            // keyboard, which dropped the caret the player had just placed -- punishing the
            // player for using the panel. Toggling hold must not cost the caret.
            if (IsWithinHoldControl(e))
            {
                Core.Log("ui host click on the hold row (lock kept)");
                return;
            }
            ClearKeyboardFocus();
            SetTyping(false, "click outside the box");
        }

        // Second opinion at the root of the tactical view. Preview events tunnel from
        // the root down, so this fires before any descendant can consume them. If this
        // listener is silent too, the honest conclusion is "Noesis is not routing keys
        // to this view", and the keyboard path should be dropped rather than retried.
        private static void WireRootKeys(FrameworkElement host)
        {
            if (host == null) return;
            if (_rootWired) return;
            _rootWired = true;
            try
            {
                // Preview tunnels root -> target, so this runs before the TextBox class
                // handler that swallows Enter. This is the only place Enter is turned
                // into a submit; keeping it in one place is what prevents a double
                // submit if the box listener ever starts seeing Enter too.
                host.PreviewKeyDown += new KeyEventHandler(OnRootKeyDown);
                // Clicking anywhere outside the box gives the keyboard back. Preview
                // mouse events tunnel from the root first, so a click that lands on the
                // box also passes through here -- it releases, and then the box's own
                // click handler re-arms the lock in the same dispatch. Net effect is
                // what the owner found by hand: click the box to lock, click away to
                // release, no key combination needed to get out.
                try
                {
                    host.PreviewMouseLeftButtonDown +=
                        new MouseButtonEventHandler(OnHostClick);
                    _boxClickMode = _boxClickMode + "; click outside releases";
                }
                catch (Exception ex2)
                {
                    _boxClickMode = "mouse click on box (host release unavailable: "
                        + ex2.GetType().Name + ")";
                }
                _rootKeyMode = "preview on host";
            }
            catch (Exception ex)
            {
                _rootKeyMode = "preview on host failed: " + ex.GetType().Name;
            }
        }

        private static void OnRootKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e == null) return;
                string name = e.Key.ToString();
                PushKey("root:" + name);
                _lastKey = "root:" + name;

                // The inspector's toggle key is handled before the panel keys: it is a
                // window action, not a typing action, and HandleKey refuses while the
                // player is locked into the text box.
                InspectorWindow.HandleKey(name);
                // TR2-H: Enter in the comms box is a submit for that page. The page keeps
                // its own armed flag, so a focused inspector box keeps its Enter too.
                if (CommsWindow.HandleKey(name)) return;

                if (name == "Escape")
                {
                    // Escape hands the keyboard back, but only while the lock is
                    // actually on: when the panel is not typing, this returns and
                    // the game's pause menu keeps working exactly as before. The
                    // lock is what makes the collision harmless, because a locked
                    // game ignores its own keybindings anyway. Clearing focus is
                    // what makes the caret disappear -- without it the next tick
                    // re-locks and Escape looks dead.
                    if (!_typing) return;
                    ClearKeyboardFocus();
                    SetTyping(false, "escape at root");
                    return;
                }
                // Noesis spells it Return (Enter is an alias), and ToString()
                // yields the primary name. Comparing only against "Enter" is
                // exactly why Enter looked dead while root:Return was in keyLog.
                if (name == "Enter" || name == "Return")
                {
                    if (!_typing && !InputFocused()) return;
                    Submit("enter key (root)");
                }
            }
            catch (Exception ex)
            {
                Core.Log("ui root key failed: " + ex.GetType().Name);
            }
        }

        private static UnityEngine.Object Handler()
        {
            if (_handler != null) return _handler;
            try
            {
                Type ty = FindType("SeaPower.InputHandler");
                if (ty == null) return null;
                _handler = UnityEngine.Object.FindObjectOfType(ty);
            }
            catch (Exception ex)
            {
                Core.Log("ui input handler not found: " + ex.GetType().Name);
            }
            return _handler;
        }

        private static bool SetMemberOn(object target, string name, object value)
        {
            if (target == null) return false;
            try
            {
                Type ty = target.GetType();
                FieldInfo fi = null;
                while (ty != null && fi == null)
                {
                    fi = ty.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    ty = ty.BaseType;
                }
                if (fi == null)
                {
                    Core.Log("ui set " + name + " failed: no such field");
                    return false;
                }
                if (value == null)
                {
                    fi.SetValue(target, null);
                    return true;
                }
                // Convert.ChangeType only understands IConvertible. A UIElement field
                // (typingElement) needs a plain assignment -- ChangeType threw
                // InvalidCastException here on every focus change, which is why the
                // game never learned which element owned the keyboard.
                if (fi.FieldType.IsInstanceOfType(value))
                {
                    fi.SetValue(target, value);
                    return true;
                }
                fi.SetValue(target, Convert.ChangeType(value, fi.FieldType));
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("ui set " + name + " failed: " + ex.GetType().Name
                         + " (field " + NameOfFieldType(target, name) + ")");
                return false;
            }
        }

        private static string NameOfFieldType(object target, string name)
        {
            try
            {
                Type ty = target.GetType();
                FieldInfo fi = null;
                while (ty != null && fi == null)
                {
                    fi = ty.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    ty = ty.BaseType;
                }
                return fi == null ? "?" : fi.FieldType.FullName;
            }
            catch (Exception) { return "?"; }
        }

        private static object GetMemberOn(object target, string name)
        {
            if (target == null) return null;
            try
            {
                Type ty = target.GetType();
                FieldInfo fi = null;
                while (ty != null && fi == null)
                {
                    fi = ty.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    ty = ty.BaseType;
                }
                return fi == null ? null : fi.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Claim or release the game's typing lock. `owner` is recorded so the state
        // endpoint can show who took the keyboard and whether it was released.
        /// <summary>
        /// Hand the keyboard back from outside Noesis' event routing. The frame poll uses
        /// this: once the shell owns the window the root tunnel does not reliably see the
        /// click that landed on the frame, so "click outside" and Escape are polled too.
        /// </summary>
        public static void ReleaseTypingNow(string why)
        {
            if (!_typing) return;
            ClearKeyboardFocus();
            SetTyping(false, why);
        }

        public static JObject SetTyping(bool on, string owner)
        {
            JObject o = new JObject();
            o["ok"] = true;
            o["want"] = on;
            o["owner"] = owner;
            _typingCalls++;
            _lastTypingCall = (on ? "on:" : "off:") + owner;
            UnityEngine.Object h = Handler();
            if (h == null)
            {
                o["ok"] = false;
                o["reason"] = "SeaPower.InputHandler not found";
                return o;
            }
            if (on)
            {
                _typingElementOk = SetMemberOn(h, "typingElement", _input);
                SetMemberOn(h, "typingActive", true);
                _typing = true;
                _typingOwner = owner;
                if (_input != null)
                {
                    try
                    {
                        Keyboard kb = _input.Keyboard;
                        if (kb != null) kb.Focus(_input);
                    }
                    catch (Exception) { }
                }
            }
            else
            {
                SetMemberOn(h, "typingActive", false);
                SetMemberOn(h, "typingElement", null);
                _typing = false;
                _typingOwner = "";
            }
            object now = GetMemberOn(h, "typingActive");
            _typingActiveField = now != null && Convert.ToBoolean(now);
            o["typingActive"] = now == null ? JValue.CreateNull() : (JToken)Convert.ToBoolean(now);
            o["note"] = "the game stops reading its keybindings while typingActive is set, "
                + "so plain Enter and Backspace reach the box; Escape releases it";
            Core.Log("ui typing " + (on ? "on" : "off") + " (" + owner + ")");
            return o;
        }

        // Keep the lock in step with who actually has keyboard focus: gaining focus
        // claims it, losing focus (Escape, a click elsewhere, the panel closing)
        // releases it. This is why no key combination is needed any more.
        // Hands the keyboard back to the game. Noesis' Keyboard.ClearFocus is an
        // instance method bound to the element's dispatcher, the same shape as Focus.
        private static void ClearKeyboardFocus()
        {
            try
            {
                if (_input == null) return;
                Keyboard kb = _input.Keyboard;
                if (kb == null) return;
                kb.ClearFocus();
                Core.Log("ui keyboard released (caret gone)");
            }
            catch (Exception ex)
            {
                Core.Log("ui keyboard release failed: " + ex.GetType().Name);
            }
        }

        private static string KeyboardFocusName()
        {
            try
            {
                if (_input == null) return null;
                Keyboard kb = _input.Keyboard;
                if (kb == null) return null;
                UIElement el = kb.FocusedElement;
                if (el == null) return null;
                // Name lives on FrameworkElement, not UIElement; report the element
                // type when a bare content host holds focus.
                FrameworkElement fe = el as FrameworkElement;
                string n = fe != null ? fe.Name : null;
                if (n == null || n.Length == 0) n = el.GetType().Name;
                return n;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SyncTypingWithFocus()
        {
            if (_input == null) return;
            bool focused = false;
            try
            {
                object v = GetMember(_input, "IsKeyboardFocused");
                if (v == null) v = GetMember(_input, "IsFocused");
                focused = v != null && Convert.ToBoolean(v);
            }
            catch (Exception)
            {
                return;
            }
            // Focus is not a usable signal in this build (see _autoLockFromFocus),
            // so arming the lock from it stays off. Releasing an auto-locked session
            // is kept: if a future build does report focus, this is already correct.
            if (focused && !_typing && _autoLockFromFocus) SetTyping(true, "focus");
            else if (!focused && _typing && _typingOwner == "focus") SetTyping(false, "focus lost");
        }

        // Only keys that belong to the field are ours, and only while it owns the
        // keyboard. The live run proved plain Backspace also toggles the game UI and
        // plain Enter is the game's screenshot key -- that is exactly what the typing
        // lock above removes, so no modifier is needed here.
        //
        // Key names are compared as strings on purpose: the bridge must not fail to
        // compile because a Key enum member is spelled differently.
        private static void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e == null) return;
                string name = e.Key.ToString();
                _lastKey = "key:" + name;
                PushKey("box:" + name);

                if (name == "Escape")
                {
                    // The root listener owns the Alt+Esc chord; releasing twice would
                    // make the log ambiguous about which listener acted.
                    return;
                }
                // Enter is deliberately absent: the TextBox class handler consumes it
                // before this listener runs (proven: Escape lands here, Enter never
                // does). Submitting from two places would risk a double submit, so the
                // root listener owns Enter and this one owns the rest.
                if (!_typing) return;
                // B31: never edit the text from a key handler. Writing _input.Text moves
                // the caret to offset 0, so the owner's Backspace removed the right
                // character and then threw the caret to the front of the box. The TextBox
                // class handler already edits and owns the caret; mirroring is Tick()'s
                // job. This listener only logs and (at the root) submits or releases.
            }
            catch (Exception ex)
            {
                Core.Log("ui key down failed: " + ex.GetType().Name);
            }
        }

        // The click path is the real interaction proof: no HTTP is involved, so it
        // also proves HOLD does not depend on the agent being alive.
        // Clicking SUBMIT runs the same Submit() the Enter key runs, so the two paths
        // cannot drift.
        private static void OnSendClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Submit("panel click");
            }
            catch (Exception ex)
            {
                Core.Log("ui send click failed: " + ex.GetType().Name);
            }
        }

        // The inspector window needs the same two lookups the panel does. Exposed as
        // narrow internal accessors instead of copying the reflection: two copies of
        // "find the tactical root" would drift the moment one of them is fixed.
        internal static FrameworkElement TacticalRootForInspector()
        {
            return FindTacticalRoot();
        }

        internal static object FindNamedForInspector(object node, string name)
        {
            return FindNamed(node, name, 0);
        }

        // The map work needs an element by type, not by name: the map control is not
        // given a Name in XAML. This walks the same links as FindNamed, so the two
        // traversals cannot drift apart.
        internal static object FindTypeForInspector(object node, string fullTypeName)
        {
            return FindByType(node, fullTypeName, 0);
        }

        private static object FindByType(object node, string fullTypeName, int depth)
        {
            if (node == null || depth > 48) return null;
            Type t = node.GetType();
            if (t.FullName == fullTypeName) return node;

            IEnumerable kids = ChildrenOf(node);
            if (kids == null) return null;
            foreach (object kid in kids)
            {
                object hit = FindByType(kid, fullTypeName, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        private static FrameworkElement FindTacticalRoot()
        {
            UnityEngine.Object[] all;
            try
            {
                all = UnityEngine.Object.FindObjectsOfType(typeof(NoesisView));
            }
            catch (Exception)
            {
                return null;
            }
            if (all == null) return null;

            for (int i = 0; i < all.Length; i++)
            {
                object view = GetMember(all[i], "_uiView");
                if (view == null) continue;
                FrameworkElement fe = GetMember(view, "Content") as FrameworkElement;
                if (fe == null) continue;
                if (fe.GetType().FullName == "SeapowerUI.MainGameView") return fe;
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance);
                if (f != null)
                {
                    try { return f.GetValue(target); }
                    catch (Exception) { return null; }
                }
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance);
                if (p != null && p.CanRead)
                {
                    try { return p.GetValue(target, null); }
                    catch (Exception) { return null; }
                }
                t = t.BaseType;
            }
            return null;
        }

        private static object FindNamed(object node, string name, int depth)
        {
            if (node == null || depth > 40) return null;

            FrameworkElement fe = node as FrameworkElement;
            if (fe != null && fe.Name == name && node is Panel) return node;

            IEnumerable kids = ChildrenOf(node);
            if (kids == null) return null;
            foreach (object kid in kids)
            {
                object hit = FindNamed(kid, name, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        // Only the links are needed, so read them by name: Panel.Children,
        // Decorator.Child, ContentControl/ContentPresenter.Content.
        private static IEnumerable ChildrenOf(object node)
        {
            if (node == null) return null;

            object kids = GetMember(node, "Children");
            IEnumerable many = kids as IEnumerable;
            if (many != null) return many;

            object single = GetMember(node, "Child");
            if (single == null) single = GetMember(node, "Content");
            if (single == null) return null;
            return new object[] { single };
        }
    }
}
