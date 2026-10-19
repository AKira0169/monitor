// MonitorSwitch - tiny tray app to view monitors and switch their input over DDC/CI.
// Build with build.cmd (uses the C# 5 compiler that ships with .NET Framework 4.x).
// Settings: %APPDATA%\MonitorSwitch\settings.ini
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "MonitorSwitch.SingleInstance", out created))
        {
            if (!created)
            {
                // Already running: ask it to show its window.
                Native.PostMessage((IntPtr)Native.HWND_BROADCAST, Native.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var app = new TrayApp(args.Contains("/hidden"));
            Application.Run();
            GC.KeepAlive(app);
        }
    }
}

// ---------------------------------------------------------------- native

static class Native
{
    public const int HWND_BROADCAST = 0xffff;
    public static readonly int WM_SHOWME = RegisterWindowMessage("MonitorSwitch.ShowMe");
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    [DllImport("dxva2.dll")] public static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder s, uint len);
}

// ---------------------------------------------------------------- monitors / DDC

class MonitorInfo
{
    public string ShortId;      // e.g. ACR075A - stable across reboots, used for hotkeys
    public string Name;         // friendly name from EDID (WMI) or driver description
    public string Device;       // \\.\DISPLAY1
    public string Adapter;
    public int Width, Height, Hz, X, Y;
    public bool Primary;
    public bool Loaded;         // DDC values were read (false on the quick first pass)
    public bool DdcOk;
    public int Input = -1;
    public int Brightness = -1, BrightnessMax = 100;
    public List<int> Inputs = new List<int>();
}

static class Ddc
{
    public const byte VCP_INPUT = 0x60, VCP_BRIGHTNESS = 0x10;
    static readonly object Lock = new object();   // DDC/I2C is slow and must not overlap
    static readonly Dictionary<string, List<int>> CapsCache = new Dictionary<string, List<int>>();
    static Dictionary<string, string> friendlyNames;

    public static string InputName(int code)
    {
        switch (code)
        {
            case 0x01: return "VGA 1"; case 0x02: return "VGA 2";
            case 0x03: return "DVI 1"; case 0x04: return "DVI 2";
            case 0x0F: return "DisplayPort 1"; case 0x10: return "DisplayPort 2";
            case 0x11: return "HDMI 1"; case 0x12: return "HDMI 2";
            case 0x1B: return "USB-C";
            default: return "Input " + code;
        }
    }

