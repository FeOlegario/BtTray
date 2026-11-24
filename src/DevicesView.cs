using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>The main view: paired devices with connect/disconnect, pin, rename and the battery bar.</summary>
    class DevicesView : PanelView
    {
        int scroll;

        // renaming: a text box over the name of the device being renamed
        TextBox editBox;
        string editingAddress;

        public DevicesView(PopupForm form) : base(form) { }

        List<BtDevice> Devices { get { return F.Devices; } }

        public override string Title { get { return L.T("Dispositivos Bluetooth", "Bluetooth devices"); } }

        public override int ContentHeight { get { return Math.Max(1, Math.Min(Devices.Count, F.MaxRows)) * F.RowH; } }

        public override bool CapturesKeyboard { get { return editingAddress != null; } }

        // ---------------------------------------------------------------- geometry
        Rectangle RowRect(int i)
        {
            Rectangle l = F.ListRect;
            return new Rectangle(F.Pad, l.Top + i * F.RowH - scroll, F.W - F.Pad * 2, F.RowH);
        }

        Rectangle PinRect(Rectangle row, BtDevice d)
        {
            int z = Sc(32);
            int right = d.CanToggle ? F.ButtonRect(row).Left - Sc(4) : row.Right - Sc(10);
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
            int w = Math.Min(TextRenderer.MeasureText(d.Name, F.FName, Size.Empty, TextFormatFlags.NoPadding).Width, NameMaxWidth(row, d));
            return new Rectangle(row.Left + Sc(52) + w + Sc(2), mid - Sc(8) - z / 2, z, z);
        }

        public override int RowAt(Point p)
        {
            if (!F.ListRect.Contains(p)) return -1;
            for (int i = 0; i < Devices.Count; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        public override bool ScrollBy(int dy)
        {
            int max = Math.Max(0, Devices.Count * F.RowH - F.ListRect.Height);
            int ns = Math.Max(0, Math.Min(max, scroll + dy));
            if (ns == scroll) return false;
            scroll = ns;
            return true;
        }

        /// <summary>The device list changed: keep the scroll valid and the rename box on its device.</summary>
        public void DataChanged()
        {
            ScrollBy(0);
            if (editingAddress != null) PlaceEditBox();
        }

        public override bool Wheel(int delta)
        {
            EndRename(false);   // the text box does not follow the scrolled content
            return base.Wheel(delta);
        }

        public override void Enter() { scroll = 0; }

        public override void Leave(bool save) { EndRename(save); }

        public override void MouseDown(Point p)
        {
            // a click anywhere else in the panel finishes the renaming (the text box keeps what was typed)
            if (editingAddress != null) EndRename(true);
        }

        public override void DoubleClick(Point p)
        {
            if (!F.ListRect.Contains(p)) return;
            for (int i = 0; i < Devices.Count; i++)
                if (NameRect(RowRect(i), Devices[i]).Contains(p)) { BeginRename(Devices[i]); return; }
        }

        public override void F5() { F.RaiseRefresh(); }

        public override bool Key(Keys key, Target focused)
        {
            // F2 renames the device of the focused row, as in Explorer
            if (key != Keys.F2 || focused == null || focused.Row < 0 || focused.Row >= Devices.Count) return false;
            BeginRename(Devices[focused.Row]);
            return true;
        }

        // ---------------------------------------------------------------- targets
        static string StatusText(BtDevice d, string busy, string err)
        {
            if (busy != null) return busy;
            if (err != null) return err;
            string s = d.Connected ? L.T("Conectado", "Connected") : L.T("Desconectado", "Disconnected");
            if (d.Connected && d.Battery >= 0) s += L.T(", bateria ", ", battery ") + d.Battery + "%";
            return s;
        }

        public override void BuildTargets(List<Target> t)
        {
            t.Add(F.HeaderButton("add", F.AddRect, L.T("Adicionar dispositivo", "Add device"), null,
                                 delegate { F.SetView(PopupForm.View.Discover); }));
            t.Add(F.HeaderButton("gear", F.GearRect, L.T("Configurações", "Settings"), null,
                                 delegate { F.SetView(PopupForm.View.Settings); }));
            t.Add(F.HeaderButton("refresh", F.RefreshRect, L.T("Atualizar", "Refresh"), L.T("Atualizar (F5)", "Refresh (F5)"),
                                 delegate { F.RaiseRefresh(); }));

            Rectangle clip = F.ListRect;
            if (Devices.Count == 0)
                t.Add(new Target { Key = "empty", Bounds = clip, Role = AccessibleRole.StaticText,
                                   Name = L.T("Nenhum dispositivo emparelhado", "No paired devices") });
            for (int i = 0; i < Devices.Count; i++)
            {
                BtDevice d = Devices[i];
                Rectangle r = RowRect(i);
                string busy; F.BusyText.TryGetValue(d.AddressHex, out busy);
                string err; F.Errors.TryGetValue(d.AddressHex, out err);
                string status = StatusText(d, busy, err);
                bool pinned = F.Pinned.Contains(d.AddressHex);

                string renameTip = L.T("Renomear", "Rename");
                if (d.OriginalName != null && d.Name != d.OriginalName)
                    renameTip += L.T(" (nome no Windows: ", " (Windows name: ") + d.OriginalName + ")";
                t.Add(new Target
                {
                    Key = "name:" + d.AddressHex, Bounds = EditRect(r, d), Clip = clip, Row = i,
                    Name = L.T("Renomear ", "Rename ") + d.Name, Description = status, Tip = renameTip,
                    Invoke = delegate { BeginRename(d); }
                });
                t.Add(new Target
                {
                    Key = "pin:" + d.AddressHex, Bounds = PinRect(r, d), Clip = clip, Row = i, Checked = pinned,
                    Role = AccessibleRole.CheckButton, Description = status,
                    Name = pinned ? L.T("Fixado na barra de tarefas: ", "Pinned to taskbar: ") + d.Name
                                  : L.T("Fixar na barra de tarefas: ", "Pin to taskbar: ") + d.Name,
                    Tip = pinned ? L.T("Desafixar da barra de tarefas", "Unpin from taskbar")
                                 : L.T("Fixar na barra de tarefas (mostra o ícone e a bateria)", "Pin to taskbar (shows the icon and battery)"),
                    Invoke = delegate { F.RaisePin(d); }
                });
                if (d.CanToggle)
                {
                    string label = busy != null ? L.T("Aguarde…", "Please wait…")
                                 : d.Connected ? L.T("Desconectar", "Disconnect") : L.T("Conectar", "Connect");
                    t.Add(new Target
                    {
                        Key = "toggle:" + d.AddressHex, Bounds = F.ButtonRect(r), Clip = clip, Row = i,
                        Name = label + ": " + d.Name, Description = status, Enabled = busy == null,
                        Invoke = delegate { F.RaiseToggle(d); }
                    });
                }
            }
        }

        // ---------------------------------------------------------------- painting
        public override void Paint(Graphics g)
        {
            F.DrawTitle(g, Title, Sc(18), F.AddRect.Left - Sc(4));
            F.DrawIconButton(g, "add", F.AddRect, Theme.GAdd, F.Th.SubText);
            F.DrawIconButton(g, "gear", F.GearRect, Theme.GSettings, F.Th.SubText);
            F.DrawIconButton(g, "refresh", F.RefreshRect, Theme.GRefresh, F.Th.SubText);

            Rectangle lr = F.ListRect;
            g.SetClip(lr);
            if (Devices.Count == 0)
                TextRenderer.DrawText(g, L.T("Nenhum dispositivo emparelhado", "No paired devices"), F.FName, lr, F.Th.SubText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            for (int i = 0; i < Devices.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom < lr.Top || r.Top > lr.Bottom) continue;
                DrawRow(g, Devices[i], r, F.RowIsHot(i));
            }
            g.ResetClip();
            PaintScrollHint(g, lr, Devices.Count * F.RowH, scroll);
        }

        void DrawRow(Graphics g, BtDevice d, Rectangle r, bool hover)
        {
            Theme th = F.Th;
            if (hover) PopupForm.FillRound(g, r, Sc(6), th.CardHover);

            string busy; F.BusyText.TryGetValue(d.AddressHex, out busy);
            string err; F.Errors.TryGetValue(d.AddressHex, out err);

            // icon
            Color iconColor = d.Connected ? th.Accent : th.SubText;
            TextRenderer.DrawText(g, Theme.Glyph(d.Kind), F.FGlyph, new Rectangle(r.Left + Sc(8), r.Top, Sc(36), r.Height), iconColor,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int textLeft = r.Left + Sc(52);
            Rectangle pr = PinRect(r, d);
            int textRight = pr.Left - Sc(4);
            int mid = r.Top + r.Height / 2;
            string nameKey = "name:" + d.AddressHex;
            if (editingAddress == d.AddressHex && editBox != null)
            {
                // input frame around the text box
                Rectangle fb = editBox.Bounds;
                fb.Inflate(Sc(5), Sc(4));
                PopupForm.FillRound(g, fb, Sc(4), th.Button);
                using (var path = Theme.RoundRect(new RectangleF(fb.X + 0.5f, fb.Y + 0.5f, fb.Width - 1, fb.Height - 1), Sc(4)))
                using (var pen = new Pen(th.Accent)) g.DrawPath(pen, path);
            }
            else
            {
                TextRenderer.DrawText(g, d.Name, F.FName, NameRect(r, d), th.Text,
                                      TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (hover || F.IsFocusShown(nameKey))
                {
                    Rectangle er = EditRect(r, d);
                    if (F.IsHot(nameKey)) PopupForm.FillRound(g, er, Sc(4), th.Dark ? th.ButtonHover : th.Border);
                    TextRenderer.DrawText(g, Theme.GEdit, F.FGlyphTiny, er, th.SubText,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // status line
            Rectangle st = new Rectangle(textLeft, mid + Sc(2), textRight - textLeft, Sc(18));
            TextFormatFlags tf = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            if (busy != null)
                TextRenderer.DrawText(g, busy, F.FStatus, st, th.Accent, tf);
            else if (err != null)
                TextRenderer.DrawText(g, err, F.FStatus, st, F.ErrorColor, tf);
            else
            {
                string status = d.Connected ? L.T("Conectado", "Connected") : L.T("Desconectado", "Disconnected");
                TextRenderer.DrawText(g, status, F.FStatus, st, th.SubText, tf);
                if (d.Connected && d.Battery >= 0)
                {
                    int x = st.Left + TextRenderer.MeasureText(g, status, F.FStatus, st.Size, tf).Width + Sc(10);
                    DrawBatteryBar(g, d.Battery, x, st, tf);
                }
            }

            // pin to the taskbar
            bool pinned = F.Pinned.Contains(d.AddressHex);
            if (F.IsHot("pin:" + d.AddressHex)) PopupForm.FillRound(g, pr, Sc(5), th.Dark ? th.ButtonHover : th.Border);
            TextRenderer.DrawText(g, pinned ? Theme.GPinned : Theme.GPin, F.FGlyphSmall, pr, pinned ? th.Accent : th.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // connect / disconnect
            if (d.CanToggle)
            {
                bool hb = F.IsHot("toggle:" + d.AddressHex) && busy == null;
                string label = busy != null ? L.T("Aguarde…", "Please wait…")
                             : d.Connected ? L.T("Desconectar", "Disconnect") : L.T("Conectar", "Connect");
                F.DrawButton(g, F.ButtonRect(r), label, !d.Connected, hb, busy != null);
            }
        }

        // Horizontal bar 0-100% whose fill shrinks and changes colour with the charge, followed by the percentage.
        void DrawBatteryBar(Graphics g, int pct, int x, Rectangle st, TextFormatFlags tf)
        {
            Theme th = F.Th;
            pct = Math.Max(0, Math.Min(100, pct));
            Color fill = Theme.BatteryColor(pct, th.Dark);
            int bw = Sc(56), bh = Sc(6);
            int lineH = TextRenderer.MeasureText(g, "0%", F.FStatus, st.Size, tf).Height;
            int by = st.Top + (lineH - bh) / 2 + Sc(1);
            int avail = st.Right - x;
            if (avail < bw + Sc(40)) bw = Math.Max(Sc(24), avail - Sc(40));

            PopupForm.FillRound(g, new Rectangle(x, by, bw, bh), bh / 2f, Theme.BatteryTrack(th.Dark));
            int fw = (int)Math.Round(bw * pct / 100.0);
            if (fw > 0)
            {
                fw = Math.Max(fw, bh);                    // keep the rounded ends visible at very low levels
                PopupForm.FillRound(g, new Rectangle(x, by, fw, bh), bh / 2f, fill);
            }

            Color textColor = pct < Theme.BatteryLow ? fill : th.SubText;
            TextRenderer.DrawText(g, pct + "%", F.FStatus, new Rectangle(x + bw + Sc(6), st.Top, Sc(40), st.Height), textColor, tf);
        }

        // ---------------------------------------------------------------- renaming
        /// <summary>Turns the name of the device into a text box. Enter or clicking elsewhere saves, Esc cancels.</summary>
        public void BeginRename(BtDevice d)
        {
            if (editBox == null)
            {
                editBox = new TextBox { BorderStyle = BorderStyle.None, MaxLength = DeviceNames.MaxLength, Font = F.FName, Visible = false };
                editBox.AccessibleName = L.T("Novo nome do dispositivo", "New device name");
                editBox.KeyDown += delegate (object o, KeyEventArgs k)
                {
                    if (k.KeyCode == Keys.Enter) { k.SuppressKeyPress = true; EndRename(true); F.FocusAfterRename(); }
                    else if (k.KeyCode == Keys.Escape) { k.SuppressKeyPress = true; EndRename(false); F.FocusAfterRename(); }
                };
                editBox.LostFocus += delegate { EndRename(true); };
                F.Controls.Add(editBox);
            }
            editingAddress = d.AddressHex;
            editBox.BackColor = F.Th.Button;
            editBox.ForeColor = F.Th.Text;
            editBox.Text = d.Name;
            PlaceEditBox();
            if (editingAddress == null) return;
            editBox.Visible = true;
            editBox.Focus();
            editBox.SelectAll();
            F.Invalidate();
        }

        // Puts the text box over the name of the device being renamed, scrolling the row into view if needed.
        void PlaceEditBox()
        {
            int i = Devices.FindIndex(x => x.AddressHex == editingAddress);
            if (i < 0) { EndRename(false); return; }   // the device went away
            Rectangle lr = F.ListRect, r = RowRect(i);
            if (r.Top < lr.Top) ScrollBy(r.Top - lr.Top);
            else if (r.Bottom > lr.Bottom) ScrollBy(r.Bottom - lr.Bottom);
            r = RowRect(i);
            int left = r.Left + Sc(52), right = PinRect(r, Devices[i]).Left - Sc(12), mid = r.Top + r.Height / 2;
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
            F.Invalidate();
            if (!save) return;
            BtDevice d = Devices.Find(x => x.AddressHex == address);
            if (d != null) F.RaiseRename(d, typed);
        }
    }
}
