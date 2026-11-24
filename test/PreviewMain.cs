using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>
    /// Development helper: checks the low-battery alert rules with simulated readings and renders the
    /// panel with fake devices at several battery levels to a PNG.
    /// Build: csc /main:BtTray.PreviewMain /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs test\PreviewMain.cs
    /// Usage: preview.exe [saida.png] [--notificar]
    /// </summary>
    static class PreviewMain
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 1) alert rules
            var alerts = new BatteryAlerts();
            var dev = new BtDevice { Name = "Fone de teste", AddressHex = "000000000001", Connected = true };
            int[] readings = { 60, 21, 20, 19, 20, 15, 11, 10, 9, 10, 12, 16, 14, 10, 18, 26, 22, 20, 8 };
            foreach (int r in readings)
            {
                dev.Battery = r;
                var res = alerts.Check(new[] { dev });
                Console.WriteLine("{0,3}% -> {1}", r, res.Count == 0 ? "-" : (res[0].Critical ? "CRÍTICO: " : "FRACA: ") + res[0].Message);
            }

            // 2) render the panel
            int[] levels = { 100, 75, 45, 20, 19, 15, 11, 10, 4 };
            var list = new List<BtDevice>();
            foreach (int l in levels)
                list.Add(new BtDevice { Name = "Dispositivo com " + l + "%", AddressHex = "AA00000000" + l.ToString("X2"),
                                        Connected = true, Battery = l, Kind = DeviceKind.Headphones });
            string basePath = args.Length > 0 ? args[0] : "preview.png";
            Render(list, false, basePath.Replace(".png", "-claro.png"));
            Render(list, true, basePath.Replace(".png", "-escuro.png"));
            RenderPinnedIcons(basePath.Replace(".png", "-icones.png"));
            Render(list, false, basePath.Replace(".png", "-configuracoes-claro.png"), true);
            Render(list, true, basePath.Replace(".png", "-configuracoes-escuro.png"), true);
            RenderRename(list, false, basePath.Replace(".png", "-renomear-claro.png"));
            RenderRename(list, true, basePath.Replace(".png", "-renomear-escuro.png"));
            RenderDiscover(false, true, basePath.Replace(".png", "-busca-claro.png"));
            RenderDiscover(true, false, basePath.Replace(".png", "-busca-escuro.png"));
            RenderDiscover(false, false, basePath.Replace(".png", "-busca-vazia.png"), true);
            L.Current = Language.English;
            Render(list, false, basePath.Replace(".png", "-ingles.png"));
            Render(list, true, basePath.Replace(".png", "-ingles-configuracoes.png"), true);
            RenderDiscover(true, true, basePath.Replace(".png", "-ingles-busca.png"));
            L.Current = Language.PtBR;

            // 3) optional: show the two real Windows notifications
            if (Array.IndexOf(args, "--notificar") >= 0)
            {
                using (var tray = new NotifyIcon())
                {
                    tray.Icon = Theme.MakeTrayIcon(16, true);
                    tray.Visible = true;
                    dev.Battery = 60; alerts.Check(new[] { dev });
                    foreach (int r in new[] { 20, 10 })
                    {
                        dev.Battery = r;
                        foreach (var a in alerts.Check(new[] { dev }))
                        {
                            tray.ShowBalloonTip(10000, a.Title, a.Message, a.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
                            DateTime until = DateTime.Now.AddSeconds(8);
                            while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                        }
                    }
                    tray.Visible = false;
                }
            }
        }

        // The "Add device" view with fake results (no real search): searching or finished, one device being paired.
        static void RenderDiscover(bool dark, bool searching, string path, bool empty = false)
        {
            Theme.ForceDark = dark;
            var found = new List<FoundDevice>();
            if (!empty)
            {
                found.Add(new FoundDevice { Id = "1", Name = "Fone sem fio", Kind = DeviceKind.Headphones, Signal = -50 });
                found.Add(new FoundDevice { Id = "2", Name = "Mouse de escritório", Kind = DeviceKind.Mouse, IsLE = true, Signal = -60 });
                found.Add(new FoundDevice { Id = "3", Name = "Teclado compacto", Kind = DeviceKind.Keyboard, IsLE = true, Signal = -70 });
                found.Add(new FoundDevice { Id = "4", Name = "Caixa de som", Kind = DeviceKind.Speaker, Signal = -80 });
            }
            using (var f = new PopupForm())
            {
                f.SetView(PopupForm.View.Discover);
                f.SetFound(found, searching);
                if (!empty && !searching) f.SetPairing("2");
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path);
                    Console.WriteLine("Prévia salva em " + path);
                }
            }
        }

        // The device list with the first device being renamed and the pencil shown on the second (as if hovered).
        static void RenderRename(List<BtDevice> list, bool dark, string path)
        {
            Theme.ForceDark = dark;
            var few = list.GetRange(0, 3);
            using (var f = new PopupForm())
            {
                f.SetDevices(few, new Dictionary<string, string>(), new Dictionary<string, string>());
                var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                f.CreateControl();
                f.BeginRename(few[0]);
                typeof(PopupForm).GetField("hoverRow", bf).SetValue(f, 1);
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path);
                    Console.WriteLine("Prévia salva em " + path);
                }
            }
        }

        static void Render(List<BtDevice> list, bool dark, string path, bool settings = false)
        {
            Theme.ForceDark = dark;
            using (var f = new PopupForm())
            {
                var field = typeof(PopupForm).GetField("MaxRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field.SetValue(f, list.Count);
                f.Pinned.Add(list[0].AddressHex);   // show both states of the pin button
                f.SetDevices(list, new Dictionary<string, string>(), new Dictionary<string, string>());
                f.SetSettingsView(settings);
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path);
                    Console.WriteLine("Prévia salva em " + path);
                }
            }
        }

        // Taskbar icons of pinned devices at the common tray sizes (100% to 200% scaling), enlarged 6x,
        // over a light and a dark taskbar. The last two columns are the app icon, with and without a connection.
        static void RenderPinnedIcons(string path)
        {
            int[] sizes = { 16, 20, 24, 32 };
            var cases = new[]
            {
                new { Kind = DeviceKind.Headphones, Connected = true, Battery = 100 },
                new { Kind = DeviceKind.Mouse, Connected = true, Battery = 85 },
                new { Kind = DeviceKind.Keyboard, Connected = true, Battery = 42 },
                new { Kind = DeviceKind.Headphones, Connected = true, Battery = 15 },
                new { Kind = DeviceKind.Gamepad, Connected = true, Battery = 7 },
                new { Kind = DeviceKind.Speaker, Connected = true, Battery = -1 },
                new { Kind = DeviceKind.Headphones, Connected = false, Battery = -1 },
            };
            const int zoom = 6, gap = 12, cell = 32 * zoom + gap;
            int band = sizes.Length * cell + gap;
            int cols = cases.Length + 2;
            using (var bmp = new Bitmap(cols * cell + gap, band * 2))
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                for (int dark = 0; dark < 2; dark++)
                {
                    using (var bg = new SolidBrush(dark == 1 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238)))
                        g.FillRectangle(bg, 0, dark * band, bmp.Width, band);
                    for (int si = 0; si < sizes.Length; si++)
                        for (int ci = 0; ci < cols; ci++)
                            using (var ic = ci < cases.Length
                                ? Theme.RenderDeviceIcon(sizes[si], cases[ci].Kind, cases[ci].Connected, cases[ci].Battery, dark == 1)
                                : Theme.RenderTrayIcon(sizes[si], ci == cases.Length, dark == 1))
                                g.DrawImage(ic, new Rectangle(gap + ci * cell, dark * band + gap + si * cell, sizes[si] * zoom, sizes[si] * zoom));
                }
                bmp.Save(path);
                Console.WriteLine("Prévia dos ícones fixos salva em " + path);
            }
        }
    }
}