    // Visits every physical monitor with its handle open; handles are destroyed afterwards.
    static void ForEach(Action<MonitorInfo, IntPtr> visit)
    {
        var hmons = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) => { hmons.Add(h); return true; }, IntPtr.Zero);
        foreach (var hMon in hmons)
        {
            var mi = new Native.MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(mi);
            if (!Native.GetMonitorInfo(hMon, ref mi)) continue;

            // Monitor device ids under this display, in the same order as the physical monitors.
            var ids = new List<string>(); var adapter = "";
            var ad = new Native.DISPLAY_DEVICE(); ad.cb = Marshal.SizeOf(ad);
            for (uint a = 0; Native.EnumDisplayDevices(null, a, ref ad, 0); a++)
                if (ad.DeviceName == mi.szDevice) { adapter = ad.DeviceString; break; }
            var md = new Native.DISPLAY_DEVICE(); md.cb = Marshal.SizeOf(md);
            for (uint i = 0; Native.EnumDisplayDevices(mi.szDevice, i, ref md, 0); i++)
                if ((md.StateFlags & 1) != 0) ids.Add(md.DeviceID);   // DISPLAY_DEVICE_ACTIVE

            var dm = new Native.DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(dm);
            Native.EnumDisplaySettings(mi.szDevice, -1, ref dm);   // ENUM_CURRENT_SETTINGS

            uint n;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0) continue;
            var phys = new Native.PHYSICAL_MONITOR[n];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMon, n, phys)) continue;
            try
            {
                for (int p = 0; p < n; p++)
                {
                    var m = new MonitorInfo();
                    m.Device = mi.szDevice.Replace(@"\\.\", "");
                    m.Adapter = adapter;
                    m.Primary = (mi.dwFlags & 1) != 0;
                    m.X = mi.rcMonitor.Left; m.Y = mi.rcMonitor.Top;
                    m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight; m.Hz = dm.dmDisplayFrequency;
                    var devId = p < ids.Count ? ids[p] : "";
                    var parts = devId.Split('\\');
                    m.ShortId = parts.Length > 1 ? parts[1] : m.Device;
                    m.Name = phys[p].szPhysicalMonitorDescription;
                    visit(m, phys[p].hPhysicalMonitor);
                }
            }
            finally { Native.DestroyPhysicalMonitors(n, phys); }
        }
    }

    public static List<MonitorInfo> List(bool withDetails)
    {
        var result = new List<MonitorInfo>();
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                m.Name = FriendlyName(m.ShortId) ?? m.Name;
                m.Loaded = withDetails;
                if (withDetails)
                {
                    uint cur, max;
                    if (Native.GetVCPFeatureAndVCPFeatureReply(h, VCP_INPUT, IntPtr.Zero, out cur, out max))
                    { m.DdcOk = true; m.Input = (int)(cur & 0xFF); }
                    if (Native.GetVCPFeatureAndVCPFeatureReply(h, VCP_BRIGHTNESS, IntPtr.Zero, out cur, out max))
                    { m.DdcOk = true; m.Brightness = (int)cur; m.BrightnessMax = (int)Math.Max(1, max); }
                    if (m.DdcOk) m.Inputs = Inputs(m.ShortId, h);
                    if (m.Input > 0 && !m.Inputs.Contains(m.Input)) m.Inputs.Insert(0, m.Input);
                }
                result.Add(m);
            });
        }
        return result.OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
    }

    // Changes brightness on every DDC monitor by delta; returns the new value of the first one, or -1.
    public static int AdjustBrightness(int delta)
    {
        int result = -1;
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                uint cur, max;
                if (!Native.GetVCPFeatureAndVCPFeatureReply(h, VCP_BRIGHTNESS, IntPtr.Zero, out cur, out max)) return;
                int v = Math.Max(0, Math.Min((int)max, (int)cur + delta));
                if (Native.SetVCPFeature(h, VCP_BRIGHTNESS, (uint)v) && result < 0) result = v * 100 / (int)Math.Max(1, max);
            });
        }
        return result;
    }

    // Sets a VCP value on the monitor with the given short id (or on all monitors if id is "*").
    public static string Set(string shortId, byte code, int value)
    {
        string error = "Monitor " + shortId + " not found (is it showing this PC?)";
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                if (shortId != "*" && !string.Equals(m.ShortId, shortId, StringComparison.OrdinalIgnoreCase)) return;
                error = Native.SetVCPFeature(h, code, (uint)value) ? null : "DDC/CI write failed on " + m.ShortId;
            });
        }
        return error;
    }

    static List<int> Inputs(string shortId, IntPtr h)
    {
        List<int> cached;
        if (CapsCache.TryGetValue(shortId, out cached) || Settings.InputCache.TryGetValue(shortId, out cached))
            return new List<int>(cached);
        var list = new List<int>();
        uint len;
        if (Native.GetCapabilitiesStringLength(h, out len) && len > 0)
        {
            var sb = new StringBuilder((int)len);
            if (Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len))
            {
                // e.g. "...vcp(02 04 10 12 60(0F 11 12) 62 ...)"
                var mt = Regex.Match(sb.ToString(), @"\b60\s*\(([0-9A-Fa-f\s]+)\)");
                if (mt.Success)
                    foreach (var hex in mt.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        list.Add(Convert.ToInt32(hex, 16));
            }
        }
        CapsCache[shortId] = list;
        if (list.Count > 0) { Settings.InputCache[shortId] = list; Settings.Save(); }   // the capabilities query takes seconds
        return new List<int>(list);
    }

    static string FriendlyName(string shortId)
    {
        if (friendlyNames == null)
        {
            friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var parts = ((string)o["InstanceName"]).Split('\\');
                        var raw = o["UserFriendlyName"] as ushort[];
                        if (parts.Length < 2 || raw == null) continue;
                        var name = new string(raw.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                        if (name.Length > 0) friendlyNames[parts[1]] = name;
                    }
            }
            catch { }
        }
        string n;
        return friendlyNames.TryGetValue(shortId, out n) ? n : null;
    }
}

// ---------------------------------------------------------------- settings / hotkeys

