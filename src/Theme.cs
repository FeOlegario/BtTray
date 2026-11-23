using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
                            GGamepad = "", GRefresh = "", GSettings = "",
                            GPin = "", GPinned = "",
                            GBack = "\uE72B", GCheck = "\uE73E", GAdd = "\uE710";

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
            using (var bmp = RenderTrayIcon(size, anyConnected, TaskbarDark()))
            {
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        /// <summary>App icon: the Bluetooth glyph, a bit faded when nothing is connected. Fitted to its outline and, at
        /// 16-20 px, slightly thickened: otherwise its 1 px strokes come out grey on the taskbar.</summary>
        public static Bitmap RenderTrayIcon(int size, bool anyConnected, bool darkTaskbar)
        {
            Color c = darkTaskbar ? Color.White : Color.Black;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float m = size * 0.06f;
                FillFitted(g, GBluetooth, IconFont, FontStyle.Regular, new RectangleF(m, m, size - 2 * m, size - 2 * m),
                           c, Math.Max(0f, 0.3f - (size - 16) * 0.04f), false);   // only small sizes need it
            }
            if (!anyConnected) Fade(bmp, 150);
            return bmp;
        }

        /// <summary>
        /// Icon for a device pinned to the taskbar: the device glyph on top and the battery percentage below it.
        /// Without a battery reading only the glyph is drawn; a disconnected device is drawn dimmed.
        /// </summary>
        public static Icon MakeDeviceIcon(int size, DeviceKind kind, bool connected, int battery)
        {
            using (var bmp = RenderDeviceIcon(size, kind, connected, battery, TaskbarDark()))
            {
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        public static Bitmap RenderDeviceIcon(int size, DeviceKind kind, bool connected, int battery, bool darkTaskbar)
        {
            Color fg = darkTaskbar ? Color.White : Color.Black;
            // The icon font's strokes are about 1 px wide at this size and would come out grey after antialiasing,
            // so the glyph is fitted to its real outline (not the font's em box) and thickened with a stroke.
            // Glyphs with fine inner details (keyboard keys, speaker cones) get little or no stroke to stay readable.
            float stroke = size / 16f * (kind == DeviceKind.Keyboard ? 0f : kind == DeviceKind.Speaker ? 0.2f : 0.4f);
            bool showBattery = connected && battery >= 0;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (!showBattery)
                {
                    float m = size * 0.06f;
                    FillFitted(g, Glyph(kind), IconFont, FontStyle.Regular, new RectangleF(m, m, size - 2 * m, size - 2 * m),
                               fg, stroke, false);
                }
                else
                {
                    battery = Math.Max(0, Math.Min(100, battery));
                    // glyph in the top half, number below it
                    FillFitted(g, Glyph(kind), IconFont, FontStyle.Regular, new RectangleF(0, 0, size, size * 0.50f), fg, stroke, false);
                    float top = size * 0.56f;
                    Color numColor = battery < BatteryLow ? BatteryColor(battery, darkTaskbar) : fg;
                    FillFitted(g, battery.ToString(), "Segoe UI", FontStyle.Bold, new RectangleF(0, top, size, size - top), numColor, 0f, true);
                }
            }
            if (!connected) Fade(bmp, 150);
            return bmp;
        }

        /// <summary>Scales the alpha of every pixel (fill and stroke overlap, so a translucent colour would show seams).</summary>
        static void Fade(Bitmap bmp, int alpha)
        {
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color p = bmp.GetPixel(x, y);
                    if (p.A > 0) bmp.SetPixel(x, y, Color.FromArgb(p.A * alpha / 255, p));
                }
        }

        /// <summary>Draws text scaled to fill the box as far as its outline allows, centred (or bottom-aligned).</summary>
        static void FillFitted(Graphics g, string text, string font, FontStyle style, RectangleF box, Color c, float stroke, bool bottom)
        {
            using (var path = new GraphicsPath())
            using (var family = new FontFamily(font))
            {
                path.AddString(text, family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF r = path.GetBounds();
                if (r.Width <= 0 || r.Height <= 0) return;
                float scale = Math.Min((box.Width * 0.98f - stroke) / r.Width, (box.Height - stroke) / r.Height);
                float w = r.Width * scale, h = r.Height * scale;
                float y = bottom ? box.Bottom - stroke / 2f - h : box.Y + (box.Height - h) / 2f;
                using (var m = new Matrix())
                {
                    m.Translate(box.X + (box.Width - w) / 2f, y);
                    m.Scale(scale, scale);
                    m.Translate(-r.X, -r.Y);
                    path.Transform(m);
                }
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
                if (stroke > 0)
                    using (var p = new Pen(c, stroke) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
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
