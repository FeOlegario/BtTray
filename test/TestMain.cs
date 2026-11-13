using System;
using System.Diagnostics;

namespace BtTray
{
    static class TestMain
    {
        static void Main(string[] args)
        {
            var sw = Stopwatch.StartNew();
            var list = Bluetooth.Enumerate();
            Console.WriteLine("Enumerate: " + sw.ElapsedMilliseconds + " ms");
            foreach (var d in list)
                Console.WriteLine(string.Format("{0} | {1} | LE={2} | conn={3} | bat={4} | kind={5} | audio={6} | cod=0x{7:X}",
                    d.Name, d.AddressHex, d.IsLE, d.Connected, d.Battery, d.Kind, d.HasAudio, d.ClassOfDevice));
            if (args.Length == 2)
            {
                var d = list[int.Parse(args[1])];
                sw.Restart();
                string r = args[0] == "connect" ? Bluetooth.Connect(d) : Bluetooth.Disconnect(d);
                Console.WriteLine(args[0] + " -> " + (r ?? "OK") + " (" + sw.ElapsedMilliseconds + " ms)");
            }
        }
    }
}