class Binding
{
    public Keys Key;          // key code | modifier flags
    public string MonitorId;  // short id, or * for all
    public int Input;
}

static class Settings
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitch");
    static readonly string FilePath = Path.Combine(Dir, "settings.ini");
    public static Keys ShowKey = Keys.Control | Keys.Alt | Keys.D0;
    public static int BrightnessStep = 10;   // Ctrl+Alt+Up/Down; Left/Right use a fine step of 2
    public static List<Binding> Bindings = new List<Binding>();
    public static Dictionary<string, string> Labels = new Dictionary<string, string>();   // "ACR075A:15" -> "Desktop"
    public static Dictionary<string, List<int>> InputCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

    public static void Load()
    {
        Bindings.Clear(); Labels.Clear(); InputCache.Clear();
        if (!File.Exists(FilePath))
        {
            // Defaults matching the old Start Menu shortcuts.
            Bindings.Add(new Binding { Key = Keys.Control | Keys.Alt | Keys.D3, MonitorId = "ACR075A", Input = 15 });
            Bindings.Add(new Binding { Key = Keys.Control | Keys.Alt | Keys.D4, MonitorId = "ACR075A", Input = 17 });
            Save();
            return;
        }
        // Lines: show=Ctrl+Alt+M | hotkey=Ctrl+Alt+3,ACR075A,15 | label=ACR075A,15,Desktop
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            var eq = line.IndexOf('='); if (eq < 0) continue;
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var val = line.Substring(eq + 1).Trim();
            var f = val.Split(',').Select(s => s.Trim()).ToArray();
            try
            {
                if (key == "show") ShowKey = ParseKeys(val);
                else if (key == "brightness_step") BrightnessStep = Math.Max(1, int.Parse(val));
                else if (key == "hotkey" && f.Length == 3)
                    Bindings.Add(new Binding { Key = ParseKeys(f[0]), MonitorId = f[1], Input = int.Parse(f[2]) });
                else if (key == "label" && f.Length >= 3)
                    Labels[f[0] + ":" + f[1]] = string.Join(",", f.Skip(2));
                else if (key == "inputs" && f.Length >= 2)
                    InputCache[f[0]] = f.Skip(1).Select(int.Parse).ToList();
            }
            catch { }
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Dir);
        var sb = new StringBuilder();
        sb.AppendLine("# MonitorSwitch settings. Input codes: 15=DisplayPort 1, 16=DisplayPort 2, 17=HDMI 1, 18=HDMI 2");
        sb.AppendLine("show=" + FormatKeys(ShowKey));
        sb.AppendLine("brightness_step=" + BrightnessStep);
        foreach (var b in Bindings) sb.AppendLine("hotkey=" + FormatKeys(b.Key) + "," + b.MonitorId + "," + b.Input);
        foreach (var kv in Labels)
        {
            var p = kv.Key.Split(':');
            sb.AppendLine("label=" + p[0] + "," + p[1] + "," + kv.Value);
        }
        foreach (var kv in InputCache) sb.AppendLine("inputs=" + kv.Key + "," + string.Join(",", kv.Value));
        File.WriteAllText(FilePath, sb.ToString());
    }

    public static string Label(string id, int input)
    {
        string l;
        return Labels.TryGetValue(id + ":" + input, out l) ? l : null;
    }

    public static string FormatKeys(Keys k)
    {
        var s = new List<string>();
        if ((k & Keys.Control) != 0) s.Add("Ctrl");
        if ((k & Keys.Alt) != 0) s.Add("Alt");
        if ((k & Keys.Shift) != 0) s.Add("Shift");
        var code = k & Keys.KeyCode;
        if (code >= Keys.D0 && code <= Keys.D9) s.Add(((int)(code - Keys.D0)).ToString());
        else if (code != Keys.None) s.Add(code.ToString());
        return string.Join("+", s);
    }

    public static Keys ParseKeys(string text)
    {
        Keys k = Keys.None;
        foreach (var part in text.Split('+').Select(p => p.Trim()))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": k |= Keys.Control; break;
                case "alt": k |= Keys.Alt; break;
                case "shift": k |= Keys.Shift; break;
                default:
                    int d;
                    if (part.Length == 1 && int.TryParse(part, out d)) k |= Keys.D0 + d;
                    else k |= (Keys)Enum.Parse(typeof(Keys), part, true);
                    break;
            }
        }
        return k;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartWithWindows
    {
        get { using (var r = Registry.CurrentUser.OpenSubKey(RunKey)) return r != null && r.GetValue("MonitorSwitch") != null; }
        set
        {
            using (var r = Registry.CurrentUser.CreateSubKey(RunKey))
                if (value) r.SetValue("MonitorSwitch", "\"" + Application.ExecutablePath + "\"");
                else r.DeleteValue("MonitorSwitch", false);
        }
    }
}

