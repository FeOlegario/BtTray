using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BtTray
{
    /// <summary>
    /// Nicknames the user gave to devices. They only exist in BtTray (Windows keeps the original name) and are saved
    /// in %LOCALAPPDATA%\BtTray\nomes.txt, one "ADDRESS\tNickname" per line.
    /// </summary>
    static class DeviceNames
    {
        public const int MaxLength = 64;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtTray", "nomes.txt"); }
        }

        public static Dictionary<string, string> Load()
        {
            var names = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(FilePath)) return names;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] p = line.Split(new[] { '\t' }, 2);
                    if (p.Length < 2 || p[0].Length != 12) continue;
                    string name = Clean(p[1]);
                    if (name.Length > 0) names[p[0].ToUpperInvariant()] = name;
                }
            }
            catch { }
            return names;
        }

        public static void Save(Dictionary<string, string> names)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var kv in names) lines.Add(kv.Key + "\t" + kv.Value);
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Shows each device under its nickname, then sorts the list again (it is sorted by name).</summary>
        public static void Apply(List<BtDevice> devices, Dictionary<string, string> names)
        {
            bool any = false;
            foreach (BtDevice d in devices)
            {
                string n;
                if (names.TryGetValue(d.AddressHex, out n)) { d.Name = n; any = true; }
            }
            if (any) Bluetooth.SortDevices(devices);
        }

        /// <summary>
        /// Turns what the user typed into the nickname to save, or null to go back to the original name
        /// (empty text, or the same as the name Windows reports).
        /// </summary>
        public static string Normalize(string typed, string original)
        {
            string n = Clean(typed);
            if (n.Length == 0) return null;
            if (original != null && string.Equals(n, original.Trim(), StringComparison.Ordinal)) return null;
            return n;
        }

        // Single line, no tabs (they separate the fields in the file), trimmed and limited in length.
        static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
            string n = sb.ToString().Trim();
            while (n.Contains("  ")) n = n.Replace("  ", " ");
            if (n.Length > MaxLength) n = n.Substring(0, MaxLength).TrimEnd();
            return n;
        }
    }
}
