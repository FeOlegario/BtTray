using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>Borderless flyout shown above the tray icon. Everything is owner-drawn (no child controls) to stay light.</summary>
    class PopupForm : Form
    {
        public event Action<BtDevice> ToggleRequested;
        public event Action RefreshRequested;
        public event Action DeviceChanged;

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
        bool hoverBtn, hoverRefresh, hoverSettings;

        Font fTitle, fName, fStatus, fButton, fGlyph, fGlyphSmall, fGlyphBattery;

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
            W = Sc(360); HeaderH = Sc(48); RowH = Sc(60); FooterH = Sc(44); Pad = Sc(6);

            fTitle = new Font("Segoe UI Semibold", 11.5f);
            fName = new Font("Segoe UI", 10f);
            fStatus = new Font("Segoe UI", 8.5f);
            fButton = new Font("Segoe UI", 9f);
            fGlyph = new Font(Theme.IconFont, 15f);
            fGlyphSmall = new Font(Theme.IconFont, 11f);
            fGlyphBattery = new Font(Theme.IconFont, 11f);
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
            Invalidate();
        }

        void UpdateSize()
        {
            int rows = Math.Max(1, Math.Min(devices.Count, MaxRows));
            int h = HeaderH + rows * RowH + Pad * 2 + FooterH;
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
            scroll = 0; hoverRow = -1;
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
            LastHidden = DateTime.Now;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.F5 && RefreshRequested != null) RefreshRequested();
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

        Rectangle RefreshRect { get { int z = Sc(34); return new Rectangle(W - z - Sc(10), (HeaderH - z) / 2 + Sc(2), z, z); } }

        Rectangle SettingsRect
        {
            get
            {
                int top = Height - FooterH;
                Size sz = TextRenderer.MeasureText("Configurações de Bluetooth", fStatus);
                return new Rectangle(Sc(10), top + Sc(7), sz.Width + Sc(40), FooterH - Sc(14));
            }
        }

        // ---------------------------------------------------------------- painting
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // header
            TextRenderer.DrawText(g, "Dispositivos Bluetooth", fTitle, new Rectangle(Sc(18), 0, W - Sc(80), HeaderH + Sc(4)),
                                  theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Rectangle rr = RefreshRect;
            if (hoverRefresh) FillRound(g, rr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GRefresh, fGlyphSmall, rr, theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // list
            Rectangle lr = ListRect;
            g.SetClip(lr);
            if (devices.Count == 0)
            {
                TextRenderer.DrawText(g, "Nenhum dispositivo emparelhado", fName, lr, theme.SubText,
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

            // footer
            int top = Height - FooterH;
            using (var pen = new Pen(theme.Divider)) g.DrawLine(pen, 0, top, W, top);
            Rectangle sr = SettingsRect;
            if (hoverSettings) FillRound(g, sr, Sc(5), theme.CardHover);
            TextRenderer.DrawText(g, Theme.GSettings, fGlyphSmall, new Rectangle(sr.Left + Sc(6), sr.Top, Sc(22), sr.Height), theme.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "Configurações de Bluetooth", fStatus, new Rectangle(sr.Left + Sc(32), sr.Top, sr.Width - Sc(32), sr.Height),
                                  theme.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
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
            int textRight = d.CanToggle ? ButtonRect(r).Left - Sc(8) : r.Right - Sc(10);
            int mid = r.Top + r.Height / 2;
            TextRenderer.DrawText(g, d.Name, fName, new Rectangle(textLeft, r.Top + Sc(8), textRight - textLeft, mid - r.Top - Sc(7)),
                                  theme.Text, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // status line
            Rectangle st = new Rectangle(textLeft, mid + Sc(2), textRight - textLeft, Sc(18));
            TextFormatFlags tf = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (busy != null)
                TextRenderer.DrawText(g, busy, fStatus, st, theme.Accent, tf);
            else if (err != null)
                TextRenderer.DrawText(g, err, fStatus, st, Color.FromArgb(theme.Dark ? 255 : 196, theme.Dark ? 153 : 43, theme.Dark ? 164 : 28), tf);
            else
            {
                string status = d.Connected ? "Conectado" : "Desconectado";
                TextRenderer.DrawText(g, status, fStatus, st, theme.SubText, tf);
                if (d.Connected && d.Battery >= 0)
                {
                    int x = st.Left + TextRenderer.MeasureText(g, status, fStatus, st.Size, tf).Width + Sc(10);
                    TextRenderer.DrawText(g, Theme.BatteryGlyph(d.Battery), fGlyphBattery, new Rectangle(x, st.Top - Sc(1), Sc(22), st.Height + Sc(2)),
                                          d.Battery <= 15 ? Color.FromArgb(232, 17, 35) : theme.SubText,
                                          TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, d.Battery + "%", fStatus, new Rectangle(x + Sc(24), st.Top, Sc(50), st.Height), theme.SubText, tf);
                }
            }

            // button
            if (d.CanToggle)
            {
                Rectangle b = ButtonRect(r);
                bool hb = hover && hoverBtn && busy == null;
                string label;
                Color back, fore;
                if (busy != null) { label = "Aguarde…"; back = theme.Button; fore = theme.SubText; }
                else if (d.Connected) { label = "Desconectar"; back = hb ? theme.ButtonHover : theme.Button; fore = theme.ButtonText; }
                else
                {
                    label = "Conectar";
                    back = hb ? ControlPaint.Light(theme.Accent, theme.Dark ? -0.15f : 0.25f) : theme.Accent;
                    if (hb && theme.Dark) back = Color.FromArgb(80, 180, 230);
                    fore = theme.AccentText;
                }
                FillRound(g, b, Sc(4), back);
                if (d.Connected || busy != null)
                    using (var path = Theme.RoundRect(new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1, b.Height - 1), Sc(4)))
                    using (var pen = new Pen(theme.Border)) g.DrawPath(pen, path);
                TextRenderer.DrawText(g, label, fButton, b, fore,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
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
            int row = -1; bool btn = false;
            if (ListRect.Contains(e.Location))
                for (int i = 0; i < devices.Count; i++)
                    if (RowRect(i).Contains(e.Location))
                    {
                        row = i;
                        btn = devices[i].CanToggle && ButtonRect(RowRect(i)).Contains(e.Location);
                        break;
                    }
            bool hr = RefreshRect.Contains(e.Location), hs = SettingsRect.Contains(e.Location);
            if (row != hoverRow || btn != hoverBtn || hr != hoverRefresh || hs != hoverSettings)
            {
                hoverRow = row; hoverBtn = btn; hoverRefresh = hr; hoverSettings = hs;
                Cursor = (btn || hr || hs) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = -1; hoverBtn = hoverRefresh = hoverSettings = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (RefreshRect.Contains(e.Location)) { if (RefreshRequested != null) RefreshRequested(); return; }
            if (SettingsRect.Contains(e.Location)) { Program.OpenBluetoothSettings(); Hide(); return; }
            if (!ListRect.Contains(e.Location)) return;
            for (int i = 0; i < devices.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (!r.Contains(e.Location)) continue;
                BtDevice d = devices[i];
                // the whole row toggles too, but only via the button for clarity
                if (d.CanToggle && ButtonRect(r).Contains(e.Location) && !busyText.ContainsKey(d.AddressHex) && ToggleRequested != null)
                    ToggleRequested(d);
                return;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int maxScroll = Math.Max(0, devices.Count * RowH - ListRect.Height);
            int ns = Math.Max(0, Math.Min(maxScroll, scroll - Math.Sign(e.Delta) * RowH));
            if (ns != scroll) { scroll = ns; Invalidate(); }
        }
    }
}
