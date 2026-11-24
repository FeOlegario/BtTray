using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>App settings (opened by the gear): the interface language.</summary>
    class SettingsView : PanelView
    {
        static readonly Language[] Languages = { Language.PtBR, Language.English };

        public SettingsView(PopupForm form) : base(form) { }

        public override string Title { get { return L.T("Configurações", "Settings"); } }

        public override int ContentHeight { get { return F.SectionH + Languages.Length * F.OptH + Sc(4); } }

        Rectangle LangRect(int i)
        {
            Rectangle l = F.ListRect;
            return new Rectangle(F.Pad, l.Top + F.SectionH + i * F.OptH, F.W - F.Pad * 2, F.OptH);
        }

        public override void BuildTargets(List<Target> t)
        {
            t.Add(F.HeaderButton("back", F.BackRect, L.T("Voltar", "Back"), L.T("Voltar (Esc)", "Back (Esc)"),
                                 delegate { F.SetView(PopupForm.View.Devices); }));
            Rectangle lr = F.ListRect;
            t.Add(new Target { Key = "section", Bounds = new Rectangle(Sc(18), lr.Top, F.W - Sc(36), F.SectionH),
                               Role = AccessibleRole.StaticText, Name = L.T("Idioma", "Language") });
            for (int i = 0; i < Languages.Length; i++)
            {
                Language lang = Languages[i];
                t.Add(new Target
                {
                    Key = "lang:" + i, Bounds = LangRect(i), Role = AccessibleRole.RadioButton,
                    Name = L.NativeName(lang), Checked = lang == L.Current,
                    Description = L.T("Idioma", "Language"),
                    Invoke = delegate { L.Set(lang); F.Invalidate(); }
                });
            }
        }

        public override void Paint(Graphics g)
        {
            Theme th = F.Th;
            Rectangle br = F.BackRect;
            F.DrawIconButton(g, "back", br, Theme.GBack, th.SubText);
            F.DrawTitle(g, Title, br.Right + Sc(6), F.W - Sc(18));

            Rectangle lr = F.ListRect;
            TextRenderer.DrawText(g, L.T("Idioma", "Language"), F.FSection, new Rectangle(Sc(18), lr.Top, F.W - Sc(36), F.SectionH),
                                  th.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            for (int i = 0; i < Languages.Length; i++)
            {
                Rectangle r = LangRect(i);
                bool selected = Languages[i] == L.Current;
                if (F.IsHot("lang:" + i)) PopupForm.FillRound(g, r, Sc(6), th.CardHover);
                if (selected)   // accent bar on the left, like the Windows 11 selected item
                    PopupForm.FillRound(g, new Rectangle(r.Left + Sc(2), r.Top + Sc(12), Sc(3), r.Height - Sc(24)), Sc(1.5f), th.Accent);
                TextRenderer.DrawText(g, L.NativeName(Languages[i]), F.FName, new Rectangle(r.Left + Sc(16), r.Top, r.Width - Sc(56), r.Height),
                                      th.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (selected)
                    TextRenderer.DrawText(g, Theme.GCheck, F.FGlyphSmall, new Rectangle(r.Right - Sc(40), r.Top, Sc(30), r.Height), th.Accent,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
