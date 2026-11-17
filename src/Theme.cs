using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BtTray
{
    class Theme
    {
        public bool Dark;
        public Color Back, Card, CardHover, Text, SubText, Accent, AccentText, Button, ButtonHover, ButtonText, Border, Divider;

        public static string IconFont = PickIconFont();

        /// <summary>Only used by the preview tool in test/; the app always follows the Windows setting.</summary>
        public static bool? ForceDark = null;

        static string PickIconFont()
        {
            using (var f = new Font("Segoe Fluent Icons", 10f))
                if (f.Name == "Segoe Fluent Icons") return f.Name;
            return "Segoe MDL2 Assets";
        }

        public static bool AppsDark()
        {
            return ReadPersonalize("AppsUseLightTheme") == 0;
        }

        public static bool TaskbarDark()
        {
            return ReadPersonalize("SystemUsesLightTheme") == 0;
        }

        static int ReadPersonalize(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : 1;
                }
            }
            catch { return 1; }
        }

        public static Theme Current()
        {
            var t = new Theme();
            t.Dark = ForceDark.HasValue ? ForceDark.Value : AppsDark();
            if (t.Dark)
            {
                t.Back = Color.FromArgb(32, 32, 32);
                t.Card = Color.FromArgb(32, 32, 32);
                t.CardHover = Color.FromArgb(45, 45, 45);
                t.Text = Color.FromArgb(255, 255, 255);
                t.SubText = Color.FromArgb(170, 170, 170);
                t.Accent = Color.FromArgb(96, 205, 255);
                t.AccentText = Color.FromArgb(0, 0, 0);
                t.Button = Color.FromArgb(55, 55, 55);
                t.ButtonHover = Color.FromArgb(68, 68, 68);
                t.ButtonText = Color.FromArgb(255, 255, 255);
                t.Border = Color.FromArgb(70, 70, 70);
                t.Divider = Color.FromArgb(48, 48, 48);
            }
            else
            {
                t.Back = Color.FromArgb(243, 243, 243);
                t.Card = Color.FromArgb(243, 243, 243);
                t.CardHover = Color.FromArgb(232, 232, 232);
                t.Text = Color.FromArgb(26, 26, 26);
                t.SubText = Color.FromArgb(96, 96, 96);
                t.Accent = Color.FromArgb(0, 95, 184);
                t.AccentText = Color.FromArgb(255, 255, 255);
                t.Button = Color.FromArgb(251, 251, 251);
                t.ButtonHover = Color.FromArgb(240, 240, 240);
                t.ButtonText = Color.FromArgb(26, 26, 26);
                t.Border = Color.FromArgb(210, 210, 210);
                t.Divider = Color.FromArgb(225, 225, 225);
            }
            return t;
        }

        // ---------------------------------------------------------------- glyphs
        public const string GBluetooth = "", GHeadphones = "", GHeadset = "", GSpeaker = "",
                            GKeyboard = "", GMouse = "", GPhone = "", GComputer = "",
                            GGamepad = "", GRefresh = "", GSettings = "";

        public static string Glyph(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Headphones: return GHeadphones;
                case DeviceKind.Headset: return GHeadphones;
                case DeviceKind.Speaker: return GSpeaker;
                case DeviceKind.Keyboard: return GKeyboard;
                case DeviceKind.Mouse: return GMouse;
                case DeviceKind.Phone: return GPhone;
                case DeviceKind.Computer: return GComputer;
                case DeviceKind.Gamepad: return GGamepad;
            }
            return GBluetooth;
        }

        // ---------------------------------------------------------------- battery bar
        // >= 20%: shades of green (lighter near 20, deeper near 100)
        // 11-19%: yellow (19) -> orange (11)
        // <= 10%: red
        public const int BatteryLow = 20, BatteryCritical = 10;

        public static Color BatteryColor(int pct, bool dark)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            if (pct <= BatteryCritical)
                return dark ? Color.FromArgb(255, 99, 99) : Color.FromArgb(220, 38, 38);
            if (pct < BatteryLow)
            {
                float t = (pct - BatteryCritical - 1) / (float)(BatteryLow - BatteryCritical - 2);   // 11 -> 0, 19 -> 1
                return dark ? Lerp(Color.FromArgb(251, 146, 60), Color.FromArgb(250, 204, 21), t)
                            : Lerp(Color.FromArgb(234, 88, 12), Color.FromArgb(234, 179, 8), t);
            }
            float g = (pct - BatteryLow) / (float)(100 - BatteryLow);                            // 20 -> 0, 100 -> 1
            return dark ? Lerp(Color.FromArgb(190, 230, 60), Color.FromArgb(74, 222, 128), g)
                        : Lerp(Color.FromArgb(132, 190, 22), Color.FromArgb(22, 150, 70), g);
        }

        public static Color BatteryTrack(bool dark)
        {
            return dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(215, 215, 215);
        }

        static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ---------------------------------------------------------------- tray icon
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static Icon MakeTrayIcon(int size, bool anyConnected)
        {
            Color c = TaskbarDark() ? Color.White : Color.Black;
            if (!anyConnected) c = Color.FromArgb(150, c);
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var f = new Font(IconFont, size * 0.82f, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(c))
                using (var sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(GBluetooth, f, b, new RectangleF(0, 0, size, size + size * 0.06f), sf);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
