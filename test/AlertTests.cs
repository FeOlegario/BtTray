using System;
using System.Collections.Generic;

namespace BtTray
{
    /// <summary>
    /// Automated checks for the low-battery alert rules (BatteryAlerts). Runs without UI or Bluetooth, so it also
    /// runs in CI. Exits with code 1 if any check fails.
    /// Build: csc /main:BtTray.AlertTests /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs test\AlertTests.cs
    /// </summary>
    static class AlertTests
    {
        static int failures;

        static int Main()
        {
            // Normal discharge with flickering readings and a partial recharge (same sequence as PreviewMain).
            // Expected: "F" = 20% warning, "C" = 10% warning, "-" = nothing.
            Sequence("descarga com oscilação e recarga",
                new[] { 60, 21, 20, 19, 20, 15, 11, 10, 9, 10, 12, 16, 14, 10, 18, 26, 22, 20, 8 },
                new[] { "-", "-", "F", "-", "-", "-", "-", "C", "-", "-", "-", "-", "-", "C", "-", "-", "-", "F", "C" });

            // Dropping straight past both thresholds gives only the critical warning.
            Sequence("queda direta abaixo de 10%", new[] { 60, 8, 5 }, new[] { "-", "C", "-" });

            // 20% warning re-arms only above 25%.
            Sequence("rearme do aviso de 20%", new[] { 20, 25, 20, 26, 20 }, new[] { "F", "-", "-", "-", "F" });

            // 10% warning re-arms only above 15%.
            Sequence("rearme do aviso de 10%", new[] { 10, 15, 10, 16, 10 }, new[] { "C", "-", "-", "-", "C" });

            // Disconnected devices and unknown battery never warn.
            {
                var alerts = new BatteryAlerts();
                var off = Dev("A", 5); off.Connected = false;
                var unknown = Dev("B", -1);
                Check("desconectado ou bateria desconhecida", alerts.Check(new[] { off, unknown }).Count == 0);
            }

            // Each device is tracked separately.
            {
                var alerts = new BatteryAlerts();
                var a = Dev("A", 20); var b = Dev("B", 20);
                Check("dois dispositivos avisam separadamente", alerts.Check(new[] { a, b }).Count == 2);
                Check("dois dispositivos não repetem", alerts.Check(new[] { a, b }).Count == 0);
            }

            // Message text mentions the device and the level.
            {
                var res = new BatteryAlerts().Check(new[] { Dev("Fone", 10) });
                Check("texto do aviso crítico", res.Count == 1 && res[0].Critical
                    && res[0].Title.Contains("Fone") && res[0].Message.Contains("10%"));
            }

            // English texts, and reading the saved language code (Portuguese is the default).
            {
                L.Current = Language.English;
                var res = new BatteryAlerts().Check(new[] { Dev("Fone", 10) });
                L.Current = Language.PtBR;
                Check("aviso em inglês", res.Count == 1 && res[0].Title.StartsWith("Critical battery: ")
                    && res[0].Message.Contains("10%"));
                var pt = new BatteryAlerts().Check(new[] { Dev("Fone", 20) });
                Check("aviso em português por padrão", pt.Count == 1 && pt[0].Title.StartsWith("Bateria fraca: "));
                Check("código de idioma", L.Parse("en") == Language.English && L.Parse("EN-us") == Language.English
                    && L.Parse("pt-BR") == Language.PtBR && L.Parse("") == Language.PtBR && L.Parse(null) == Language.PtBR
                    && L.Parse("xx") == Language.PtBR && L.Parse(L.Code(Language.English)) == Language.English);
            }

            // Device nicknames (DeviceNames): shown instead of the Windows name, list sorted again, cleaned input.
            {
                var a = new BtDevice { Name = "Zeta", OriginalName = "Zeta", AddressHex = "AAAAAAAAAAAA", Connected = true };
                var b = new BtDevice { Name = "Mouse", OriginalName = "Mouse", AddressHex = "BBBBBBBBBBBB", Connected = true };
                var c = new BtDevice { Name = "Caixa", OriginalName = "Caixa", AddressHex = "CCCCCCCCCCCC", Connected = false };
                var list = new List<BtDevice> { b, a, c };
                DeviceNames.Apply(list, new Dictionary<string, string> { { "AAAAAAAAAAAA", "Fone do trabalho" } });
                Check("apelido substitui o nome", a.Name == "Fone do trabalho" && a.OriginalName == "Zeta" && b.Name == "Mouse");
                Check("lista reordenada pelo apelido, conectados primeiro", list[0] == a && list[1] == b && list[2] == c);
                Check("nome vazio volta ao original", DeviceNames.Normalize("   ", "LEO-577") == null && DeviceNames.Normalize("", "LEO-577") == null);
                Check("nome igual ao original remove o apelido", DeviceNames.Normalize(" LEO-577 ", "LEO-577") == null);
                Check("apelido sem espaços nas pontas", DeviceNames.Normalize("  Fone  ", "LEO-577") == "Fone");
                Check("apelido em uma linha só", DeviceNames.Normalize("Fone\tdo\r\ntrabalho", "x") == "Fone do trabalho");
                Check("apelido limitado a 64 caracteres", DeviceNames.Normalize(new string('a', 100), "x").Length == DeviceNames.MaxLength);
            }

            Console.WriteLine(failures == 0 ? "Todos os testes passaram." : failures + " teste(s) falharam.");
            return failures == 0 ? 0 : 1;
        }

        static BtDevice Dev(string id, int battery)
        {
            return new BtDevice { Name = "Dispositivo " + id, AddressHex = "00000000000" + id, Connected = true, Battery = battery };
        }

        static void Sequence(string name, int[] readings, string[] expected)
        {
            var alerts = new BatteryAlerts();
            var dev = Dev("1", -1);
            var got = new List<string>();
            foreach (int r in readings)
            {
                dev.Battery = r;
                var res = alerts.Check(new[] { dev });
                got.Add(res.Count == 0 ? "-" : res.Count > 1 ? "?" : res[0].Critical ? "C" : "F");
            }
            string e = string.Join(" ", expected), g = string.Join(" ", got.ToArray());
            Check(name + (e == g ? "" : "\n      leituras: " + string.Join(" ", Array.ConvertAll(readings, x => x.ToString()))
                + "\n      esperado: " + e + "\n      obtido:   " + g), e == g);
        }

        static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  FALHA ") + name);
            if (!ok) failures++;
        }
    }
}
