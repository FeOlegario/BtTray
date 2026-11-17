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

        static void Render(List<BtDevice> list, bool dark, string path)
        {
            Theme.ForceDark = dark;
            using (var f = new PopupForm())
            {
                var field = typeof(PopupForm).GetField("MaxRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field.SetValue(f, list.Count);
                f.SetDevices(list, new Dictionary<string, string>(), new Dictionary<string, string>());
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path);
                    Console.WriteLine("Prévia salva em " + path);
                }
            }
        }
    }
}
