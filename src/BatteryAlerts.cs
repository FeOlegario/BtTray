using System.Collections.Generic;

namespace BtTray
{
    class BatteryAlert
    {
        public string Title;
        public string Message;
        public bool Critical;
    }

    /// <summary>
    /// Decides when to warn about low battery. Each device is warned once when it reaches 20% and once more at 10%.
    /// The warnings re-arm only after the battery climbs back up (charging), with a margin to avoid repeating
    /// the alert when the reading flickers between two values.
    /// </summary>
    class BatteryAlerts
    {
        const int RearmLow = Theme.BatteryLow + 5;            // > 25% re-arms the 20% warning
        const int RearmCritical = Theme.BatteryCritical + 5;  // > 15% re-arms the 10% warning

        // address -> 0 = nothing warned, 1 = 20% warned, 2 = 10% warned
        readonly Dictionary<string, int> level = new Dictionary<string, int>();

        public List<BatteryAlert> Check(IEnumerable<BtDevice> devices)
        {
            var result = new List<BatteryAlert>();
            foreach (BtDevice d in devices)
            {
                if (!d.Connected || d.Battery < 0) continue;
                int lvl;
                level.TryGetValue(d.AddressHex, out lvl);

                if (d.Battery > RearmLow) lvl = 0;
                else if (d.Battery > RearmCritical && lvl == 2) lvl = 1;

                if (d.Battery <= Theme.BatteryCritical && lvl < 2)
                {
                    lvl = 2;
                    result.Add(new BatteryAlert
                    {
                        Critical = true,
                        Title = "Bateria crítica: " + d.Name,
                        Message = d.Name + " está com apenas " + d.Battery + "% de bateria. Carregue agora para não perder a conexão."
                    });
                }
                else if (d.Battery <= Theme.BatteryLow && lvl < 1)
                {
                    lvl = 1;
                    result.Add(new BatteryAlert
                    {
                        Critical = false,
                        Title = "Bateria fraca: " + d.Name,
                        Message = d.Name + " está com " + d.Battery + "% de bateria. Seria bom carregar o dispositivo."
                    });
                }
                level[d.AddressHex] = lvl;
            }
            return result;
        }
    }
}
