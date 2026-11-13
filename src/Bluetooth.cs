using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BtTray
{
    enum DeviceKind { Generic, Headphones, Headset, Speaker, Keyboard, Mouse, Phone, Computer, Gamepad }

    class BtDevice
    {
        public string InstanceId;
        public string Name;
        public ulong Address;
        public string AddressHex;     // 12 hex chars, upper case
        public bool IsLE;
        public bool Connected;
        public int Battery = -1;      // -1 = unknown
        public Guid ContainerId;
        public uint ClassOfDevice;
        public DeviceKind Kind;
        public bool HasAudio;         // has Bluetooth audio KS filters (reliable connect/disconnect)

        public bool CanToggle { get { return !IsLE; } }
    }

    static class Bluetooth
    {
        // ---------------------------------------------------------------- property keys
        static readonly DEVPROPKEY PK_FriendlyName = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static readonly DEVPROPKEY PK_DeviceDesc   = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        static readonly DEVPROPKEY PK_Name         = new DEVPROPKEY("b725f130-47ef-101a-a5f1-02608c9eebac", 10);
        static readonly DEVPROPKEY PK_ContainerId  = new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
        static readonly DEVPROPKEY PK_IsConnected  = new DEVPROPKEY("83da6326-97a6-4088-9453-a1923f573b29", 15);
        static readonly DEVPROPKEY PK_Battery      = new DEVPROPKEY("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
        static readonly DEVPROPKEY PK_CoD          = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 10);
        static readonly DEVPROPKEY PK_CoDOld       = new DEVPROPKEY("2bd67d8b-8beb-48d5-87e0-6cda3428040a", 4);

        static readonly Guid KSCATEGORY_AUDIO    = new Guid("6994AD04-93EF-11D0-A3CC-00A0C9223196");
        static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        const int KSPROPERTY_ONESHOT_RECONNECT  = 0;
        const int KSPROPERTY_ONESHOT_DISCONNECT = 1;

        class AudioFilter { public string Path; public Guid Container; }

        // ---------------------------------------------------------------- enumeration
        public static List<BtDevice> Enumerate()
        {
            var devices = new List<BtDevice>();
            var battery = new Dictionary<Guid, int>();

            foreach (string enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" })
            {
                IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
                if (set == Native.INVALID_HANDLE) continue;
                try
                {
                    var info = new Native.SP_DEVINFO_DATA();
                    info.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                    for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref info); i++)
                    {
                        string id = GetInstanceId(set, ref info);
                        if (id == null) continue;
                        Guid container = GetGuid(set, ref info, PK_ContainerId);

                        byte[] bat = GetProp(set, ref info, PK_Battery);
                        if (bat != null && bat.Length >= 1 && container != Guid.Empty)
                        {
                            int v = bat[0];
                            if (v <= 100) battery[container] = v;
                        }

                        string up = id.ToUpperInvariant();
                        bool classic = up.StartsWith("BTHENUM\\DEV_");
                        bool le = up.StartsWith("BTHLE\\DEV_");
                        if (!classic && !le) continue;

                        var d = new BtDevice();
                        d.InstanceId = id;
                        d.IsLE = le;
                        d.ContainerId = container;
                        int p = up.IndexOf("DEV_") + 4;
                        d.AddressHex = up.Length >= p + 12 ? up.Substring(p, 12) : "";
                        ulong addr; ulong.TryParse(d.AddressHex, System.Globalization.NumberStyles.HexNumber, null, out addr);
                        d.Address = addr;
                        d.Name = GetString(set, ref info, PK_FriendlyName) ?? GetString(set, ref info, PK_Name)
                                 ?? GetString(set, ref info, PK_DeviceDesc) ?? d.AddressHex;
                        byte[] con = GetProp(set, ref info, PK_IsConnected);
                        d.Connected = con != null && con.Length > 0 && con[0] != 0;
                        byte[] cod = GetProp(set, ref info, PK_CoD) ?? GetProp(set, ref info, PK_CoDOld);
                        if (cod != null && cod.Length >= 4) d.ClassOfDevice = BitConverter.ToUInt32(cod, 0);
                        d.Kind = KindFromCod(d.ClassOfDevice);
                        devices.Add(d);
                    }
                }
                finally { Native.SetupDiDestroyDeviceInfoList(set); }
            }

            var filters = GetAudioFilters();
            foreach (var d in devices)
            {
                int b;
                if (d.ContainerId != Guid.Empty && battery.TryGetValue(d.ContainerId, out b)) d.Battery = b;
                foreach (var f in filters)
                    if (Matches(f, d)) { d.HasAudio = true; break; }
                if (d.HasAudio && d.Kind == DeviceKind.Generic) d.Kind = DeviceKind.Headphones;
            }

            devices.Sort(delegate (BtDevice a, BtDevice b2)
            {
                if (a.Connected != b2.Connected) return a.Connected ? -1 : 1;
                return string.Compare(a.Name, b2.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return devices;
        }

        static DeviceKind KindFromCod(uint cod)
        {
            if (cod == 0) return DeviceKind.Generic;
            uint major = (cod >> 8) & 0x1F;
            uint minor = (cod >> 2) & 0x3F;
            switch (major)
            {
                case 1: return DeviceKind.Computer;
                case 2: return DeviceKind.Phone;
                case 4:
                    if (minor == 1 || minor == 2) return DeviceKind.Headset;
                    if (minor == 5 || minor == 7 || minor == 10) return DeviceKind.Speaker;
                    return DeviceKind.Headphones;
                case 5:
                    if ((minor & 0x10) != 0 && (minor & 0x20) == 0) return DeviceKind.Keyboard;
                    if ((minor & 0x20) != 0 && (minor & 0x10) == 0) return DeviceKind.Mouse;
                    if ((minor & 0x0F) == 1 || (minor & 0x0F) == 2) return DeviceKind.Gamepad;
                    return DeviceKind.Keyboard;
            }
            return DeviceKind.Generic;
        }

        static bool Matches(AudioFilter f, BtDevice d)
        {
            if (d.ContainerId != Guid.Empty && f.Container == d.ContainerId) return true;
            return d.AddressHex.Length == 12 && f.Path.IndexOf(d.AddressHex, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static List<AudioFilter> GetAudioFilters()
        {
            var list = new List<AudioFilter>();
            Guid cat = KSCATEGORY_AUDIO;
            IntPtr set = Native.SetupDiGetClassDevsGuid(ref cat, null, IntPtr.Zero, Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE) return list;
            try
            {
                var iface = new Native.SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cat, i, ref iface); i++)
                {
                    var info = new Native.SP_DEVINFO_DATA();
                    info.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                    int req;
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out req, ref info);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, buf, req, out req, ref info)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
                        if (path == null || path.IndexOf("#bth", StringComparison.OrdinalIgnoreCase) < 0
                            && path.IndexOf("\\bth", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var f = new AudioFilter();
                        f.Path = path;
                        f.Container = GetGuid(set, ref info, PK_ContainerId);
                        list.Add(f);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        // ---------------------------------------------------------------- actions
        /// <summary>Returns null on success, or an error message.</summary>
        public static string Connect(BtDevice d)
        {
            if (d.IsLE) return "Dispositivos Bluetooth LE se conectam sozinhos quando estão ligados e por perto.";
            int ok = SendToAudioFilters(d, KSPROPERTY_ONESHOT_RECONNECT);
            if (ok > 0) return null;
            return ServiceFallback(d, true);
        }

        public static string Disconnect(BtDevice d)
        {
            if (d.IsLE) return "O Windows não permite desconectar dispositivos Bluetooth LE por aplicativos.";
            int ok = SendToAudioFilters(d, KSPROPERTY_ONESHOT_DISCONNECT);
            if (ok > 0) return null;
            return ServiceFallback(d, false);
        }

        static int SendToAudioFilters(BtDevice d, int prop)
        {
            int ok = 0;
            foreach (var f in GetAudioFilters())
            {
                if (!Matches(f, d)) continue;
                if (SendKsProperty(f.Path, prop)) ok++;
            }
            return ok;
        }

        static bool SendKsProperty(string path, int id)
        {
            using (SafeFileHandle h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                       Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                var p = new Native.KSPROPERTY();
                p.Set = KSPROPSETID_BtAudio;
                p.Id = id;
                p.Flags = Native.KSPROPERTY_TYPE_GET;
                int ret;
                return Native.DeviceIoControl(h, Native.IOCTL_KS_PROPERTY, ref p, Marshal.SizeOf(typeof(Native.KSPROPERTY)),
                                              IntPtr.Zero, 0, out ret, IntPtr.Zero);
            }
        }

        // Non-audio classic devices: toggle the device's Bluetooth services.
        // Disconnect = disable services (remembered on disk); Connect = re-enable them.
        static string StateFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtTray");
                return Path.Combine(dir, "servicos-desativados.txt");
            }
        }

        static string ServiceFallback(BtDevice d, bool connect)
        {
            var info = new Native.BLUETOOTH_DEVICE_INFO();
            info.dwSize = Marshal.SizeOf(typeof(Native.BLUETOOTH_DEVICE_INFO));
            info.Address = d.Address;
            int err = Native.BluetoothGetDeviceInfo(IntPtr.Zero, ref info);
            if (err != 0) return "Não foi possível acessar o dispositivo (erro " + err + ").";

            var saved = LoadState();
            List<Guid> services;
            if (connect)
            {
                if (!saved.TryGetValue(d.AddressHex, out services)) services = InstalledServices(ref info);
                // re-enable remembered services; for still-enabled ones, cycle to force a reconnect
                bool any = false; int lastErr = 0;
                foreach (Guid g in services)
                {
                    Guid s = g;
                    if (!saved.ContainsKey(d.AddressHex)) Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, 0);
                    int r = Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, 1);
                    if (r == 0) any = true; else lastErr = r;
                }
                saved.Remove(d.AddressHex);
                SaveState(saved);
                if (any) return null;
                return Explain(lastErr, true);
            }
            else
            {
                services = InstalledServices(ref info);
                if (services.Count == 0) return "Este dispositivo não expõe serviços que possam ser desconectados.";
                var disabled = new List<Guid>();
                int lastErr = 0;
                foreach (Guid g in services)
                {
                    Guid s = g;
                    int r = Native.BluetoothSetServiceState(IntPtr.Zero, ref info, ref s, 0);
                    if (r == 0) disabled.Add(g); else lastErr = r;
                }
                if (disabled.Count == 0) return Explain(lastErr, false);
                saved[d.AddressHex] = disabled;
                SaveState(saved);
                return null;
            }
        }

        static string Explain(int err, bool connect)
        {
            if (err == 5) return "O Windows exige permissão de administrador para " + (connect ? "conectar" : "desconectar") +
                                 " este tipo de dispositivo. Execute o app como administrador.";
            return "Não foi possível " + (connect ? "conectar" : "desconectar") + " (erro " + err + "). Verifique se o dispositivo está ligado e por perto.";
        }

        static List<Guid> InstalledServices(ref Native.BLUETOOTH_DEVICE_INFO info)
        {
            var res = new List<Guid>();
            uint count = 32;
            var arr = new Guid[32];
            if (Native.BluetoothEnumerateInstalledServices(IntPtr.Zero, ref info, ref count, arr) == 0)
                for (int i = 0; i < count && i < arr.Length; i++) res.Add(arr[i]);
            return res;
        }

        static Dictionary<string, List<Guid>> LoadState()
        {
            var map = new Dictionary<string, List<Guid>>();
            try
            {
                if (!File.Exists(StateFile)) return map;
                foreach (string line in File.ReadAllLines(StateFile))
                {
                    string[] parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    var l = new List<Guid>();
                    foreach (string g in parts[1].Split(',')) { Guid x; if (Guid.TryParse(g, out x)) l.Add(x); }
                    map[parts[0]] = l;
                }
            }
            catch { }
            return map;
        }

        static void SaveState(Dictionary<string, List<Guid>> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                var sb = new StringBuilder();
                foreach (var kv in map)
                {
                    sb.Append(kv.Key).Append('=');
                    for (int i = 0; i < kv.Value.Count; i++) { if (i > 0) sb.Append(','); sb.Append(kv.Value[i].ToString()); }
                    sb.AppendLine();
                }
                File.WriteAllText(StateFile, sb.ToString());
            }
            catch { }
        }

        // ---------------------------------------------------------------- property helpers
        static string GetInstanceId(IntPtr set, ref Native.SP_DEVINFO_DATA info)
        {
            var sb = new StringBuilder(512);
            int req;
            return Native.SetupDiGetDeviceInstanceIdW(set, ref info, sb, sb.Capacity, out req) ? sb.ToString() : null;
        }

        static byte[] GetProp(IntPtr set, ref Native.SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            int type, req;
            var buf = new byte[256];
            DEVPROPKEY k = key;
            if (Native.SetupDiGetDevicePropertyW(set, ref info, ref k, out type, buf, buf.Length, out req, 0))
            {
                if (req <= 0) return null;
                var r = new byte[req];
                Array.Copy(buf, r, req);
                return r;
            }
            if (req > buf.Length && req < 65536)
            {
                buf = new byte[req];
                if (Native.SetupDiGetDevicePropertyW(set, ref info, ref k, out type, buf, buf.Length, out req, 0)) return buf;
            }
            return null;
        }

        static string GetString(IntPtr set, ref Native.SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            byte[] b = GetProp(set, ref info, key);
            if (b == null || b.Length < 2) return null;
            string s = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }

        static Guid GetGuid(IntPtr set, ref Native.SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            byte[] b = GetProp(set, ref info, key);
            if (b == null || b.Length < 16) return Guid.Empty;
            var g = new byte[16];
            Array.Copy(b, g, 16);
            return new Guid(g);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public int pid;
        public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; }
    }

    static class Native
    {
        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        public const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint IOCTL_KS_PROPERTY = 0x002F0003;
        public const int KSPROPERTY_TYPE_GET = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KSPROPERTY { public Guid Set; public int Id; public int Flags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public SYSTEMTIME stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        public static extern IntPtr SetupDiGetClassDevsGuid(ref Guid classGuid, string enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int req);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out int type,
                                                            byte[] buf, int size, out int req, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid iface, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size,
                                                                   out int req, ref SP_DEVINFO_DATA devInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref KSPROPERTY inBuf, int inSize,
                                                  IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern int BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref Guid service, uint flags);

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern int BluetoothEnumerateInstalledServices(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info, ref uint count,
                                                                     [Out] Guid[] guids);
    }
}