// Long-lived hidden window that owns the global hotkeys (survives the main form closing).
class HotkeyWindow : NativeWindow, IDisposable
{
    readonly List<int> ids = new List<int>();
    public const int ShowId = 0, BrightnessBaseId = 100;   // 1..N = Settings.Bindings
    public static readonly Keys[] BrightnessKeys = { Keys.Up, Keys.Down, Keys.Right, Keys.Left };
    public event Action<int> Pressed;   // hotkey id
    public List<string> Failed = new List<string>();

    public HotkeyWindow() { CreateHandle(new CreateParams()); }

    public void Register()
    {
        foreach (var id in ids) Native.UnregisterHotKey(Handle, id);
        ids.Clear(); Failed.Clear();
        Add(ShowId, Settings.ShowKey, true);
        for (int i = 0; i < Settings.Bindings.Count; i++) Add(i + 1, Settings.Bindings[i].Key, true);
        // Brightness keys auto-repeat while held.
        for (int i = 0; i < BrightnessKeys.Length; i++) Add(BrightnessBaseId + i, Keys.Control | Keys.Alt | BrightnessKeys[i], false);
    }

    void Add(int id, Keys k, bool noRepeat)
    {
        uint mods = noRepeat ? Native.MOD_NOREPEAT : 0;
        if ((k & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((k & Keys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((k & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if (Native.RegisterHotKey(Handle, id, mods, (uint)(k & Keys.KeyCode))) ids.Add(id);
        else Failed.Add(Settings.FormatKeys(k));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
        else base.WndProc(ref m);
    }

    public void Dispose() { foreach (var id in ids) Native.UnregisterHotKey(Handle, id); DestroyHandle(); }
}

// ---------------------------------------------------------------- tray app

class TrayApp
{
    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkeys;
    readonly Control ui = new Control();   // marshals worker-thread results back to the UI thread
    MainForm form;

    public TrayApp(bool hidden)
    {
        ui.CreateControl();
        Settings.Load();
        // Keep the startup entry pointing at wherever this exe lives now (it may have been moved).
        if (Settings.StartWithWindows) Settings.StartWithWindows = true;
        hotkeys = new HotkeyWindow();
        hotkeys.Pressed += OnHotkey;
        hotkeys.Register();

        tray = new NotifyIcon { Icon = AppIcon.Make(), Text = "MonitorSwitch", Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
        BuildMenu();

        // Listener for a second instance asking us to show.
        new ShowListener(ShowWindow);

        if (!hidden) ShowWindow();
        if (hotkeys.Failed.Count > 0)
            tray.ShowBalloonTip(4000, "MonitorSwitch", "Hotkey already in use: " + string.Join(", ", hotkeys.Failed), ToolTipIcon.Warning);
    }

    void BuildMenu()
    {
        var menu = tray.ContextMenuStrip; menu.Items.Clear();
        menu.Items.Add("Open", null, (s, e) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var b in Settings.Bindings)
        {
            var bb = b;
            var text = (Settings.Label(b.MonitorId, b.Input) ?? Ddc.InputName(b.Input)) + "   (" + Settings.FormatKeys(b.Key) + ")";
            menu.Items.Add(text, null, (s, e) => Switch(bb.MonitorId, bb.Input));
        }
        menu.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = Settings.StartWithWindows };
        auto.Click += (s, e) => Settings.StartWithWindows = !auto.Checked;
        menu.Items.Add(auto);
        menu.Items.Add("Edit settings file", null, (s, e) =>
        {
            Settings.Save();
            System.Diagnostics.Process.Start("notepad.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"MonitorSwitch\settings.ini"));
        });
        menu.Items.Add("Reload settings", null, (s, e) => ReloadHotkeys(true));
        menu.Items.Add("Exit", null, (s, e) => { tray.Visible = false; hotkeys.Dispose(); Application.Exit(); });
    }

    public void ReloadHotkeys(bool fromDisk)
    {
        if (fromDisk) Settings.Load();
        hotkeys.Register();
        if (hotkeys.Failed.Count > 0)
            tray.ShowBalloonTip(4000, "MonitorSwitch", "Hotkey already in use: " + string.Join(", ", hotkeys.Failed), ToolTipIcon.Warning);
        if (form != null && !form.IsDisposed) form.RefreshHotkeys(hotkeys.Failed);
    }

    void OnHotkey(int id)
    {
        if (id == HotkeyWindow.ShowId) { if (form != null && form.Visible && Form.ActiveForm == form) form.Hide(); else ShowWindow(); return; }
        if (id >= HotkeyWindow.BrightnessBaseId)
        {
            var k = HotkeyWindow.BrightnessKeys[id - HotkeyWindow.BrightnessBaseId];
            int step = k == Keys.Up || k == Keys.Down ? Settings.BrightnessStep : 2;
            AdjustBrightness(k == Keys.Up || k == Keys.Right ? step : -step);
            return;
        }
        var b = Settings.Bindings[id - 1];
        Switch(b.MonitorId, b.Input);
    }

    // Key repeat fires faster than DDC can write, so presses are summed and applied by one worker.
    readonly object brightLock = new object();
    int pendingDelta;
    bool adjusting;
    readonly Osd osd = new Osd();

    void AdjustBrightness(int delta)
    {
        lock (brightLock)
        {
            pendingDelta += delta;
            if (adjusting) return;
            adjusting = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                int d;
                lock (brightLock)
                {
                    d = pendingDelta; pendingDelta = 0;
                    if (d == 0) { adjusting = false; break; }
                }
                int v = Ddc.AdjustBrightness(d);
                ui.BeginInvoke((Action)(() =>
                {
                    if (v >= 0) osd.ShowValue(v);
                    else tray.ShowBalloonTip(3000, "MonitorSwitch", "Brightness: no DDC/CI monitor on this PC", ToolTipIcon.Warning);
                }));
            }
            ui.BeginInvoke((Action)(() => { if (form != null && !form.IsDisposed && form.Visible) form.Reload(false); }));
        });
    }

    public void Switch(string id, int input)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var err = Ddc.Set(id, Ddc.VCP_INPUT, input);
            ui.BeginInvoke((Action)(() =>
            {
                if (err != null) tray.ShowBalloonTip(3000, "MonitorSwitch", err, ToolTipIcon.Warning);
                if (form != null && !form.IsDisposed && form.Visible) form.Reload(false);
            }));
        });
    }

    void ShowWindow()
    {
        if (form == null || form.IsDisposed) form = new MainForm(this, hotkeys.Failed);
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.Reload(true);
    }
}

class ShowListener : NativeWindow
{
    readonly Action show;
    public ShowListener(Action show)
    {
        this.show = show;
        // A top-level (non message-only) invisible window so HWND_BROADCAST reaches it.
        CreateHandle(new CreateParams { Caption = "MonitorSwitch.Listener" });
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_SHOWME) show();
        base.WndProc(ref m);
    }
}

// Small brightness indicator near the bottom of the screen; never takes focus.
class Osd : Form
{
    readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 1200 };
    int value;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Theme.Card;
        Size = new Size(260, 56); DoubleBuffered = true;
        hideTimer.Tick += (s, e) => { hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT
            return cp;
        }
    }

    public void ShowValue(int v)
    {
        value = v;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 80);
        if (!Visible) Show();
        Invalidate();
        hideTimer.Stop(); hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(Theme.Text)) g.DrawString("☀  " + value, Theme.Title, b, 14, 14);
        var bar = new Rectangle(90, 25, Width - 108, 6);
        using (var b = new SolidBrush(Theme.Btn)) g.FillRectangle(b, bar);
        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, bar.X, bar.Y, bar.Width * value / 100, bar.Height);
    }
}

