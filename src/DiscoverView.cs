using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>"Add device": nearby unpaired devices with a Pair button. The search itself is run by TrayApp.</summary>
    class DiscoverView : PanelView
    {
        List<FoundDevice> found = new List<FoundDevice>();
        bool searching;
        string pairingId, error;
        int scroll;

        public DiscoverView(PopupForm form) : base(form) { }

        public override string Title { get { return L.T("Adicionar dispositivo", "Add device"); } }

        public override int ContentHeight { get { return F.SectionH + Math.Max(1, Math.Min(found.Count, F.MaxRows - 1)) * F.RowH; } }

        // ---------------------------------------------------------------- data (from PopupForm)
        public void SetFound(List<FoundDevice> list, bool isSearching)
        {
            found = list;
            searching = isSearching;
            ScrollBy(0);
        }

        public void SetPairing(string id) { pairingId = id; }

        public void SetError(string message) { error = message; }

        public override void Enter() { scroll = 0; error = null; }

        public override void F5() { Rescan(); }

        void Rescan()
        {
            error = null;
            if (pairingId == null) F.RaiseRescan();
            F.Invalidate();
        }

        // ---------------------------------------------------------------- geometry
        Rectangle StatusRect { get { Rectangle l = F.ListRect; return new Rectangle(Sc(18), l.Top, F.W - Sc(36), F.SectionH); } }

        Rectangle ListArea { get { Rectangle l = F.ListRect; return new Rectangle(l.Left, l.Top + F.SectionH, l.Width, l.Height - F.SectionH); } }

        Rectangle RowRect(int i)
        {
            Rectangle l = ListArea;
            return new Rectangle(F.Pad, l.Top + i * F.RowH - scroll, F.W - F.Pad * 2, F.RowH);
        }

        public override int RowAt(Point p)
        {
            if (!ListArea.Contains(p)) return -1;
            for (int i = 0; i < found.Count; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        public override bool ScrollBy(int dy)
        {
            int max = Math.Max(0, found.Count * F.RowH - ListArea.Height);
            int ns = Math.Max(0, Math.Min(max, scroll + dy));
            if (ns == scroll) return false;
            scroll = ns;
            return true;
        }

        string StatusText
        {
            get
            {
                if (error != null) return error;
                if (searching) return L.T("Procurando dispositivos próximos…", "Looking for nearby devices…");
                if (found.Count == 0) return L.T("Nenhum dispositivo encontrado.", "No devices found.");
                return L.T("Busca concluída. Para procurar de novo, pressione F5.", "Search finished. Press F5 to search again.");
            }
        }

        string HintText
        {
            get
            {
                return searching
                    ? L.T("Deixe o dispositivo ligado, por perto e em modo de emparelhamento.",
                          "Keep the device on, nearby and in pairing mode.")
                    : L.T("Coloque o dispositivo em modo de emparelhamento e pressione F5 para procurar de novo.",
                          "Put the device in pairing mode and press F5 to search again.");
            }
        }

        // ---------------------------------------------------------------- targets
        public override void BuildTargets(List<Target> t)
        {
            t.Add(F.HeaderButton("back", F.BackRect, L.T("Voltar", "Back"), L.T("Voltar (Esc)", "Back (Esc)"),
                                 delegate { F.SetView(PopupForm.View.Devices); }));
            t.Add(F.HeaderButton("rescan", F.RefreshRect, L.T("Procurar novamente", "Search again"),
                                 L.T("Procurar novamente (F5)", "Search again (F5)"), Rescan));
            t.Add(new Target { Key = "status", Bounds = StatusRect, Role = AccessibleRole.StaticText, Name = StatusText });

            Rectangle clip = ListArea;
            if (found.Count == 0)
                t.Add(new Target { Key = "hint", Bounds = clip, Role = AccessibleRole.StaticText, Name = HintText });
            for (int i = 0; i < found.Count; i++)
            {
                FoundDevice f = found[i];
                bool busy = pairingId == f.Id;
                t.Add(new Target
                {
                    Key = "pair:" + f.Id, Bounds = F.ButtonRect(RowRect(i)), Clip = clip, Row = i,
                    Name = (busy ? L.T("Emparelhando: ", "Pairing: ") : L.T("Emparelhar: ", "Pair: ")) + f.Name,
                    Description = f.IsLE ? "Bluetooth LE" : "Bluetooth",
                    Enabled = pairingId == null,
                    Invoke = delegate { error = null; F.RaisePair(f); }
                });
            }
        }

        // ---------------------------------------------------------------- painting
        public override void Paint(Graphics g)
        {
            Theme th = F.Th;
            Rectangle br = F.BackRect, rr = F.RefreshRect;
            F.DrawIconButton(g, "back", br, Theme.GBack, th.SubText);
            F.DrawTitle(g, Title, br.Right + Sc(6), rr.Left - Sc(4));
            F.DrawIconButton(g, "rescan", rr, Theme.GRefresh, searching ? th.Accent : th.SubText);

            Color sc = error != null ? F.ErrorColor : searching ? th.Accent : th.SubText;
            TextRenderer.DrawText(g, StatusText, F.FStatus, StatusRect, sc,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            Rectangle fl = ListArea;
            g.SetClip(fl);
            if (found.Count == 0)
                TextRenderer.DrawText(g, HintText, F.FStatus, new Rectangle(Sc(24), fl.Top, F.W - Sc(48), fl.Height), th.SubText,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            for (int i = 0; i < found.Count; i++)
            {
                Rectangle r = RowRect(i);
                if (r.Bottom < fl.Top || r.Top > fl.Bottom) continue;
                DrawFound(g, found[i], r, F.RowIsHot(i));
            }
            g.ResetClip();
            PaintScrollHint(g, fl, found.Count * F.RowH, scroll);
        }

        void DrawFound(Graphics g, FoundDevice f, Rectangle r, bool hover)
        {
            Theme th = F.Th;
            bool busy = pairingId == f.Id, blocked = pairingId != null && !busy;
            if (hover && !blocked) PopupForm.FillRound(g, r, Sc(6), th.CardHover);

            TextRenderer.DrawText(g, Theme.Glyph(f.Kind), F.FGlyph, new Rectangle(r.Left + Sc(8), r.Top, Sc(36), r.Height), th.SubText,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            Rectangle b = F.ButtonRect(r);
            int textLeft = r.Left + Sc(52), textRight = b.Left - Sc(8);
            int mid = r.Top + r.Height / 2;
            TextRenderer.DrawText(g, f.Name, F.FName, new Rectangle(textLeft, r.Top + Sc(8), textRight - textLeft, mid - r.Top - Sc(7)),
                                  th.Text, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string sub = busy ? L.T("Emparelhando…", "Pairing…") : f.IsLE ? "Bluetooth LE" : "Bluetooth";
            TextRenderer.DrawText(g, sub, F.FStatus, new Rectangle(textLeft, mid + Sc(2), textRight - textLeft, Sc(18)), busy ? th.Accent : th.SubText,
                                  TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            F.DrawButton(g, b, busy ? L.T("Aguarde…", "Please wait…") : L.T("Emparelhar", "Pair"), true,
                         F.IsHot("pair:" + f.Id) && pairingId == null, busy || blocked);
        }
    }
}
