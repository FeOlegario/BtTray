using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace BtTray
{
    /// <summary>A device the user pinned to the taskbar. Name and kind are remembered so the icon can still be shown
    /// (dimmed) while the device is off or out of range.</summary>
    class Pin
    {
        public string Address;
        public string Name;
        public DeviceKind Kind;
    }

    /// <summary>Saves the pinned devices in %LOCALAPPDATA%\BtTray\fixados.txt, one "ADDRESS\tKind\tName" per line.</summary>
    static class PinStore
    {
        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtTray", "fixados.txt"); }
        }

        public static bool Exists { get { try { return File.Exists(FilePath); } catch { return false; } } }

        public static List<Pin> Load()
        {
            var list = new List<Pin>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string[] p = line.Split(new[] { '\t' }, 3);
                    if (p.Length < 3 || p[0].Length != 12 || list.Exists(x => x.Address == p[0])) continue;
                    DeviceKind kind;
                    if (!Enum.TryParse(p[1], out kind)) kind = DeviceKind.Generic;
                    list.Add(new Pin { Address = p[0].ToUpperInvariant(), Kind = kind, Name = p[2] });
                }
            }
            catch { }
            return list;
        }

        public static void Save(List<Pin> pins)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (Pin p in pins) lines.Add(p.Address + "\t" + p.Kind + "\t" + p.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
                File.WriteAllLines(FilePath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>
    /// One extra notification-area icon per pinned device, showing its glyph and battery percentage.
    /// Uses Shell_NotifyIcon directly (instead of NotifyIcon) so each icon has a stable GUID derived from the device
    /// address: Windows then remembers, per device, whether the user chose to show it on the taskbar.
    /// </summary>
    class PinnedIcons : NativeWindow, IDisposable
    {
        public event Action OpenRequested;
        public event Action<string> UnpinRequested;

        class Slot
        {
            public string Address;
            public int Id;
            public Guid Guid;
            public bool UseGuid = true;
            public bool Added;
            public string Key;      // what the current icon shows; null forces a redraw
            public string Tip = "";
            public IntPtr HIcon;
            public System.Drawing.Icon Icon;
        }

        const int CallbackMessage = 0x8000 + 0x5B7;   // WM_APP + n
        readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        readonly List<Slot> slots = new List<Slot>();
        int nextId = 100;

        public PinnedIcons()
        {
            // a hidden top-level window (not message-only) so it also receives the TaskbarCreated broadcast
            CreateHandle(new CreateParams { Caption = "BtTray pinned icons" });
        }

        /// <summary>Forces all icons to be redrawn (e.g. after the taskbar theme changed).</summary>
        public void Invalidate()
        {
            foreach (Slot s in slots) s.Key = null;
        }

        public void Update(List<Pin> pins, List<BtDevice> devices)
        {
            for (int i = slots.Count - 1; i >= 0; i--)
                if (!pins.Exists(p => p.Address == slots[i].Address)) { Remove(slots[i]); slots.RemoveAt(i); }

            int size = SystemInformation.SmallIconSize.Width;
            bool dark = Theme.TaskbarDark();
            foreach (Pin p in pins)
            {
                Slot s = slots.Find(x => x.Address == p.Address);
                if (s == null)
                {
                    s = new Slot { Address = p.Address, Id = nextId++, Guid = GuidFor(p.Address) };
                    slots.Add(s);
                }
                BtDevice d = devices.Find(x => x.AddressHex == p.Address);
                bool connected = d != null && d.Connected;
                int battery = connected ? d.Battery : -1;
                DeviceKind kind = d != null ? d.Kind : p.Kind;
                string name = d != null ? d.Name : p.Name;

                string tip = name + "\n" + (!connected ? L.T("Desconectado", "Disconnected") : battery >= 0 ? L.T("Bateria: ", "Battery: ") + battery + "%" : L.T("Conectado", "Connected"));
                if (tip.Length > 127) tip = tip.Substring(0, 127);
                string key = size + "|" + dark + "|" + kind + "|" + connected + "|" + battery;
                if (key == s.Key && tip == s.Tip && s.Added) continue;

                if (key != s.Key)
                {
                    System.Drawing.Icon old = s.Icon;
                    s.Icon = Theme.MakeDeviceIcon(size, kind, connected, battery);
                    s.HIcon = s.Icon.Handle;
                    s.Key = key;
                    if (old != null) old.Dispose();
                }
                s.Tip = tip;
                if (s.Added) Notify(NIM_MODIFY, s);
                else Add(s);
            }
        }

        void Add(Slot s)
        {
            if (s.UseGuid)
            {
                Notify(NIM_DELETE, s);   // clears a stale registration left by a previous crash
                if (Notify(NIM_ADD, s)) { s.Added = true; return; }
                s.UseGuid = false;       // GUID refused (e.g. registered to another path): fall back to the id
            }
            s.Added = Notify(NIM_ADD, s);
        }

        void Remove(Slot s)
        {
            if (s.Added) Notify(NIM_DELETE, s);
            s.Added = false;
            if (s.Icon != null) { s.Icon.Dispose(); s.Icon = null; }
        }

        bool Notify(int msg, Slot s)
        {
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = Handle;
            d.uID = s.Id;
            d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | (s.UseGuid ? NIF_GUID : 0);
            d.uCallbackMessage = CallbackMessage;
            d.hIcon = s.HIcon;
            d.szTip = s.Tip ?? "";
            d.guidItem = s.Guid;
            try { return Shell_NotifyIcon(msg, ref d); } catch { return false; }
        }

        // Stable per device and per executable path: an unsigned app's icon GUID is tied to the path it first ran from.
        static Guid GuidFor(string address)
        {
            string seed = "BtTray|" + Application.ExecutablePath.ToLowerInvariant() + "|" + address;
            byte[] h;
            using (var md5 = MD5.Create()) h = md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
            h[7] = (byte)((h[7] & 0x0F) | 0x30);   // version 3 (name based)
            h[8] = (byte)((h[8] & 0x3F) | 0x80);   // RFC 4122 variant
            return new Guid(h);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMessage)
            {
                int id = (int)(long)m.WParam;
                int ev = (int)((long)m.LParam & 0xFFFF);
                Slot s = slots.Find(x => x.Id == id);
                if (s != null)
                {
                    if (ev == WM_LBUTTONUP) { if (OpenRequested != null) OpenRequested(); }
                    else if (ev == WM_RBUTTONUP) ShowMenu(s);
                }
                return;
            }
            if (m.Msg == taskbarCreated && taskbarCreated != 0)
            {
                // Explorer restarted: every icon is gone and must be added again
                foreach (Slot s in slots) { s.Added = false; Add(s); }
            }
            base.WndProc(ref m);
        }

        void ShowMenu(Slot s)
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendMenu(menu, 0, 1, L.T("Abrir", "Open"));
                AppendMenu(menu, 0, 2, L.T("Desafixar da barra de tarefas", "Unpin from taskbar"));
                SetMenuDefaultItem(menu, 1, 0);
                POINT pt;
                GetCursorPos(out pt);
                SetForegroundWindow(Handle);   // so the menu closes when clicking elsewhere
                int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, Handle, IntPtr.Zero);
                PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
                if (cmd == 1 && OpenRequested != null) OpenRequested();
                else if (cmd == 2 && UnpinRequested != null) UnpinRequested(s.Address);
            }
            finally { DestroyMenu(menu); }
        }

        public void Dispose()
        {
            foreach (Slot s in slots) Remove(s);
            slots.Clear();
            DestroyHandle();
        }

        // ---------------------------------------------------------------- native
        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20;
        const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
        const int TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, int flags, int id, string text);
        [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPos);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    }
}