static class AppIcon
{
    public static Icon Make()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 2, 4, 28, 19);
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 5, 7, 22, 13);
            using (var b = new SolidBrush(Theme.Accent)) { g.FillRectangle(b, 13, 23, 6, 4); g.FillRectangle(b, 8, 27, 16, 3); }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

// ---------------------------------------------------------------- UI

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 27);
    public static readonly Color Card = Color.FromArgb(36, 36, 41);
    public static readonly Color CardBorder = Color.FromArgb(56, 56, 63);
    public static readonly Color Text = Color.FromArgb(236, 236, 240);
    public static readonly Color Muted = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(77, 163, 255);
    public static readonly Color Btn = Color.FromArgb(50, 50, 57);
    public static readonly Color Warn = Color.FromArgb(240, 170, 80);
    public static readonly Font Body = new Font("Segoe UI", 9.5f);
    public static readonly Font Small = new Font("Segoe UI", 8.5f);
    public static readonly Font Title = new Font("Segoe UI Semibold", 12f);
    public static readonly Font BtnFont = new Font("Segoe UI Semibold", 9.5f);
}

class FlatBtn : Button
{
    public FlatBtn(string text, bool active)
    {
        Text = text; FlatStyle = FlatStyle.Flat; Font = Theme.BtnFont; Cursor = Cursors.Hand;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(10, 4, 10, 4);
        Margin = new Padding(0, 0, 8, 8);
        SetActive(active);
    }
    public void SetActive(bool active)
    {
        BackColor = active ? Theme.Accent : Theme.Btn;
        ForeColor = active ? Color.White : Theme.Text;
        FlatAppearance.BorderColor = active ? Theme.Accent : Theme.CardBorder;
        FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Color.FromArgb(66, 66, 75);
    }
}

