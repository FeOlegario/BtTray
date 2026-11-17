using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("BtTray - Bluetooth na bandeja")]
[assembly: System.Reflection.AssemblyProduct("BtTray")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace BtTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\BtTray_Show", out created))
            {
                if (!created) { showEvent.Set(); return; }   // already running: ask it to show the flyout
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(showEvent));
            }
        }

        public static void OpenBluetoothSettings()
        {
            try { Process.Start("ms-settings:bluetooth"); } catch { }
        }
    }

    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "BtTray";

        class Pending { public bool Target; public DateTime Deadline; }

        readonly NotifyIcon tray = new NotifyIcon();
        readonly PopupForm popup = new PopupForm();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer();
        readonly MenuItem startupItem;

        List<BtDevice> devices = new List<BtDevice>();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        readonly Dictionary<string, string> busyText = new Dictionary<string, string>();
        readonly Dictionary<string, string> errors = new Dictionary<string, string>();
        readonly Dictionary<string, DateTime> errorUntil = new Dictionary<string, DateTime>();

        readonly BatteryAlerts batteryAlerts = new BatteryAlerts();
        readonly Queue<BatteryAlert> alertQueue = new Queue<BatteryAlert>();
        readonly System.Windows.Forms.Timer alertTimer = new System.Windows.Forms.Timer();

        int refreshing;            // 0/1, interlocked
        volatile bool refreshAgain;
        bool? iconConnected;

        public TrayApp(WaitHandle showEvent)
        {
            IntPtr force = popup.Handle;  // create handle so BeginInvoke works before first show
            ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
            {
                try { popup.BeginInvoke((MethodInvoker)ShowPopup); } catch { }
            }, null, Timeout.Infinite, false);
            popup.ToggleRequested += Toggle;
            popup.RefreshRequested += RequestRefresh;
            popup.VisibleChanged += delegate
            {
                if (popup.Visible) { trim.Stop(); RequestRefresh(); }
                else trim.Start();
                UpdatePollInterval();
            };
            popup.DeviceChanged += delegate { debounce.Stop(); debounce.Start(); };

            startupItem = new MenuItem("Iniciar com o Windows", delegate { SetStartup(!startupItem.Checked); });
            startupItem.Checked = IsStartup();
            var menu = new ContextMenu(new[]
            {
                new MenuItem("Abrir", delegate { ShowPopup(); }) ,
                new MenuItem("Atualizar", delegate { RequestRefresh(); }),
                startupItem,
                new MenuItem("-"),
                new MenuItem("Configurações de Bluetooth", delegate { Program.OpenBluetoothSettings(); }),
                new MenuItem("Sair", delegate { ExitThread(); })
            });
            menu.MenuItems[0].DefaultItem = true;

            tray.ContextMenu = menu;
            tray.Text = "Bluetooth";
            SetTrayIcon(false);
            tray.MouseClick += delegate (object o, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (popup.Visible) { popup.Hide(); return; }
                // clicking the icon while the flyout is open first deactivates (hides) it; don't reopen it
                if ((DateTime.Now - popup.LastHidden).TotalMilliseconds < 250) return;
                ShowPopup();
            };
            tray.Visible = true;

            poll.Tick += delegate { RequestRefresh(); };
            debounce.Interval = 700;
            debounce.Tick += delegate { debounce.Stop(); RequestRefresh(); };
            trim.Interval = 2000;
            trim.Tick += delegate { trim.Stop(); TrimMemory(); };
            alertTimer.Interval = 7000;   // spacing between notifications so one doesn't replace another
            alertTimer.Tick += delegate { ShowNextAlert(); };
            UpdatePollInterval();
            poll.Start();

            SystemEvents.UserPreferenceChanged += OnPrefsChanged;
            RequestRefresh();
            trim.Start();
        }

        void OnPrefsChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
            {
                iconConnected = null;
                popup.BeginInvoke((MethodInvoker)delegate { SetTrayIcon(AnyConnected()); });
            }
        }

        void ShowPopup()
        {
            popup.SetDevices(devices, busyText, errors);
            popup.ShowNearTray();
        }

        // ---------------------------------------------------------------- refresh (off the UI thread)
        void RequestRefresh()
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1) { refreshAgain = true; return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<BtDevice> list;
                try { list = Bluetooth.Enumerate(); }
                catch { list = null; }
                try
                {
                    popup.BeginInvoke((MethodInvoker)delegate
                    {
                        Interlocked.Exchange(ref refreshing, 0);
                        if (list != null) Apply(list);
                        if (refreshAgain) { refreshAgain = false; RequestRefresh(); }
                    });
                }
                catch { Interlocked.Exchange(ref refreshing, 0); }
            });
        }

        void Apply(List<BtDevice> list)
        {
            devices = list;
            DateTime now = DateTime.Now;

            foreach (string addr in new List<string>(pending.Keys))
            {
                Pending p = pending[addr];
                BtDevice d = devices.Find(x => x.AddressHex == addr);
                if (d != null && d.Connected == p.Target) { pending.Remove(addr); busyText.Remove(addr); }
                else if (now > p.Deadline)
                {
                    pending.Remove(addr); busyText.Remove(addr);
                    SetError(addr, p.Target ? "Não conectou. Ele está ligado e por perto?" : "Não foi possível desconectar.");
                }
            }
            foreach (string addr in new List<string>(errorUntil.Keys))
                if (now > errorUntil[addr]) { errorUntil.Remove(addr); errors.Remove(addr); }

            popup.SetDevices(devices, busyText, errors);
            UpdateTooltip();
            SetTrayIcon(AnyConnected());
            UpdatePollInterval();

            foreach (BatteryAlert a in batteryAlerts.Check(devices)) alertQueue.Enqueue(a);
            if (alertQueue.Count > 0 && !alertTimer.Enabled) ShowNextAlert();
        }

        void ShowNextAlert()
        {
            if (alertQueue.Count == 0) { alertTimer.Stop(); return; }
            BatteryAlert a = alertQueue.Dequeue();
            string title = a.Title.Length > 63 ? a.Title.Substring(0, 62) + "…" : a.Title;
            tray.ShowBalloonTip(10000, title, a.Message, a.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
            alertTimer.Start();
        }

        bool AnyConnected() { return devices.Exists(d => d.Connected); }

        void UpdatePollInterval()
        {
            int iv = (popup.Visible || pending.Count > 0) ? 1500 : 60000;
            if (poll.Interval != iv) poll.Interval = iv;
        }

        // ---------------------------------------------------------------- connect / disconnect
        void Toggle(BtDevice d)
        {
            bool target = !d.Connected;
            string addr = d.AddressHex;
            pending[addr] = new Pending { Target = target, Deadline = DateTime.Now.AddSeconds(target ? 20 : 12) };
            busyText[addr] = target ? "Conectando…" : "Desconectando…";
            errors.Remove(addr); errorUntil.Remove(addr);
            popup.SetDevices(devices, busyText, errors);
            UpdatePollInterval();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                try { err = target ? Bluetooth.Connect(d) : Bluetooth.Disconnect(d); }
                catch (Exception ex) { err = ex.Message; }
                popup.BeginInvoke((MethodInvoker)delegate
                {
                    if (err != null)
                    {
                        pending.Remove(addr); busyText.Remove(addr);
                        SetError(addr, err);
                        tray.ShowBalloonTip(5000, d.Name, err, ToolTipIcon.Warning);
                        popup.SetDevices(devices, busyText, errors);
                    }
                    RequestRefresh();
                });
            });
        }

        void SetError(string addr, string msg)
        {
            errors[addr] = msg;
            errorUntil[addr] = DateTime.Now.AddSeconds(8);
        }

        // ---------------------------------------------------------------- tray icon & tooltip
        void SetTrayIcon(bool connected)
        {
            if (iconConnected == connected) return;
            iconConnected = connected;
            Icon old = tray.Icon;
            tray.Icon = Theme.MakeTrayIcon(SystemInformation.SmallIconSize.Width, connected);
            if (old != null) old.Dispose();
        }

        void UpdateTooltip()
        {
            string text = "Bluetooth";
            foreach (BtDevice d in devices)
            {
                if (!d.Connected) continue;
                string line = "\n" + d.Name + (d.Battery >= 0 ? " · " + d.Battery + "%" : "");
                if (text.Length + line.Length > 63) break;
                text += line;
            }
            if (text == "Bluetooth") text = devices.Count == 0 ? "Bluetooth: nenhum dispositivo" : "Bluetooth: nada conectado";
            if (tray.Text != text) tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // ---------------------------------------------------------------- startup with Windows
        static bool IsStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                startupItem.Checked = on;
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(4000, "BtTray", "Não foi possível alterar a inicialização: " + ex.Message, ToolTipIcon.Error);
            }
        }

        // ---------------------------------------------------------------- memory
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnPrefsChanged;
            tray.Visible = false;
            tray.Dispose();
            poll.Dispose(); debounce.Dispose(); trim.Dispose(); alertTimer.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }
    }
}
