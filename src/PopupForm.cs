using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>
    /// Borderless flyout shown above the tray icon. Everything is owner-drawn (no child controls) to stay light.
    /// It has three views: the device list, the app settings (gear in the header) and the search for new devices
    /// (plus button in the header). The search itself is run by TrayApp; this form only draws it.
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219 && DeviceChanged != null) DeviceChanged();   // WM_DEVICECHANGE
            base.WndProc(ref m);
        }

        List<BtDevice> devices = new List<BtDevice>();
        Dictionary<string, string> busyText = new Dictionary<string, string>();   // address -> "Conectando…"
        Dictionary<string, string> errors = new Dictionary<string, string>();     // address -> message
        Theme theme = Theme.Current();

        float s = 1f;
        int W, HeaderH, RowH, FooterH, Pad, MaxRows = 6;
        int scroll, hoverRow = -1;
        bool hoverBtn, hoverPin, hoverRefresh, hoverSettings, hoverGear, hoverBack;

        static readonly Language[] Languages = { Language.PtBR, Language.English };
        View view = View.Devices;
        int hoverLang = -1;

        // search for new devices
        List<FoundDevice> found = new List<FoundDevice>();
        bool searching;
        string pairingId, discoverError;
        int fscroll, hoverFound = -1;
        bool hoverFoundBtn, hoverAdd;
        int OptH, SectionH;
        readonly ToolTip tip = new ToolTip();

        // renaming: a text box over the name of the device being renamed
        TextBox editBox;
        string editingAddress;
        bool hoverEdit;
        string tipText;

        Font fTitle, fName, fStatus, fButton, fGlyph, fGlyphSmall, fGlyphTiny, fSection;

        public DateTime LastHidden = DateTime.MinValue;

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
                     | ControlStyles.ResizeRedraw, true);

            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            W = Sc(384); HeaderH = Sc(48); RowH = Sc(60); FooterH = Sc(44); Pad = Sc(6);
            OptH = Sc(44); SectionH = Sc(30);

            fTitle = new Font("Segoe UI Semibold", 11.5f);
            fName = new Font("Segoe UI", 10f);
            fStatus = new Font("Segoe UI", 8.5f);
            fButton = new Font("Segoe UI", 9f);
            fGlyph = new Font(Theme.IconFont, 15f);
            fGlyphSmall = new Font(Theme.IconFont, 11f);
            fGlyphTiny = new Font(Theme.IconFont, 9.5f);
            fSection = new Font("Segoe UI Semibold", 9f);
            BackColor = theme.Back;
            UpdateSize();
        }

        int Sc(float v) { return (int)Math.Round(v * s); }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
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

        // ---------------------------------------------------------------- data
        public void SetDevices(List<BtDevice> list, Dictionary<string, string> busy, Dictionary<string, string> errs)
        {
            devices = list;
            busyText = busy;
            errors = errs;
            UpdateSize();
            if (editingAddress != null) PlaceEditBox();
            Invalidate();
        }

        /// <summary>Switches between the device list and the settings view.</summary>
        public void SetSettingsView(bool on) { SetView(on ? View.Settings : View.Devices); }

        public View CurrentView { get { return view; } }

        public void SetView(View v)
        {
            if (view == v) return;
            EndRename(false);
            View old = view;
            view = v;
            hoverRow = hoverLang = hoverFound = -1;
            hoverBtn = hoverPin = hoverRefresh = hoverGear = hoverBack = hoverAdd = hoverFoundBtn = false;
            fscroll = 0;
            if (v == View.Discover) discoverError = null;
            Cursor = Cursors.Default;
            UpdateTip();
            UpdateSize();
            Invalidate();
            if (old == View.Discover && DiscoverClosed != null) DiscoverClosed();
            if (v == View.Discover && DiscoverOpened != null) DiscoverOpened();
        }

        /// <summary>Results of the search for new devices.</summary>
        public void SetFound(List<FoundDevice> list, bool isSearching)
        {
            found = list;
            searching = isSearching;
            if (view == View.Discover)
            {
                hoverFound = -1; hoverFoundBtn = false;
                UpdateSize();
                fscroll = Math.Max(0, Math.Min(fscroll, found.Count * RowH - FoundListRect.Height));
            }
            Invalidate();
        }

        /// <summary>Id of the device being paired (its button shows "Please wait…"), or null.</summary>
        public void SetPairing(string id) { pairingId = id; Invalidate(); }

        /// <summary>Message shown in the status line of the search view, or null.</summary>
        public void SetDiscoverError(string message) { discoverError = message; Invalidate(); }

        void UpdateSize()
        {
            int rows = Math.Max(1, Math.Min(devices.Count, MaxRows));
            int content;
            if (view == View.Settings) content = SectionH + Languages.Length * OptH + Sc(4);
            else if (view == View.Discover) content = SectionH + Math.Max(1, Math.Min(found.Count, MaxRows - 1)) * RowH;
            else content = rows * RowH;
            int h = HeaderH + content + Pad * 2 + FooterH;
            int maxScroll = Math.Max(0, devices.Count * RowH - MaxRows * RowH);
            scroll = Math.Min(scroll, maxScroll);
            if (Width != W || Height != h)
            {
                bool vis = Visible;
                Size = new Size(W, h);
                if (vis) PlaceNearTray();
            }
        }

        public void ShowNearTray()
        {
            Theme t = Theme.Current();
            if (t.Dark != theme.Dark) { theme = t; BackColor = t.Back; if (IsHandleCreated) ApplyDwm(); }
            EndRename(false);
            scroll = 0; hoverRow = -1; hoverPin = false; hoverEdit = false;
            SetView(View.Devices);
            PlaceNearTray();
            Show();
            Activate();
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
            EndRename(true);   // clicking outside keeps what was typed, like renaming a file in Explorer
            LastHidden = DateTime.Now;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // while renaming, Esc and F5 belong to the text box (KeyPreview sends them here first)
            if (editingAddress != null) { base.OnKeyDown(e); return; }
            if (e.KeyCode == Keys.Escape) { if (view != View.Devices) SetView(View.Devices); else Hide(); }
            else if (e.KeyCode == Keys.F5)
            {
                if (view == View.Discover) Rescan();
                else if (RefreshRequested != null) RefreshRequested();
            }
            base.OnKeyDown(e);
        }

        // ---------------------------------------------------------------- geometry
        Rectangle ListRect { get { return new Rectangle(0, HeaderH + Pad, W, Height - HeaderH - FooterH - Pad * 2); } }

        Rectangle RowRect(int i)
        {
            Rectangle l = ListRect;
            return new Rectangle(Pad, l.Top + i * RowH - scroll, W - Pad * 2, RowH);
        }

        Rectangle ButtonRect(Rectangle row)
        {
            int bw = Sc(108), bh = Sc(32);
            return new Rectangle(row.Right - bw - Sc(10), row.Top + (row.Height - bh) / 2, bw, bh);
        }

        Rectangle PinRect(Rectangle row, BtDevice d)
        {
            int z = Sc(32);
            int right = d.CanToggle ? ButtonRect(row).Left - Sc(4) : row.Right - Sc(10);
            return new Rectangle(right - z, row.Top + (row.Height - z) / 2, z, z);
        }

        // name of a device row, leaving room for the pencil after it
        int NameMaxWidth(Rectangle row, BtDevice d)
        {
            return PinRect(row, d).Left - Sc(4) - (row.Left + Sc(52)) - Sc(26);
        }

        Rectangle NameRect(Rectangle row, BtDevice d)
        {
            int mid = row.Top + row.Height / 2;
            return new Rectangle(row.Left + Sc(52), row.Top + Sc(8), NameMaxWidth(row, d), mid - row.Top - Sc(7));
        }

        // pencil right after the name text
        Rectangle EditRect(Rectangle row, BtDevice d)
        {
            int z = Sc(24), mid = row.Top + row.Height / 2;
            int w = Math.Min(TextRenderer.MeasureText(d.Name, fName, Size.Empty, TextFormatFlags.NoPadding).Width, NameMaxWidth(row, d));
            return new Rectangle(row.Left + Sc(52) + w + Sc(2), mid - Sc(8) - z / 2, z, z);
        }

        Rectangle RefreshRect { get { int z = Sc(34); return new Rectangle(W - z - Sc(10), (HeaderH - z) / 2 + Sc(2), z, z); } }

        Rectangle GearRect { get { Rectangle r = RefreshRect; return new Rectangle(r.Left - r.Width - Sc(2), r.Top, r.Width, r.Height); } }

        Rectangle AddRect { get { Rectangle r = GearRect; return new Rectangle(r.Left - r.Width - Sc(2), r.Top, r.Width, r.Height); } }

        // search view: a status line, then the found devices (scrollable)
        Rectangle FoundListRect { get { Rectangle l = ListRect; return new Rectangle(l.Left, l.Top + SectionH, l.Width, l.Height - SectionH); } }

        Rectangle FoundRect(int i)
        {
            Rectangle l = FoundListRect;
            return new Rectangle(Pad, l.Top + i * RowH - fscroll, W - Pad * 2, RowH);
        }

        Color ErrorColor { get { return Color.FromArgb(theme.Dark ? 255 : 196, theme.Dark ? 153 : 43, theme.Dark ? 164 : 28); } }

        Rectangle BackRect { get { int z = Sc(34); return new Rectangle(Sc(8), (HeaderH - z) / 2 + Sc(2), z, z); } }

        Rectangle LangRect(int i)
        {
            Rectangle l = ListRect;
            return new Rectangle(Pad, l.Top + SectionH + i * OptH, W - Pad * 2, OptH);
        }

        static string BtSettingsText { get { return L.T("Configurações de Bluetooth", "Bluetooth settings"); } }

        Rectangle SettingsRect
        {
            get
            {
                int top = Height - FooterH;
                Size sz = TextRenderer.MeasureText(BtSettingsText, fStatus);
                return new Rectangle(Sc(10), top + Sc(7), sz.Width + Sc(40), FooterH - Sc(14));
            }
        }

        // ---------------------------------------------------------------- painting
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (view == View.Settings) PaintSettings(g);
            else if (view == View.Discover) PaintDiscover(g);
            else PaintDevices(g);

            // footer
            int top = Height - FooterH;
            using (var pen = new Pen(theme.Divider)) g.DrawLine(pen, 0, top, W, top);
            Rectangle sr = SettingsRect;
            if (hoverSettings) FillRound(g, sr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GBluetooth, fGlyphSmall, new Rectangle(sr.Left + Sc(6), sr.Top, Sc(22), sr.Height), theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, BtSettingsText, fStatus, new Rectangle(sr.Left + Sc(32), sr.Top, sr.Width - Sc(32), sr.Height),
                                  theme.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        void PaintDevices(Graphics g)
        {
            // header
            Rectangle gr = GearRect, ar = AddRect;
            TextRenderer.DrawText(g, L.T("Dispositivos Bluetooth", "Bluetooth devices"), fTitle,
                                  new Rectangle(Sc(18), 0, ar.Left - Sc(22), HeaderH + Sc(4)), theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (hoverAdd) FillRound(g, ar, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GAdd, fGlyphSmall, ar, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (hoverGear) FillRound(g, gr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GSettings, fGlyphSmall, gr, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Rectangle rr = RefreshRect;
            if (hoverRefresh) FillRound(g, rr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GRefresh, fGlyphSmall, rr, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // list
            Rectangle lr = ListRect;
            g.SetClip(lr);
            if (devices.Count == 0)
            {
                TextRenderer.DrawText(g, L.T("Nenhum dispositivo emparelhado", "No paired devices"), fName, lr, theme.SubText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            for (int i = 0; i < devices.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom < lr.Top || r.Top > lr.Bottom) continue;
                DrawRow(g, devices[i], r, i == hoverRow);
            }
            g.ResetClip();

            // scroll hint
            if (devices.Count > MaxRows)
            {
                int total = devices.Count * RowH;
                int th = Math.Max(Sc(20), lr.Height * lr.Height / total);
                int ty = lr.Top + (lr.Height - th) * scroll / Math.Max(1, total - lr.Height);
                FillRound(g, new Rectangle(W - Sc(5), ty, Sc(3), th), Sc(1), theme.Border);
            }
        }

        void PaintSettings(Graphics g)
        {
            // header: back arrow + title
            Rectangle br = BackRect;
            if (hoverBack) FillRound(g, br, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GBack, fGlyphSmall, br, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L.T("Configurações", "Settings"), fTitle,
                                  new Rectangle(br.Right + Sc(6), 0, W - br.Right - Sc(24), HeaderH + Sc(4)), theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // language
            Rectangle lr = ListRect;
            TextRenderer.DrawText(g, L.T("Idioma", "Language"), fSection, new Rectangle(Sc(18), lr.Top, W - Sc(36), SectionH),
                                  theme.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            for (int i = 0; i < Languages.Length; i++)
            {
                Rectangle r = LangRect(i);
                bool selected = Languages[i] == L.Current;
                if (i == hoverLang) FillRound(g, r, Sc(6), theme.CardHover);
                if (selected)   // accent bar on the left, like the Windows 11 selected item
                    FillRound(g, new Rectangle(r.Left + Sc(2), r.Top + Sc(12), Sc(3), r.Height - Sc(24)), Sc(1.5f), theme.Accent);
                TextRenderer.DrawText(g, L.NativeName(Languages[i]), fName, new Rectangle(r.Left + Sc(16), r.Top, r.Width - Sc(56), r.Height),
                                      theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (selected)
                    TextRenderer.DrawText(g, Theme.GCheck, fGlyphSmall, new Rectangle(r.Right - Sc(40), r.Top, Sc(30), r.Height), theme.Accent,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void PaintDiscover(Graphics g)
        {
            // header: back arrow + title + search again
            Rectangle br = BackRect, rr = RefreshRect;
            if (hoverBack) FillRound(g, br, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GBack, fGlyphSmall, br, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L.T("Adicionar dispositivo", "Add device"), fTitle,
                                  new Rectangle(br.Right + Sc(6), 0, rr.Left - br.Right - Sc(10), HeaderH + Sc(4)), theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (hoverRefresh) FillRound(g, rr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GRefresh, fGlyphSmall, rr, searching ? theme.Accent : theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // status line
            Rectangle lr = ListRect;
            string status;
            Color sc = theme.SubText;
            if (discoverError != null) { status = discoverError; sc = ErrorColor; }
            else if (searching) { status = L.T("Procurando dispositivos próximos…", "Looking for nearby devices…"); sc = theme.Accent; }
            else if (found.Count == 0) status = L.T("Nenhum dispositivo encontrado.", "No devices found.");
            else status = L.T("Busca concluída. Para procurar de novo, pressione F5.", "Search finished. Press F5 to search again.");
            TextRenderer.DrawText(g, status, fStatus, new Rectangle(Sc(18), lr.Top, W - Sc(36), SectionH), sc,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // found devices
            Rectangle fl = FoundListRect;
            g.SetClip(fl);
            if (found.Count == 0)
            {
                string hint = searching
                    ? L.T("Deixe o dispositivo ligado, por perto e em modo de emparelhamento.",
                          "Keep the device on, nearby and in pairing mode.")
                    : L.T("Coloque o dispositivo em modo de emparelhamento e pressione F5 para procurar de novo.",
                          "Put the device in pairing mode and press F5 to search again.");
                TextRenderer.DrawText(g, hint, fStatus, new Rectangle(Sc(24), fl.Top, W - Sc(48), fl.Height), theme.SubText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            for (int i = 0; i < found.Count; i++)
            {
                Rectangle r = FoundRect(i);
                if (r.Bottom < fl.Top || r.Top > fl.Bottom) continue;
                DrawFound(g, found[i], r, i == hoverFound);
            }
            g.ResetClip();

            if (found.Count * RowH > fl.Height)
            {
                int total = found.Count * RowH;
                int th = Math.Max(Sc(20), fl.Height * fl.Height / total);
                int ty = fl.Top + (fl.Height - th) * fscroll / Math.Max(1, total - fl.Height);
                FillRound(g, new Rectangle(W - Sc(5), ty, Sc(3), th), Sc(1), theme.Border);
            }
        }

        void DrawFound(Graphics g, FoundDevice f, Rectangle r, bool hover)
        {
            bool busy = pairingId == f.Id, blocked = pairingId != null && !busy;
            if (hover && !blocked) FillRound(g, r, Sc(6), theme.CardHover);

            TextRenderer.DrawText(g, Theme.Glyph(f.Kind), fGlyph, new Rectangle(r.Left + Sc(8), r.Top, Sc(36), r.Height), theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            Rectangle b = ButtonRect(r);
            int textLeft = r.Left + Sc(52), textRight = b.Left - Sc(8);
            int mid = r.Top + r.Height / 2;
            TextRenderer.DrawText(g, f.Name, fName, new Rectangle(textLeft, r.Top + Sc(8), textRight - textLeft, mid - r.Top - Sc(7)),
                                  theme.Text, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string sub = busy ? L.T("Emparelhando…", "Pairing…") : f.IsLE ? "Bluetooth LE" : "Bluetooth";
            TextRenderer.DrawText(g, sub, fStatus, new Rectangle(textLeft, mid + Sc(2), textRight - textLeft, Sc(18)), busy ? theme.Accent : theme.SubText,
                                  TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            DrawButton(g, b, busy ? L.T("Aguarde…", "Please wait…") : L.T("Emparelhar", "Pair"), true,
                       hover && hoverFoundBtn && pairingId == null, busy || blocked);
        }

        /// <summary>Rounded button: primary (accent) or secondary (bordered); muted while busy or unavailable.</summary>
        void DrawButton(Graphics g, Rectangle b, string label, bool primary, bool hover, bool muted)
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
            TextRenderer.DrawText(g, label, fButton, b, fore,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawRow(Graphics g, BtDevice d, Rectangle r, bool hover)
        {
            if (hover) FillRound(g, r, Sc(6), theme.CardHover);

            string busy; busyText.TryGetValue(d.AddressHex, out busy);
            string err; errors.TryGetValue(d.AddressHex, out err);

            // icon
            Color iconColor = d.Connected ? theme.Accent : theme.SubText;
            TextRenderer.DrawText(g, Theme.Glyph(d.Kind), fGlyph, new Rectangle(r.Left + Sc(8), r.Top, Sc(36), r.Height), iconColor,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int textLeft = r.Left + Sc(52);
            Rectangle pr = PinRect(r, d);
            int textRight = pr.Left - Sc(4);
            int mid = r.Top + r.Height / 2;
            if (editingAddress == d.AddressHex && editBox != null)
            {
                // input frame around the text box
                Rectangle fb = editBox.Bounds;
                fb.Inflate(Sc(5), Sc(4));
                FillRound(g, fb, Sc(4), theme.Button);
                using (var path = Theme.RoundRect(new RectangleF(fb.X + 0.5f, fb.Y + 0.5f, fb.Width - 1, fb.Height - 1), Sc(4)))
                using (var pen = new Pen(theme.Accent)) g.DrawPath(pen, path);
            }
            else
            {
                TextRenderer.DrawText(g, d.Name, fName, NameRect(r, d), theme.Text,
                                      TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (hover)
                {
                    Rectangle er = EditRect(r, d);
                    if (hoverEdit) FillRound(g, er, Sc(4), theme.Dark ? theme.ButtonHover : theme.Border);
                    TextRenderer.DrawText(g, Theme.GEdit, fGlyphTiny, er, theme.SubText,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // status line
            Rectangle st = new Rectangle(textLeft, mid + Sc(2), textRight - textLeft, Sc(18));
            TextFormatFlags tf = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (busy != null)
                TextRenderer.DrawText(g, busy, fStatus, st, theme.Accent, tf);
            else if (err != null)
                TextRenderer.DrawText(g, err, fStatus, st, ErrorColor, tf);
            else
            {
                string status = d.Connected ? L.T("Conectado", "Connected") : L.T("Desconectado", "Disconnected");
                TextRenderer.DrawText(g, status, fStatus, st, theme.SubText, tf);
                if (d.Connected && d.Battery >= 0)
                {
                    int x = st.Left + TextRenderer.MeasureText(g, status, fStatus, st.Size, tf).Width + Sc(10);
                    DrawBatteryBar(g, d.Battery, x, st, tf);
                }
            }

            // pin to the taskbar
            bool pinned = Pinned.Contains(d.AddressHex);
            if (hover && hoverPin) FillRound(g, pr, Sc(5), theme.Dark ? theme.ButtonHover : theme.Border);
            TextRenderer.DrawText(g, pinned ? Theme.GPinned : Theme.GPin, fGlyphSmall, pr, pinned ? theme.Accent : theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // button
            if (d.CanToggle)
            {
                bool hb = hover && hoverBtn && busy == null;
                string label = busy != null ? L.T("Aguarde…", "Please wait…")
                             : d.Connected ? L.T("Desconectar", "Disconnect") : L.T("Conectar", "Connect");
                DrawButton(g, ButtonRect(r), label, !d.Connected, hb, busy != null);
            }
        }

        // Horizontal bar 0-100% whose fill shrinks and changes colour with the charge, followed by the percentage.
        void DrawBatteryBar(Graphics g, int pct, int x, Rectangle st, TextFormatFlags tf)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            Color fill = Theme.BatteryColor(pct, theme.Dark);
            int bw = Sc(56), bh = Sc(6);
            int lineH = TextRenderer.MeasureText(g, "0%", fStatus, st.Size, tf).Height;
            int by = st.Top + (lineH - bh) / 2 + Sc(1);
            int avail = st.Right - x;
            if (avail < bw + Sc(40)) bw = Math.Max(Sc(24), avail - Sc(40));

            var track = new Rectangle(x, by, bw, bh);
            FillRound(g, track, bh / 2f, Theme.BatteryTrack(theme.Dark));
            int fw = (int)Math.Round(bw * pct / 100.0);
            if (fw > 0)
            {
                fw = Math.Max(fw, bh);                    // keep the rounded ends visible at very low levels
                FillRound(g, new Rectangle(x, by, fw, bh), bh / 2f, fill);
            }

            Color textColor = pct < Theme.BatteryLow ? fill : theme.SubText;
            TextRenderer.DrawText(g, pct + "%", fStatus, new Rectangle(x + bw + Sc(6), st.Top, Sc(40), st.Height), textColor, tf);
        }

        static void FillRound(Graphics g, Rectangle r, float radius, Color c)
        {
            using (var p = Theme.RoundRect(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        // ---------------------------------------------------------------- input
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (view == View.Discover)
            {
                int fr = -1; bool fb = false;
                if (FoundListRect.Contains(e.Location))
                    for (int i = 0; i < found.Count; i++)
                        if (FoundRect(i).Contains(e.Location))
                        {
                            fr = i;
                            fb = pairingId == null && ButtonRect(FoundRect(i)).Contains(e.Location);
                            break;
                        }
                bool hbk = BackRect.Contains(e.Location), hsr = RefreshRect.Contains(e.Location), hst = SettingsRect.Contains(e.Location);
                if (fr != hoverFound || fb != hoverFoundBtn || hbk != hoverBack || hsr != hoverRefresh || hst != hoverSettings)
                {
                    hoverFound = fr; hoverFoundBtn = fb; hoverBack = hbk; hoverRefresh = hsr; hoverSettings = hst;
                    Cursor = (fb || hbk || hsr || hst) ? Cursors.Hand : Cursors.Default;
                    UpdateTip();
                    Invalidate();
                }
                return;
            }
            if (view == View.Settings)
            {
                int lang = -1;
                for (int i = 0; i < Languages.Length; i++) if (LangRect(i).Contains(e.Location)) lang = i;
                bool hbk = BackRect.Contains(e.Location), hst = SettingsRect.Contains(e.Location);
                if (lang != hoverLang || hbk != hoverBack || hst != hoverSettings)
                {
                    hoverLang = lang; hoverBack = hbk; hoverSettings = hst;
                    Cursor = (lang >= 0 || hbk || hst) ? Cursors.Hand : Cursors.Default;
                    UpdateTip();
                    Invalidate();
                }
                return;
            }
            int row = -1; bool btn = false, pin = false, edit = false;
            if (ListRect.Contains(e.Location))
                for (int i = 0; i < devices.Count; i++)
                    if (RowRect(i).Contains(e.Location))
                    {
                        row = i;
                        btn = devices[i].CanToggle && ButtonRect(RowRect(i)).Contains(e.Location);
                        pin = PinRect(RowRect(i), devices[i]).Contains(e.Location);
                        edit = editingAddress != devices[i].AddressHex && EditRect(RowRect(i), devices[i]).Contains(e.Location);
                        break;
                    }
            bool hr = RefreshRect.Contains(e.Location), hs = SettingsRect.Contains(e.Location), hg = GearRect.Contains(e.Location),
                 ha = AddRect.Contains(e.Location);
            if (row != hoverRow || btn != hoverBtn || pin != hoverPin || hr != hoverRefresh || hs != hoverSettings || hg != hoverGear
                || ha != hoverAdd || edit != hoverEdit)
            {
                hoverRow = row; hoverBtn = btn; hoverPin = pin; hoverRefresh = hr; hoverSettings = hs; hoverGear = hg; hoverAdd = ha;
                hoverEdit = edit;
                Cursor = (btn || pin || hr || hs || hg || ha || edit) ? Cursors.Hand : Cursors.Default;
                UpdateTip();
                Invalidate();
            }
        }

        void UpdateTip()
        {
            string t = null;
            if (hoverEdit && hoverRow >= 0 && hoverRow < devices.Count)
            {
                BtDevice d = devices[hoverRow];
                t = L.T("Renomear", "Rename");
                if (d.OriginalName != null && d.Name != d.OriginalName)
                    t += L.T(" (nome no Windows: ", " (Windows name: ") + d.OriginalName + ")";
            }
            else if (hoverPin && hoverRow >= 0 && hoverRow < devices.Count)
                t = Pinned.Contains(devices[hoverRow].AddressHex)
                    ? L.T("Desafixar da barra de tarefas", "Unpin from taskbar")
                    : L.T("Fixar na barra de tarefas (mostra o ícone e a bateria)", "Pin to taskbar (shows the icon and battery)");
            else if (hoverRefresh && view == View.Discover) t = L.T("Procurar novamente (F5)", "Search again (F5)");
            else if (hoverRefresh) t = L.T("Atualizar (F5)", "Refresh (F5)");
            else if (hoverAdd) t = L.T("Adicionar dispositivo", "Add device");
            else if (hoverGear) t = L.T("Configurações", "Settings");
            else if (hoverBack) t = L.T("Voltar (Esc)", "Back (Esc)");
            if (t == tipText) return;
            tipText = t;
            tip.SetToolTip(this, t);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = hoverLang = hoverFound = -1;
            hoverBtn = hoverPin = hoverRefresh = hoverSettings = hoverGear = hoverBack = hoverAdd = hoverFoundBtn = hoverEdit = false;
            UpdateTip();
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (SettingsRect.Contains(e.Location)) { Program.OpenBluetoothSettings(); Hide(); return; }
            if (view == View.Discover)
            {
                if (BackRect.Contains(e.Location)) { SetView(View.Devices); return; }
                if (RefreshRect.Contains(e.Location)) { Rescan(); return; }
                if (pairingId != null || !FoundListRect.Contains(e.Location)) return;
                for (int i = 0; i < found.Count; i++)
                    if (ButtonRect(FoundRect(i)).Contains(e.Location))
                    {
                        discoverError = null;
                        if (PairRequested != null) PairRequested(found[i]);
                        return;
                    }
                return;
            }
            if (view == View.Settings)
            {
                if (BackRect.Contains(e.Location)) { SetView(View.Devices); return; }
                for (int i = 0; i < Languages.Length; i++)
                    if (LangRect(i).Contains(e.Location))
                    {
                        L.Set(Languages[i]);
                        Invalidate();
                        return;
                    }
                return;
            }
            if (GearRect.Contains(e.Location)) { SetView(View.Settings); return; }
            if (AddRect.Contains(e.Location)) { SetView(View.Discover); return; }
            if (RefreshRect.Contains(e.Location)) { if (RefreshRequested != null) RefreshRequested(); return; }
            if (!ListRect.Contains(e.Location)) return;
            for (int i = 0; i < devices.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (!r.Contains(e.Location)) continue;
                BtDevice d = devices[i];
                if (editingAddress != d.AddressHex && EditRect(r, d).Contains(e.Location)) { BeginRename(d); return; }
                if (PinRect(r, d).Contains(e.Location))
                {
                    if (PinRequested != null) PinRequested(d);
                    UpdateTip();
                    return;
                }
                // the whole row toggles too, but only via the button for clarity
                if (d.CanToggle && ButtonRect(r).Contains(e.Location) && !busyText.ContainsKey(d.AddressHex) && ToggleRequested != null)
                    ToggleRequested(d);
                return;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // a click anywhere else in the panel finishes the renaming (the text box keeps what was typed)
            if (editingAddress != null) EndRename(true);
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (view != View.Devices || e.Button != MouseButtons.Left || !ListRect.Contains(e.Location)) return;
            for (int i = 0; i < devices.Count; i++)
                if (NameRect(RowRect(i), devices[i]).Contains(e.Location)) { BeginRename(devices[i]); return; }
        }

        /// <summary>Turns the name of the device into a text box. Enter or clicking elsewhere saves, Esc cancels.</summary>
        public void BeginRename(BtDevice d)
        {
            if (view != View.Devices) return;
            if (editBox == null)
            {
                editBox = new TextBox { BorderStyle = BorderStyle.None, MaxLength = DeviceNames.MaxLength, Font = fName, Visible = false };
                editBox.KeyDown += delegate (object o, KeyEventArgs k)
                {
                    if (k.KeyCode == Keys.Enter) { k.SuppressKeyPress = true; EndRename(true); }
                    else if (k.KeyCode == Keys.Escape) { k.SuppressKeyPress = true; EndRename(false); }
                };
                editBox.LostFocus += delegate { EndRename(true); };
                Controls.Add(editBox);
            }
            editingAddress = d.AddressHex;
            editBox.BackColor = theme.Button;
            editBox.ForeColor = theme.Text;
            editBox.Text = d.Name;
            hoverEdit = false;
            PlaceEditBox();
            if (editingAddress == null) return;
            editBox.Visible = true;
            editBox.Focus();
            editBox.SelectAll();
            UpdateTip();
            Invalidate();
        }

        // Puts the text box over the name of the device being renamed, scrolling the row into view if needed.
        void PlaceEditBox()
        {
            int i = devices.FindIndex(x => x.AddressHex == editingAddress);
            if (i < 0) { EndRename(false); return; }   // the device went away
            Rectangle lr = ListRect, r = RowRect(i);
            if (r.Top < lr.Top) scroll -= lr.Top - r.Top;
            else if (r.Bottom > lr.Bottom) scroll += r.Bottom - lr.Bottom;
            r = RowRect(i);
            int left = r.Left + Sc(52), right = PinRect(r, devices[i]).Left - Sc(12), mid = r.Top + r.Height / 2;
            editBox.Width = Math.Max(Sc(40), right - left);
            editBox.Location = new Point(left, mid - Sc(8) - editBox.Height / 2);
        }

        void EndRename(bool save)
        {
            if (editingAddress == null) return;
            string address = editingAddress;
            editingAddress = null;          // first: hiding the box raises LostFocus, which calls this again
            string typed = editBox.Text;
            editBox.Visible = false;
            Invalidate();
            if (!save) return;
            BtDevice d = devices.Find(x => x.AddressHex == address);
            if (d != null && RenameRequested != null) RenameRequested(d, typed);
        }

        void Rescan()
        {
            discoverError = null;
            if (pairingId == null && RescanRequested != null) RescanRequested();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (view == View.Settings) return;
            if (view == View.Discover)
            {
                int fmax = Math.Max(0, found.Count * RowH - FoundListRect.Height);
                int fs = Math.Max(0, Math.Min(fmax, fscroll - Math.Sign(e.Delta) * RowH));
                if (fs != fscroll) { fscroll = fs; Invalidate(); }
                return;
            }
            EndRename(false);
            int maxScroll = Math.Max(0, devices.Count * RowH - ListRect.Height);
            int ns = Math.Max(0, Math.Min(maxScroll, scroll - Math.Sign(e.Delta) * RowH));
            if (ns != scroll) { scroll = ns; Invalidate(); }
        }
    }
}