// Mouse-drag only: scrolling over the window or keys (Ctrl+Alt+arrows) must not move it.
class NoWheelTrackBar : TrackBar
{
    public NoWheelTrackBar() { TabStop = false; }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x020A) return;                        // WM_MOUSEWHEEL
        if (m.Msg >= 0x0100 && m.Msg <= 0x0109) return;     // WM_KEYDOWN .. WM_UNICHAR
        base.WndProc(ref m);
    }
}

class MainForm : Form
{
    readonly TrayApp app;
    readonly FlowLayoutPanel list;
    readonly Label status;
    readonly Label hotkeyInfo;
    List<MonitorInfo> monitors = new List<MonitorInfo>();
    int loadVersion;

    public MainForm(TrayApp app, List<string> failedHotkeys)
    {
        this.app = app;
        Text = "MonitorSwitch"; Icon = AppIcon.Make();
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 360);

        var header = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(16, 12, 16, 0) };
        header.Controls.Add(new Label { Text = "Monitors", Font = Theme.Title, AutoSize = true, Location = new Point(16, 12) });
        var refresh = new FlatBtn("Refresh  F5", false) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refresh.Click += (s, e) => Reload(true);
        header.Controls.Add(refresh);
        header.Layout += (s, e) => refresh.Location = new Point(header.Width - refresh.Width - 16, 10);

        list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(16, 4, 16, 8)
        };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 4, 16, 6) };
        hotkeyInfo = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true };
        status = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small };
        footer.Controls.Add(status); footer.Controls.Add(hotkeyInfo);

        Controls.Add(list); Controls.Add(footer); Controls.Add(header);
        RefreshHotkeys(failedHotkeys);

        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyDown += OnKey;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;   // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar on Windows 10 20H1+ / 11
        Native.DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    public void RefreshHotkeys(List<string> failed)
    {
        if (failed.Count > 0)
        {
            hotkeyInfo.ForeColor = Theme.Warn;
            hotkeyInfo.Text = "Hotkey in use by another app: " + string.Join(", ", failed) + "  (tray → Edit settings file)";
        }
        else
        {
            hotkeyInfo.ForeColor = Theme.Muted;
            hotkeyInfo.Text = Settings.FormatKeys(Settings.ShowKey) + " show / hide   ·   Ctrl+Alt+↑↓ brightness (←→ fine)";
        }
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); return; }
        if (e.KeyCode == Keys.F5) { Reload(true); return; }
        // 1..9 switch the first monitor to its Nth input.
        if (e.Modifiers == Keys.None && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
        {
            var m = monitors.FirstOrDefault(x => x.DdcOk);
            int n = e.KeyCode - Keys.D1;
            if (m != null && n < m.Inputs.Count) DoSwitch(m, m.Inputs[n]);
        }
    }

    public void Reload(bool showLoading)
    {
        int version = ++loadVersion;
        if (showLoading) status.Text = "Reading monitors over DDC/CI…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Quick pass (no DDC reads) first so the window fills instantly, then the full read.
            if (showLoading) Publish(version, () => Ddc.List(false), false);
            Publish(version, () => Ddc.List(true), true);
        });
    }

    void Publish(int version, Func<List<MonitorInfo>> read, bool done)
    {
        List<MonitorInfo> result; string err = null;
        try { result = read(); } catch (Exception ex) { result = new List<MonitorInfo>(); err = ex.Message; }
        if (IsDisposed) return;
        Invoke((Action)(() =>
        {
            if (version != loadVersion) return;
            monitors = result;
            Render();
            if (done) status.Text = err ?? (result.Count + " monitor" + (result.Count == 1 ? "" : "s") + "   ·   1-9 switch input   ·   Esc hide   ·   right-click an input for hotkey / name");
        }));
    }

    void Render()
    {
        list.SuspendLayout();
        list.Controls.Clear();
        int width = list.ClientSize.Width - list.Padding.Horizontal - 2;
        foreach (var m in monitors) list.Controls.Add(Card(m, width));
        if (monitors.Count == 0)
            list.Controls.Add(new Label { Text = "No monitors found.", ForeColor = Theme.Muted, AutoSize = true });
        list.ResumeLayout();
        // Grow the window to fit the cards (up to a limit) so there is no scrolling in the usual case.
        int needed = list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + list.Padding.Vertical + 48 + 54;
        ClientSize = new Size(ClientSize.Width, Math.Max(220, Math.Min(needed, 760)));
    }

    Control Card(MonitorInfo m, int width)
    {
        var card = new Panel { Width = width, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(14, 12, 14, 6) };
        card.Paint += (s, e) => { using (var p = new Pen(Theme.CardBorder)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };

        var title = new Label { Text = m.Name + (m.Primary ? "   ★ primary" : ""), Font = Theme.Title, AutoSize = true, Location = new Point(14, 10) };
        var detail = new Label
        {
            Text = m.Width + "×" + m.Height + " @ " + m.Hz + " Hz   ·   " + m.Device + "   ·   " + m.ShortId + "\n" + m.Adapter
                + "   ·   at " + m.X + "," + m.Y,
            ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, 38)
        };
        card.Controls.Add(title); card.Controls.Add(detail);
        int y = 82;

        if (!m.DdcOk)
        {
            card.Controls.Add(new Label
            {
                Text = m.Loaded ? "DDC/CI not available (enable DDC/CI in the monitor's OSD menu)" : "Reading inputs…",
                ForeColor = m.Loaded ? Theme.Warn : Theme.Muted, AutoSize = true, Location = new Point(16, y)
            });
            card.Height = y + 34;
            return card;
        }

        card.Controls.Add(new Label { Text = "INPUT", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, y) });
        y += 22;
        var inputs = new FlowLayoutPanel { Location = new Point(14, y), Width = width - 28, AutoSize = true, MaximumSize = new Size(width - 28, 0), WrapContents = true };
        for (int i = 0; i < m.Inputs.Count; i++)
        {
            int code = m.Inputs[i];
            var label = Settings.Label(m.ShortId, code);
            var hk = Settings.Bindings.FirstOrDefault(b => b.Input == code && (b.MonitorId == "*" || string.Equals(b.MonitorId, m.ShortId, StringComparison.OrdinalIgnoreCase)));
            var text = (i + 1) + "  " + (label != null ? label + " · " : "") + Ddc.InputName(code) + (hk != null ? "   " + Settings.FormatKeys(hk.Key) : "");
            var btn = new FlatBtn(text, code == m.Input);
            btn.Click += (s, e) => DoSwitch(m, code);
            btn.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) InputMenu(m, code).Show(btn, e.Location); };
            inputs.Controls.Add(btn);
        }
        card.Controls.Add(inputs);
        y += inputs.PreferredSize.Height + 6;

        if (m.Brightness >= 0)
        {
            var bl = new Label { Text = "BRIGHTNESS  " + m.Brightness, ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, y) };
            card.Controls.Add(bl);
            y += 20;
            var bar = new NoWheelTrackBar { Minimum = 0, Maximum = m.BrightnessMax, Value = Math.Min(m.Brightness, m.BrightnessMax), TickStyle = TickStyle.None, Location = new Point(10, y), Width = width - 24, BackColor = Theme.Card };
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                timer.Stop(); int v = bar.Value; var id = m.ShortId;
                ThreadPool.QueueUserWorkItem(_ => Ddc.Set(id, Ddc.VCP_BRIGHTNESS, v));
            };
            bar.Scroll += (s, e) => { bl.Text = "BRIGHTNESS  " + bar.Value; m.Brightness = bar.Value; timer.Stop(); timer.Start(); };
            card.Controls.Add(bar);
            y += bar.Height;
        }
        card.Height = y + 6;
        return card;
    }

    ContextMenuStrip InputMenu(MonitorInfo m, int code)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Assign hotkey…", null, (s, e) =>
        {
            var k = HotkeyDialog.Capture(this, Ddc.InputName(code));
            if (k == Keys.None) return;
            Settings.Bindings.RemoveAll(b => b.Key == k || (b.Input == code && string.Equals(b.MonitorId, m.ShortId, StringComparison.OrdinalIgnoreCase)));
            Settings.Bindings.Add(new Binding { Key = k, MonitorId = m.ShortId, Input = code });
            Settings.Save(); app.ReloadHotkeys(false); Render();
        });
        menu.Items.Add("Remove hotkey", null, (s, e) =>
        {
            Settings.Bindings.RemoveAll(b => b.Input == code && string.Equals(b.MonitorId, m.ShortId, StringComparison.OrdinalIgnoreCase));
            Settings.Save(); app.ReloadHotkeys(false); Render();
        });
        menu.Items.Add("Rename…", null, (s, e) =>
        {
            var name = HotkeyDialog.AskText(this, "Name for " + Ddc.InputName(code) + " (e.g. Desktop, Laptop)", Settings.Label(m.ShortId, code) ?? "");
            if (name == null) return;
            var key = m.ShortId + ":" + code;
            if (name.Trim().Length == 0) Settings.Labels.Remove(key); else Settings.Labels[key] = name.Trim().Replace(",", " ");
            Settings.Save(); app.ReloadHotkeys(false); Render();
        });
        return menu;
    }

    void DoSwitch(MonitorInfo m, int code)
    {
        status.Text = "Switching " + m.Name + " to " + Ddc.InputName(code) + "…";
        app.Switch(m.ShortId, code);
    }
}

