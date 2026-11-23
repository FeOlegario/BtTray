using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace BtTray
{
    /// <summary>A nearby Bluetooth device that is not paired yet.</summary>
    class FoundDevice
    {
        public string Id;
        public string Name;
        public DeviceKind Kind;
        public bool IsLE;
        public int Signal = int.MinValue;   // dBm, when Windows reports it
        // DeviceInformation, kept as object so that loading this type does not pull in WinRT
        internal object Info;
    }

    /// <summary>
    /// Finds nearby unpaired devices (Bluetooth Classic and LE) and pairs them, through the same WinRT API used by the
    /// Windows Settings app. All WinRT code lives in this file, so it is only loaded when the search is opened.
    /// Every event is raised on the UI thread of the control passed to the constructor.
    /// </summary>
    class DeviceDiscovery : IDisposable
    {
        /// <summary>The list or the searching state changed.</summary>
        public event Action Changed;

        /// <summary>
        /// How long a search runs. Windows only reports Bluetooth Classic devices when its inquiry ends, about 30 s in,
        /// so the search must last longer than that; it is not unlimited because scanning keeps the radio busy and can
        /// make connected headphones stutter.
        /// </summary>
        public const int SearchSeconds = 45;

        const string PIsPresent = "System.Devices.Aep.IsPresent", PSignal = "System.Devices.Aep.SignalStrength",
                     PCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major", PCodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor",
                     PLeCategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Category",
                     PLeSubcategory = "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory";
        static readonly string[] Props = { PIsPresent, PSignal, PCodMajor, PCodMinor, PLeCategory, PLeSubcategory };

        readonly Control ui;
        readonly Dictionary<string, FoundDevice> found = new Dictionary<string, FoundDevice>();
        readonly List<DeviceWatcher> watchers = new List<DeviceWatcher>();
        readonly Timer timeout = new Timer();
        int generation;      // events from watchers of an earlier search are ignored

        public bool Searching { get; private set; }

        public DeviceDiscovery(Control ui)
        {
            this.ui = ui;
            timeout.Interval = SearchSeconds * 1000;
            timeout.Tick += delegate { Stop(); };
        }

        /// <summary>Found devices, strongest signal first, then by name.</summary>
        public List<FoundDevice> Devices
        {
            get
            {
                var list = new List<FoundDevice>(found.Values);
                list.Sort(delegate (FoundDevice a, FoundDevice b)
                {
                    if (a.Signal != b.Signal) return b.Signal.CompareTo(a.Signal);
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                return list;
            }
        }

        public void Start()
        {
            StopWatchers();
            found.Clear();
            int gen = ++generation;
            Watch(BluetoothDevice.GetDeviceSelectorFromPairingState(false), false, gen);
            Watch(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), true, gen);
            Searching = true;
            timeout.Stop();
            timeout.Start();
            Raise();
        }

        public void Stop()
        {
            timeout.Stop();
            StopWatchers();
            if (!Searching) return;
            Searching = false;
            Raise();
        }

        public void Remove(string id)
        {
            if (found.Remove(id)) Raise();
        }

        void Watch(string selector, bool le, int gen)
        {
            DeviceWatcher w = DeviceInformation.CreateWatcher(selector, Props, DeviceInformationKind.AssociationEndpoint);
            w.Added += (s, info) => OnUi(gen, delegate { AddOrUpdate(info, le); });
            w.Updated += (s, upd) => OnUi(gen, delegate { Update(upd); });
            w.Removed += (s, upd) => OnUi(gen, delegate { Remove(upd.Id); });
            watchers.Add(w);
            try { w.Start(); } catch { }
        }

        void StopWatchers()
        {
            foreach (DeviceWatcher w in watchers)
                try
                {
                    if (w.Status == DeviceWatcherStatus.Started || w.Status == DeviceWatcherStatus.EnumerationCompleted) w.Stop();
                }
                catch { }
            watchers.Clear();
            generation++;
        }

        void OnUi(int gen, MethodInvoker action)
        {
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate { if (gen == generation) action(); });
            }
            catch { }   // window already gone
        }

        void AddOrUpdate(DeviceInformation info, bool le)
        {
            // nameless entries are mostly beacons and other devices that cannot be paired from here
            if (string.IsNullOrEmpty(info.Name) || !info.Pairing.CanPair) return;
            var f = new FoundDevice { Id = info.Id, Name = info.Name, IsLE = le, Info = info };
            f.Kind = le ? KindFromAppearance(info.Properties) : KindFromCod(info.Properties);
            object present;
            if (le && info.Properties.TryGetValue(PIsPresent, out present) && present is bool && !(bool)present) return;
            f.Signal = SignalOf(info.Properties, f.Signal);
            found[f.Id] = f;
            Raise();
        }

        void Update(DeviceInformationUpdate upd)
        {
            FoundDevice f;
            if (!found.TryGetValue(upd.Id, out f)) return;
            object present;
            if (upd.Properties.TryGetValue(PIsPresent, out present) && present is bool && !(bool)present) { Remove(upd.Id); return; }
            int signal = SignalOf(upd.Properties, f.Signal);
            if (signal == f.Signal) return;
            f.Signal = signal;
            Raise();
        }

        static int SignalOf(IReadOnlyDictionary<string, object> props, int fallback)
        {
            object v;
            if (props.TryGetValue(PSignal, out v) && v != null)
                try { return Convert.ToInt32(v); } catch { }
            return fallback;
        }

        static DeviceKind KindFromCod(IReadOnlyDictionary<string, object> props)
        {
            object major, minor;
            if (!props.TryGetValue(PCodMajor, out major) || major == null) return DeviceKind.Generic;
            props.TryGetValue(PCodMinor, out minor);
            uint cod = (Convert.ToUInt32(major) << 8) | ((minor == null ? 0u : Convert.ToUInt32(minor)) << 2);
            return Bluetooth.KindFromCod(cod);
        }

        // Bluetooth LE GAP appearance: category / subcategory
        static DeviceKind KindFromAppearance(IReadOnlyDictionary<string, object> props)
        {
            object cat, sub;
            if (!props.TryGetValue(PLeCategory, out cat) || cat == null) return DeviceKind.Generic;
            props.TryGetValue(PLeSubcategory, out sub);
            int c = Convert.ToInt32(cat), s = sub == null ? 0 : Convert.ToInt32(sub);
            switch (c)
            {
                case 0x01: return DeviceKind.Phone;
                case 0x02: return DeviceKind.Computer;
                case 0x0F:   // human interface device
                    if (s == 1) return DeviceKind.Keyboard;
                    if (s == 2) return DeviceKind.Mouse;
                    if (s == 3 || s == 4) return DeviceKind.Gamepad;
                    return DeviceKind.Generic;
                case 0x21: return DeviceKind.Speaker;      // audio sink
                case 0x25: return DeviceKind.Headphones;   // wearable audio device (earbuds, headset)
            }
            return DeviceKind.Generic;
        }

        void Raise()
        {
            if (Changed != null) Changed();
        }

        // ---------------------------------------------------------------- pairing
        /// <summary>
        /// Pairs the device. done(null) on success, otherwise done(message) with a user-facing reason; always on the UI thread.
        /// Confirmation and PIN comparison are asked with a message box; devices that need a typed PIN are not supported.
        /// </summary>
        public void Pair(FoundDevice f, Action<string> done)
        {
            var info = (DeviceInformation)f.Info;
            DeviceInformationCustomPairing custom = info.Pairing.Custom;
            TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler =
                (s, args) => OnPairingRequested(f.Name, args);
            custom.PairingRequested += handler;

            IAsyncOperation<DevicePairingResult> op;
            try
            {
                op = custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin);
            }
            catch (Exception ex)
            {
                custom.PairingRequested -= handler;
                done(ex.Message);
                return;
            }
            op.Completed = (o, st) =>
            {
                string err;
                try
                {
                    custom.PairingRequested -= handler;
                    err = st == AsyncStatus.Completed ? Message(o.GetResults().Status)
                                                      : L.T("Não foi possível emparelhar.", "Could not pair.");
                }
                catch (Exception ex) { err = ex.Message; }
                try { ui.BeginInvoke((MethodInvoker)delegate { done(err); }); } catch { }
            };
        }

        // Runs on a WinRT thread; questions go to the UI thread while a deferral keeps the pairing waiting.
        void OnPairingRequested(string name, DevicePairingRequestedEventArgs args)
        {
            if (args.PairingKind == DevicePairingKinds.ConfirmOnly) { args.Accept(); return; }

            Deferral deferral = args.GetDeferral();
            string pin = args.Pin;
            string text = args.PairingKind == DevicePairingKinds.DisplayPin
                ? L.T("Digite este PIN no dispositivo \"" + name + "\":\n\n" + pin, "Enter this PIN on \"" + name + "\":\n\n" + pin)
                : L.T("O PIN mostrado no dispositivo \"" + name + "\" é este?\n\n" + pin, "Does \"" + name + "\" show this PIN?\n\n" + pin);
            MessageBoxButtons buttons = args.PairingKind == DevicePairingKinds.DisplayPin ? MessageBoxButtons.OKCancel : MessageBoxButtons.YesNo;
            try
            {
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        DialogResult r = MessageBox.Show(text, L.T("Emparelhar dispositivo", "Pair device"), buttons,
                                                         MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                                                         MessageBoxOptions.DefaultDesktopOnly);
                        if (r == DialogResult.OK || r == DialogResult.Yes) args.Accept();
                    }
                    finally { deferral.Complete(); }
                });
            }
            catch { deferral.Complete(); }
        }

        static string Message(DevicePairingResultStatus s)
        {
            switch (s)
            {
                case DevicePairingResultStatus.Paired:
                case DevicePairingResultStatus.AlreadyPaired:
                    return null;
                case DevicePairingResultStatus.AuthenticationTimeout:
                case DevicePairingResultStatus.NotReadyToPair:
                case DevicePairingResultStatus.ConnectionRejected:
                    return L.T("O dispositivo não respondeu. Ele está em modo de emparelhamento?",
                               "The device did not respond. Is it in pairing mode?");
                case DevicePairingResultStatus.PairingCanceled:
                case DevicePairingResultStatus.RejectedByHandler:
                    return L.T("Emparelhamento cancelado.", "Pairing canceled.");
                case DevicePairingResultStatus.RequiredHandlerNotRegistered:
                    return L.T("Este dispositivo pede um PIN digitado. Use as Configurações de Bluetooth.",
                               "This device needs a typed PIN. Use the Bluetooth settings.");
                case DevicePairingResultStatus.AccessDenied:
                case DevicePairingResultStatus.AuthenticationNotAllowed:
                    return L.T("O Windows não permitiu o emparelhamento.", "Windows did not allow the pairing.");
                case DevicePairingResultStatus.OperationAlreadyInProgress:
                    return L.T("Já existe um emparelhamento em andamento.", "A pairing is already in progress.");
            }
            return L.T("Não foi possível emparelhar (" + s + ").", "Could not pair (" + s + ").");
        }

        public void Dispose()
        {
            Stop();
            timeout.Dispose();
        }
    }
}
