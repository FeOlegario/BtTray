using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>
    /// Borderless flyout shown above the tray icon. Everything is owner-drawn to stay light.
    /// This class is the frame: window, placement, footer, switching views and routing input. Each screen is a
    /// PanelView (DevicesView, SettingsView, DiscoverView) that paints itself and lists its Targets; the same target
    /// list drives hover, clicks, tooltips, keyboard navigation and the screen reader (PanelAccessibility.cs).
    /// The actions themselves (connect, pair, …) are done by TrayApp through the events below.
    /// </summary>
    class PopupForm : Form
    {
        public enum View { Devices, Settings, Discover }

        public event Action<BtDevice> ToggleRequested;
        public event Action RefreshRequested;
        public event Action DeviceChanged;
        public event Action<BtDevice> PinRequested;
        public event Action DiscoverOpened, DiscoverClosed, RescanRequested;
        public event Action<FoundDevice> PairRequested;
        public event Action<BtDevice, string> RenameRequested;

        /// <summary>Addresses of the devices pinned to the taskbar (owned by TrayApp).</summary>
        public readonly HashSet<string> Pinned = new HashSet<string>();

        public DateTime LastHidden = DateTime.MinValue;

        // data shown by the device list (owned by TrayApp)
        internal List<BtDevice> Devices = new List<BtDevice>();
        internal Dictionary<string, string> BusyText = new Dictionary<string, string>();   // address -> "Conectando…"
        internal Dictionary<string, string> Errors = new Dictionary<string, string>();     // address -> message

        // layout and style shared by the views
        Theme theme = Theme.Current();
        float s = 1f;
        internal int W, HeaderH, RowH, FooterH, Pad, OptH, SectionH, MaxRows = 6;
        internal Font FTitle, FName, FStatus, FButton, FGlyph, FGlyphSmall, FGlyphTiny, FSection;
        internal Theme Th { get { return theme; } }

        readonly DevicesView devicesView;
        readonly SettingsView settingsView;
        readonly DiscoverView discoverView;
        View view = View.Devices;
        PanelView current;

        // targets, hover and keyboard focus
        List<Target> targets = new List<Target>();
        bool targetsDirty = true;
        string hotKey, focusKey;
        int hotRow = -1;
        bool focusVisible;          // focus rectangle only after the keyboard is used, like Windows
        readonly ToolTip tip = new ToolTip();
        string tipText;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            Text = "Bluetooth";
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            W = Sc(384); HeaderH = Sc(48); RowH = Sc(60); FooterH = Sc(44); Pad = Sc(6);
            OptH = Sc(44); SectionH = Sc(30);

            FTitle = new Font("Segoe UI Semibold", 11.5f);
            FName = new Font("Segoe UI", 10f);
            FStatus = new Font("Segoe UI", 8.5f);
            FButton = new Font("Segoe UI", 9f);
            FGlyph = new Font(Theme.IconFont, 15f);
            FGlyphSmall = new Font(Theme.IconFont, 11f);
            FGlyphTiny = new Font(Theme.IconFont, 9.5f);
            FSection = new Font("Segoe UI Semibold", 9f);
            BackColor = theme.Back;

            devicesView = new DevicesView(this);
            settingsView = new SettingsView(this);
            discoverView = new DiscoverView(this);
            current = devicesView;
            UpdateSize();
        }

        internal int Sc(float v) { return (int)Math.Round(v * s); }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219 && DeviceChanged != null) DeviceChanged();   // WM_DEVICECHANGE
            base.WndProc(ref m);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
        }

        void ApplyDwm()
        {
            try
            {
                int round = 2;                                     // DWMWCP_ROUND (Windows 11)
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int border = ColorTranslator.ToWin32(theme.Border);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);  // DWMWA_BORDER_COLOR
                int dark = theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }

        // ---------------------------------------------------------------- public API (TrayApp)
        public void SetDevices(List<BtDevice> list, Dictionary<string, string> busy, Dictionary<string, string> errs)
        {
            Devices = list;
            BusyText = busy;
            Errors = errs;
            UpdateSize();
            devicesView.DataChanged();
            Invalidate();
        }

        /// <summary>Results of the search for new devices.</summary>
        public void SetFound(List<FoundDevice> list, bool isSearching)
        {
            discoverView.SetFound(list, isSearching);
            if (view == View.Discover) UpdateSize();
            Invalidate();
        }

        /// <summary>Id of the device being paired (its button shows "Please wait…"), or null.</summary>
        public void SetPairing(string id) { discoverView.SetPairing(id); Invalidate(); }

        /// <summary>Message shown in the status line of the search view, or null.</summary>
        public void SetDiscoverError(string message) { discoverView.SetError(message); Invalidate(); }

        /// <summary>Switches between the device list and the settings view.</summary>
        public void SetSettingsView(bool on) { SetView(on ? View.Settings : View.Devices); }

        public View CurrentView { get { return view; } }

        public void SetView(View v)
        {
            if (view == v) return;
            current.Leave(false);
            View old = view;
            view = v;
            current = v == View.Settings ? (PanelView)settingsView : v == View.Discover ? (PanelView)discoverView : devicesView;
            current.Enter();
            hotKey = null; hotRow = -1;
            focusKey = null;
            Cursor = Cursors.Default;
            SetTip(null);
            UpdateSize();
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            // opened with the keyboard: keep going with the keyboard from the first item of the new view
            if (focusVisible) MoveFocus(1);
            if (old == View.Discover && DiscoverClosed != null) DiscoverClosed();
            if (v == View.Discover && DiscoverOpened != null) DiscoverOpened();
        }

        /// <summary>Turns the device's name into a text box (also used by the preview tool).</summary>
        public void BeginRename(BtDevice d)
        {
            if (view != View.Devices) return;
            devicesView.BeginRename(d);
        }

        public void ShowNearTray()
        {
            Theme t = Theme.Current();
            if (t.Dark != theme.Dark) { theme = t; BackColor = t.Back; if (IsHandleCreated) ApplyDwm(); }
            current.Leave(false);
            focusVisible = false;
            SetView(View.Devices);
            devicesView.Enter();
            hotKey = null; hotRow = -1; focusKey = null;
            PlaceNearTray();
            Show();
            Activate();
        }

        void UpdateSize()
        {
            int h = HeaderH + current.ContentHeight + Pad * 2 + FooterH;
            if (Width != W || Height != h)
            {
                bool vis = Visible;
                Size = new Size(W, h);
                if (vis) PlaceNearTray();
            }
            current.ScrollBy(0);   // keep the scroll valid for the new height
        }

        void PlaceNearTray()
        {
            Point c = Cursor.Position;
            Screen scr = Screen.FromPoint(c);
            Rectangle wa = scr.WorkingArea, b = scr.Bounds;
            int m = Sc(12);
            int x, y;
            if (wa.Bottom < b.Bottom || (wa.Top == b.Top && wa.Left == b.Left && wa.Right == b.Right))  // taskbar at bottom
            { x = wa.Right - Width - m; y = wa.Bottom - Height - m; }
            else if (wa.Top > b.Top)                       // top
            { x = wa.Right - Width - m; y = wa.Top + m; }
            else if (wa.Left > b.Left)                     // left
            { x = wa.Left + m; y = wa.Bottom - Height - m; }
            else                                           // right
            { x = wa.Right - Width - m; y = wa.Bottom - Height - m; }
            Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            current.Leave(true);   // clicking outside keeps a name being typed, like renaming a file in Explorer
            LastHidden = DateTime.Now;
            Hide();
        }

        // ---------------------------------------------------------------- events raised by the views
        internal void RaiseRefresh() { if (RefreshRequested != null) RefreshRequested(); }
        internal void RaisePin(BtDevice d) { if (PinRequested != null) PinRequested(d); }
        internal void RaiseRescan() { if (RescanRequested != null) RescanRequested(); }
        internal void RaisePair(FoundDevice f) { if (PairRequested != null) PairRequested(f); }
        internal void RaiseRename(BtDevice d, string typed) { if (RenameRequested != null) RenameRequested(d, typed); }

        internal void RaiseToggle(BtDevice d)
        {
            if (d.CanToggle && !BusyText.ContainsKey(d.AddressHex) && ToggleRequested != null) ToggleRequested(d);
        }

        /// <summary>After the rename box closes with Enter/Esc, the keyboard goes back to the panel.</summary>
        internal void FocusAfterRename()
        {
            Focus();
            if (focusVisible) Invalidate();
        }

        // ---------------------------------------------------------------- geometry shared by the views
        internal Rectangle ListRect { get { return new Rectangle(0, HeaderH + Pad, W, Height - HeaderH - FooterH - Pad * 2); } }

        internal Rectangle ButtonRect(Rectangle row)
        {
            int bw = Sc(108), bh = Sc(32);
            return new Rectangle(row.Right - bw - Sc(10), row.Top + (row.Height - bh) / 2, bw, bh);
        }

        internal Rectangle RefreshRect { get { int z = Sc(34); return new Rectangle(W - z - Sc(10), (HeaderH - z) / 2 + Sc(2), z, z); } }
        internal Rectangle GearRect { get { Rectangle r = RefreshRect; return new Rectangle(r.Left - r.Width - Sc(2), r.Top, r.Width, r.Height); } }
        internal Rectangle AddRect { get { Rectangle r = GearRect; return new Rectangle(r.Left - r.Width - Sc(2), r.Top, r.Width, r.Height); } }
        internal Rectangle BackRect { get { int z = Sc(34); return new Rectangle(Sc(8), (HeaderH - z) / 2 + Sc(2), z, z); } }

        static string BtSettingsText { get { return L.T("Configurações de Bluetooth", "Bluetooth settings"); } }

        // footer link to the Windows Bluetooth settings
        Rectangle FooterLinkRect
        {
            get
            {
                int top = Height - FooterH;
                Size sz = TextRenderer.MeasureText(BtSettingsText, FStatus);
                return new Rectangle(Sc(10), top + Sc(7), sz.Width + Sc(40), FooterH - Sc(14));
            }
        }

        internal Color ErrorColor { get { return Color.FromArgb(theme.Dark ? 255 : 196, theme.Dark ? 153 : 43, theme.Dark ? 164 : 28); } }

        // ---------------------------------------------------------------- targets
        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            targetsDirty = true;    // anything that repaints may have moved or changed a target
            base.OnInvalidated(e);
        }

        /// <summary>Targets of the current view plus the footer link, in keyboard order.</summary>
        internal List<Target> Targets
        {
            get
            {
                if (targetsDirty)
                {
                    targetsDirty = false;
                    var list = new List<Target>();
                    current.BuildTargets(list);
                    list.Add(new Target
                    {
                        Key = "btsettings", Bounds = FooterLinkRect, Role = AccessibleRole.Link, Name = BtSettingsText,
                        Invoke = delegate { Program.OpenBluetoothSettings(); Hide(); }
                    });
                    targets = list;
                }
                return targets;
            }
        }

        internal Target HeaderButton(string key, Rectangle bounds, string name, string tipText, Action invoke)
        {
            return new Target { Key = key, Bounds = bounds, Name = name, Tip = tipText ?? name, Invoke = invoke };
        }

        internal string AccessibleTitle { get { return current.Title; } }

        internal int HitIndex(Point p)
        {
            List<Target> t = Targets;
            for (int i = 0; i < t.Count; i++)
                if (t[i].Focusable && t[i].VisibleAt(p)) return i;
            return -1;
        }

        internal int FocusIndex { get { return focusKey == null ? -1 : Targets.FindIndex(x => x.Key == focusKey); } }

        internal bool IsHot(string key) { return key != null && key == hotKey; }

        internal bool IsFocusShown(string key) { return focusVisible && key != null && key == focusKey; }

        /// <summary>Row highlight: under the mouse, or holding the keyboard focus.</summary>
        internal bool RowIsHot(int row)
        {
            if (row == hotRow) return true;
            if (!focusVisible) return false;
            int i = FocusIndex;
            return i >= 0 && Targets[i].Row == row;
        }

        /// <summary>Runs a target's action (click, Enter/Space, or a screen reader's default action).</summary>
        internal void InvokeTarget(int index)
        {
            List<Target> t = Targets;
            if (index < 0 || index >= t.Count || !t[index].Focusable || !t[index].Enabled) return;
            focusKey = t[index].Key;
            t[index].Invoke();
            Invalidate();
        }

        /// <summary>Gives the keyboard focus to a target (keyboard navigation or a screen reader).</summary>
        internal void FocusTarget(int index, bool visible)
        {
            List<Target> t = Targets;
            if (index < 0 || index >= t.Count || !t[index].Focusable) return;
            focusKey = t[index].Key;
            focusVisible = visible;
            EnsureVisible(t[index]);
            Invalidate();
            NotifyFocus();
        }

        // Scrolls so that the whole row of the target is inside its scrolling area.
        void EnsureVisible(Target t)
        {
            if (t.Clip.IsEmpty) return;
            int pad = Sc(14);   // a button sits 14 px inside its 60 px row
            int dy = 0;
            if (t.Bounds.Top - pad < t.Clip.Top) dy = t.Bounds.Top - pad - t.Clip.Top;
            else if (t.Bounds.Bottom + pad > t.Clip.Bottom) dy = t.Bounds.Bottom + pad - t.Clip.Bottom;
            if (dy != 0 && current.ScrollBy(dy)) targetsDirty = true;
        }

        void MoveFocus(int dir)
        {
            List<Target> t = Targets;
            int n = t.Count;
            if (n == 0) return;
            int start = FocusIndex;
            int i = start < 0 ? (dir > 0 ? -1 : n) : start;
            for (int k = 0; k < n; k++)
            {
                i = ((i + dir) % n + n) % n;
                if (t[i].Focusable) { FocusTarget(i, true); return; }
            }
        }

        void NotifyFocus()
        {
            int i = FocusIndex;
            if (i < 0 || !IsHandleCreated) return;
            AccessibilityNotifyClients(AccessibleEvents.Focus, i);
            AccessibilityNotifyClients(AccessibleEvents.Selection, i);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new PanelAccessible(this); }

        // ---------------------------------------------------------------- painting
        internal static void FillRound(Graphics g, Rectangle r, float radius, Color c)
        {
            using (var p = Theme.RoundRect(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        internal void DrawTitle(Graphics g, string text, int left, int right)
        {
            TextRenderer.DrawText(g, text, FTitle, new Rectangle(left, 0, right - left, HeaderH + Sc(4)), theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        /// <summary>Square glyph button of the header (hover background when hot).</summary>
        internal void DrawIconButton(Graphics g, string key, Rectangle r, string glyph, Color color)
        {
            if (IsHot(key)) FillRound(g, r, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, glyph, FGlyphSmall, r, color,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>Rounded button: primary (accent) or secondary (bordered); muted while busy or unavailable.</summary>
        internal void DrawButton(Graphics g, Rectangle b, string label, bool primary, bool hover, bool muted)
        {
            Color back, fore;
            if (muted) { back = theme.Button; fore = theme.SubText; }
            else if (!primary) { back = hover ? theme.ButtonHover : theme.Button; fore = theme.ButtonText; }
            else
            {
                back = hover ? ControlPaint.Light(theme.Accent, theme.Dark ? -0.15f : 0.25f) : theme.Accent;
                if (hover && theme.Dark) back = Color.FromArgb(80, 180, 230);
                fore = theme.AccentText;
            }
            FillRound(g, b, Sc(4), back);
            if (!primary || muted)
                using (var path = Theme.RoundRect(new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1, b.Height - 1), Sc(4)))
                using (var pen = new Pen(theme.Border)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, label, FButton, b, fore,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            current.Paint(g);

            // footer
            int top = Height - FooterH;
            using (var pen = new Pen(theme.Divider)) g.DrawLine(pen, 0, top, W, top);
            Rectangle sr = FooterLinkRect;
            if (IsHot("btsettings")) FillRound(g, sr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GBluetooth, FGlyphSmall, new Rectangle(sr.Left + Sc(6), sr.Top, Sc(22), sr.Height), theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, BtSettingsText, FStatus, new Rectangle(sr.Left + Sc(32), sr.Top, sr.Width - Sc(32), sr.Height),
                                  theme.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);

            PaintFocus(g);
        }

        // Keyboard focus: a 2 px outline in the text colour around the focused target, as Windows 11 draws it.
        void PaintFocus(Graphics g)
        {
            if (!focusVisible || current.CapturesKeyboard) return;
            int i = FocusIndex;
            if (i < 0) return;
            Target t = Targets[i];
            if (t.Offscreen) return;
            Rectangle b = t.Bounds;
            b.Inflate(Sc(2), Sc(2));
            if (!t.Clip.IsEmpty) g.SetClip(t.Clip);
            using (var path = Theme.RoundRect(new RectangleF(b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2), Sc(6)))
            using (var pen = new Pen(theme.Text, Math.Max(2, Sc(2)))) g.DrawPath(pen, path);
            g.ResetClip();
        }

        // ---------------------------------------------------------------- mouse
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHover(e.Location);
        }

        void UpdateHover(Point p)
        {
            int i = HitIndex(p);
            Target t = i >= 0 ? Targets[i] : null;
            string key = t != null ? t.Key : null;
            int row = current.RowAt(p);
            if (key == hotKey && row == hotRow) return;
            hotKey = key; hotRow = row;
            Cursor = t != null && t.Enabled ? Cursors.Hand : Cursors.Default;
            SetTip(t != null ? t.Tip : null);
            Invalidate();
        }

        /// <summary>Preview tool only: draws the panel as if the mouse were at this point.</summary>
        internal void PreviewHover(Point p) { UpdateHover(p); }

        /// <summary>Preview tool only: moves the keyboard focus n times, as pressing Tab.</summary>
        internal void PreviewTab(int n) { for (int k = 0; k < n; k++) MoveFocus(1); }

        void SetTip(string t)
        {
            if (t == tipText) return;
            tipText = t;
            tip.SetToolTip(this, t);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hotKey = null; hotRow = -1;
            SetTip(null);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            current.MouseDown(e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitIndex(e.Location);
            if (i < 0) return;
            focusVisible = false;     // the mouse hides the keyboard focus rectangle
            InvokeTarget(i);
            UpdateHover(e.Location);  // the target under the mouse may have changed (new view, new label)
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) current.DoubleClick(e.Location);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (current.Wheel(e.Delta)) { Invalidate(); UpdateHover(e.Location); }
        }

        // ---------------------------------------------------------------- keyboard
        // Tab, arrows and Enter are "dialog keys": they arrive here, before KeyDown.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (current.CapturesKeyboard) return base.ProcessDialogKey(keyData);
            switch (keyData)
            {
                case Keys.Tab:
                case Keys.Down:
                case Keys.Right:
                    MoveFocus(1); return true;
                case Keys.Shift | Keys.Tab:
                case Keys.Up:
                case Keys.Left:
                    MoveFocus(-1); return true;
                case Keys.Home:
                    focusKey = null; MoveFocus(1); return true;
                case Keys.End:
                    focusKey = null; MoveFocus(-1); return true;
                case Keys.Enter:
                    InvokeFocused(); return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        void InvokeFocused()
        {
            int i = FocusIndex;
            if (i < 0) { MoveFocus(1); return; }   // first Enter just shows where the focus is
            focusVisible = true;
            InvokeTarget(i);
            // the same target may now have a new label or be gone (e.g. "Connect" -> "Please wait…")
            if (FocusIndex < 0 && view == View.Devices) MoveFocus(1);
            else NotifyFocus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // while renaming, the keys belong to the text box (KeyPreview sends them here first)
            if (current.CapturesKeyboard) { base.OnKeyDown(e); return; }
            if (e.KeyCode == Keys.Escape)
            {
                if (view != View.Devices) SetView(View.Devices);
                else Hide();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5) { current.F5(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None) { InvokeFocused(); e.Handled = true; }
            else
            {
                int i = FocusIndex;
                if (current.Key(e.KeyCode, i >= 0 ? Targets[i] : null)) e.Handled = true;
            }
            base.OnKeyDown(e);
        }
    }
}