static class HotkeyDialog
{
    public static Keys Capture(IWin32Window owner, string what)
    {
        Keys result = Keys.None;
        using (var f = Dialog("Hotkey for " + what))
        {
            var lbl = new Label { Text = "Press a key combination (with Ctrl/Alt/Shift)…\nEsc to cancel", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            f.Controls.Add(lbl);
            f.KeyPreview = true;
            f.KeyDown += (s, e) =>
            {
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Escape) { f.Close(); return; }
                var code = e.KeyCode;
                if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey) { lbl.Text = Settings.FormatKeys(e.Modifiers) + "+…"; return; }
                if ((e.Modifiers & (Keys.Control | Keys.Alt)) == 0) { lbl.Text = "Use Ctrl and/or Alt with the key"; return; }
                result = code | e.Modifiers; f.Close();
            };
            f.ShowDialog(owner);
        }
        return result;
    }

    public static string AskText(IWin32Window owner, string prompt, string value)
    {
        string result = null;
        using (var f = Dialog(prompt))
        {
            var box = new TextBox { Text = value, Dock = DockStyle.Top, BackColor = Theme.Btn, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            var ok = new FlatBtn("Save", true) { Dock = DockStyle.Bottom };
            ok.Click += (s, e) => { result = box.Text; f.Close(); };
            f.AcceptButton = ok;
            f.Controls.Add(ok); f.Controls.Add(box);
            f.ShowDialog(owner);
        }
        return result;
    }

    static Form Dialog(string title)
    {
        return new Form
        {
            Text = title, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(340, 110), Padding = new Padding(16),
            AutoScaleMode = AutoScaleMode.Dpi
        };
    }
}
