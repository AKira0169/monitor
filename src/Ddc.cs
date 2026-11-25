using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

enum BrightnessSource { None, Ddc, Wmi }

class MonitorInfo
{
    public string ShortId;      // e.g. ACR075A - model id, shared by identical monitors
    public string UnitId;       // e.g. ACR075A-76650389 - one physical unit (EDID serial, else port); equals ShortId if unknown
    public string PortId;       // e.g. ACR075A-UID4357 - port form; always accepted as an alias for UnitId

    // id is "*", this monitor's model id (every unit of the model), or this unit's UnitId/PortId.
    public bool Matches(string id)
    {
        return id == Ddc.AllMonitors || IsUnit(id) || string.Equals(ShortId, id, StringComparison.OrdinalIgnoreCase);
    }

    // id names this one physical unit (not the whole model).
    public bool IsUnit(string id)
    {
        return string.Equals(UnitId, id, StringComparison.OrdinalIgnoreCase) || string.Equals(PortId, id, StringComparison.OrdinalIgnoreCase);
    }
    public string Instance = "";   // PnP instance, e.g. 5&2302f849&1&UID4357 (joins WMI rows to this monitor)
    public string Name;         // friendly name from EDID (WMI) or driver description
    public string Device;       // DISPLAY1
    public string Adapter;
    public int Width, Height, Hz, X, Y;
    public bool Primary;
    public bool Loaded;         // DDC values were read (false on the quick first pass)
    public bool DdcOk;
    public int Input = -1;
    public int Brightness = -1, BrightnessMax = 100;
    public BrightnessSource BrightnessVia = BrightnessSource.None;   // how brightness is read/written
    public bool NoHandle;       // Windows gave no physical-monitor handle (typical for laptop panels): no DDC at all
    public bool SwitchedAway;   // not answering because we just switched it to another input (expected)
    public Quirk Quirk;         // built-in per-model workaround/note, or null
    public List<int> Inputs = new List<int>();
    public bool CanSwitchInput { get { return DdcOk && (Input >= 0 || Inputs.Count > 0); } }
}

// All DDC/CI access. Every public method is thread-safe and blocking (DDC is slow: call off the UI thread).
static class Ddc
{
    public const byte VCP_INPUT = 0x60, VCP_BRIGHTNESS = 0x10;
    public const string AllMonitors = "*";

    static readonly object Lock = new object();   // DDC/I2C is slow and must not overlap
    static readonly Dictionary<string, List<int>> CapsCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> friendlyNames;
    static Dictionary<string, string> serials;   // WMI instance ("5&2302f849&1&UID4357") -> EDID serial
    static DateTime wmiLoaded;
    static bool wmiOk;   // last WMI query succeeded; a failed one is retried (rate limited)

    static readonly IntPtr NoHandle = new IntPtr(-1);   // visit() handle for a display Windows gave no physical monitor for
    const int Attempts = 3, RetryDelayMs = 45;                       // flaky DDC: retries on failure only
    static readonly TimeSpan SwitchGrace = TimeSpan.FromSeconds(90);   // a monitor may stay silent after switching away
    static readonly HashSet<string> wmiPanels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // brightness via WMI (under Lock)
    static readonly Dictionary<string, DateTime> switchedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // under Lock

    static readonly object WmiLock = new object();   // WMI brightness calls (never held together with Lock)
    static DateTime wmiBrightRetryAfter;
    static bool wmiBrightLogged;
    static readonly Dictionary<string, DateTime> wmiNoMatchUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // under WmiLock

    static readonly object DiagLock = new object();
    static string lastDiag;

    // Raised (on a worker thread) when a monitor's input list was read from its capabilities string.
    public static event Action<string, List<int>> InputsDiscovered;

    public static string InputName(int code)
    {
        switch (code)
        {
            case 0x01: return "VGA 1"; case 0x02: return "VGA 2";
            case 0x03: return "DVI 1"; case 0x04: return "DVI 2";
            case 0x05: return "Composite 1"; case 0x06: return "Composite 2";
            case 0x07: return "S-Video 1"; case 0x08: return "S-Video 2";
            case 0x09: return "Tuner 1"; case 0x0A: return "Tuner 2"; case 0x0B: return "Tuner 3";
            case 0x0C: return "Component 1"; case 0x0D: return "Component 2"; case 0x0E: return "Component 3";
            case 0x0F: return "DisplayPort 1"; case 0x10: return "DisplayPort 2";
            case 0x11: return "HDMI 1"; case 0x12: return "HDMI 2";
            case 0x1B: return "USB-C";
            // Non-standard codes some monitors (notably LG) use; see Quirks.cs.
            case 0x90: return "HDMI 1 (alt code)"; case 0x91: return "HDMI 2 (alt code)";
            case 0xD0: return "DisplayPort 1 (alt code)"; case 0xD1: return "DisplayPort 2 (alt code)";
            default: return "Input " + code;
        }
    }

    // Replaces the capabilities cache with the saved lists (the capabilities query takes seconds per
    // monitor). Monitors missing from known are re-queried, so editing/removing an inputs= line works.
    public static void SeedInputs(IDictionary<string, List<int>> known)
    {
        lock (Lock)
        {
            CapsCache.Clear();
            foreach (var kv in known) CapsCache[kv.Key] = new List<int>(kv.Value);
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
                if (withDetails) ReadDetails(m, h);
                result.Add(m);
            }, true);
        }
        if (withDetails)
        {
            AddWmiBrightness(result);   // WMI is slow: outside the DDC lock
            LogDiagnostics(result);
        }
        return result.OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
    }

    // Changes brightness on every monitor by delta (DDC, or WMI for laptop panels); returns the new
    // percentage of the first one, or -1.
    public static int AdjustBrightness(int delta)
    {
        int result = -1;
        var viaWmi = new List<MonitorInfo>();
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                if (!IsWmiPanel(m))
                {
                    // Hotkey path: one attempt, and no waiting on a monitor we know is silent (switched away).
                    if (!m.NoHandle)
                    {
                        if (RecentlySwitched(m)) return;
                        uint cur, max;
                        if (GetVcp(h, VCP_BRIGHTNESS, out cur, out max, 1))
                        {
                            int v = Math.Max(0, Math.Min((int)max, (int)cur + delta));
                            if (SetVcp(h, VCP_BRIGHTNESS, (uint)v) && result < 0) result = v * 100 / (int)Math.Max(1, max);
                        }
                        return;
                    }
                }
                viaWmi.Add(m);   // known WMI panel, or a display with no DDC handle at all
            });
        }
        foreach (var m in viaWmi)
        {
            int cur; int[] levels;
            if (!WmiRead(m, out cur, out levels)) continue;
            int v = SnapStep(cur, delta, levels);
            if (WmiWrite(m, v) && result < 0) result = v;
        }
        return result;
    }

    // Sets a VCP value on monitors matching id: AllMonitors, a ShortId (every unit of that model) or a
    // UnitId/PortId (one unit). device, when given, narrows the match further. Brightness falls back to WMI
    // for panels without DDC. Returns null on success.
    public static string Set(string id, string device, byte code, int value)
    {
        int matched = 0;
        var failed = new List<string>();
        var viaWmi = new List<MonitorInfo>();
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                if (!m.Matches(id)) return;
                if (device != null && !string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)) return;
                matched++;
                if (code == VCP_BRIGHTNESS && IsWmiPanel(m)) { viaWmi.Add(m); return; }
                int raw = value;
                if (code == VCP_INPUT && m.Quirk != null) raw = m.Quirk.ToDevice(value);
                bool actuallySwitching = false;
                if (code == VCP_INPUT)
                {
                    // Only a real change of input makes the monitor go quiet; remember that (not a re-select).
                    uint was, wmax;
                    if (GetVcp(h, VCP_INPUT, out was, out wmax, 1))
                    {
                        int shown = (int)(was & 0xFF);
                        if (m.Quirk != null) shown = m.Quirk.FromDevice(shown);
                        actuallySwitching = shown != value;
                    }
                }
                if (SetVcp(h, code, (uint)raw))
                {
                    if (actuallySwitching) switchedAt[SwitchKey(m)] = DateTime.UtcNow;
                    if (m.Quirk != null && m.Quirk.DelayMs > 0) Thread.Sleep(m.Quirk.DelayMs);
                }
                else if (code == VCP_BRIGHTNESS && m.NoHandle) viaWmi.Add(m);
                else failed.Add(m.ShortId);
            });
        }
        foreach (var m in viaWmi)
        {
            int cur; int[] levels;
            WmiRead(m, out cur, out levels);   // only for the level list; a failed read still lets the write try
            if (!WmiWrite(m, Snap(value, levels))) failed.Add(m.ShortId);
        }
        if (matched == 0) return "Monitor " + id + " not found (is it showing this PC?)";
        return failed.Count == 0 ? null : "Write failed on " + string.Join(", ", failed) + (code == VCP_INPUT ? " (DDC/CI)" : "");
    }

    // dxva2 calls with a small retry: many monitors, docks and KVMs drop an occasional DDC transaction.
    // Success returns immediately; only failures pay the delay. NoHandle (no DDC) fails at once. (0 is a valid physical-monitor handle on recent Windows.)
    static bool GetVcp(IntPtr h, byte code, out uint cur, out uint max, int attempts = Attempts)
    {
        cur = 0; max = 0;
        if (h == NoHandle) return false;
        for (int i = 0; i < attempts; i++)
        {
            if (Native.GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out cur, out max)) return true;
            if (i < attempts - 1) Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    static bool SetVcp(IntPtr h, byte code, uint value)
    {
        if (h == NoHandle) return false;
        for (int i = 0; i < Attempts; i++)
        {
            if (Native.SetVCPFeature(h, code, value)) return true;
            if (i < Attempts - 1) Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    // Keyed by model + display, not UnitId (which can flip between serial and port form).
    static string SwitchKey(MonitorInfo m) { return m.ShortId + "|" + m.Device; }

    static bool RecentlySwitched(MonitorInfo m)   // caller holds Lock
    {
        DateTime when;
        return switchedAt.TryGetValue(SwitchKey(m), out when) && DateTime.UtcNow - when < SwitchGrace;
    }

    static string PanelKey(MonitorInfo m) { return m.Instance.Length > 0 ? m.Instance : m.ShortId; }

    static bool IsWmiPanel(MonitorInfo m) { return wmiPanels.Contains(PanelKey(m)); }   // caller holds Lock

    static void ReadDetails(MonitorInfo m, IntPtr h)
    {
        uint cur, max;
        bool wmiPanel = IsWmiPanel(m);
        bool inputRead = GetVcp(h, VCP_INPUT, out cur, out max);
        if (inputRead)
        {
            m.DdcOk = true;
            m.Input = (int)(cur & 0xFF);
            if (m.Quirk != null) m.Input = m.Quirk.FromDevice(m.Input);
        }
        if ((inputRead || !wmiPanel) && GetVcp(h, VCP_BRIGHTNESS, out cur, out max))
        { m.DdcOk = true; m.Brightness = (int)cur; m.BrightnessMax = (int)Math.Max(1, max); m.BrightnessVia = BrightnessSource.Ddc; }

        if (m.DdcOk) switchedAt.Remove(SwitchKey(m));
        else if (RecentlySwitched(m))
        {
            // We just switched this monitor to another input; it legitimately stops answering. Not an error.
            m.SwitchedAway = true; m.DdcOk = true;
        }
        if (m.DdcOk) m.Inputs = Inputs(m, h);
        if (m.Input > 0 && !m.Inputs.Contains(m.Input)) m.Inputs.Insert(0, m.Input);
    }

    // Laptop panels (and anything else without DDC brightness): brightness through WMI.
    static void AddWmiBrightness(List<MonitorInfo> list)
    {
        foreach (var m in list)
        {
            if (m.Brightness >= 0)
            {
                lock (Lock) wmiPanels.Remove(PanelKey(m));
                continue;
            }
            int cur; int[] levels;
            if (!WmiRead(m, out cur, out levels)) continue;
            m.Brightness = Math.Max(0, Math.Min(100, cur)); m.BrightnessMax = 100;
            m.BrightnessVia = BrightnessSource.Wmi;
            lock (Lock) wmiPanels.Add(PanelKey(m));
        }
    }

    // "DISPLAY\ACR075A\5&2302f849&1&UID4357_0" -> instance (5&2302f849&1&UID4357) and model id (ACR075A).
    static string WmiInstanceOf(string instanceName, out string model)
    {
        model = "";
        var parts = (instanceName ?? "").Split('\\');
        if (parts.Length < 2) return "";
        model = parts[1];
        if (parts.Length < 3) return "";
        var inst = parts[2];
        int us = inst.LastIndexOf('_');
        return us > 0 ? inst.Substring(0, us) : inst;
    }

    static bool WmiMatches(string instanceName, MonitorInfo m)
    {
        string model;
        var inst = WmiInstanceOf(instanceName, out model);
        if (m.Instance.Length > 0 && inst.Length > 0) return string.Equals(inst, m.Instance, StringComparison.OrdinalIgnoreCase);
        return string.Equals(model, m.ShortId, StringComparison.OrdinalIgnoreCase);
    }

    // Current brightness (0-100) and the panel's allowed levels. False if this monitor has no WMI brightness
    // (every desktop monitor): "no instances at all" is remembered for a minute so hotkeys stay quick.
    static bool WmiRead(MonitorInfo m, out int cur, out int[] levels)
    {
        cur = -1; levels = null;
        lock (WmiLock)
        {
            if (DateTime.UtcNow < wmiBrightRetryAfter) return false;
            DateTime until;
            if (wmiNoMatchUntil.TryGetValue(PanelKey(m), out until) && DateTime.UtcNow < until) return false;
            try
            {
                bool any = false;
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentBrightness, Levels FROM WmiMonitorBrightness"))
                using (var results = s.Get())
                    foreach (ManagementObject o in results)
                        using (o)
                        {
                            any = true;
                            if (!WmiMatches((string)o["InstanceName"], m)) continue;
                            wmiNoMatchUntil.Remove(PanelKey(m));
                            cur = Convert.ToInt32(o["CurrentBrightness"]);
                            var raw = o["Levels"] as byte[];
                            levels = raw == null ? new int[0] : raw.Select(b => (int)b).ToArray();
                            return true;
                        }
                if (!any) wmiBrightRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                else wmiNoMatchUntil[PanelKey(m)] = DateTime.UtcNow + TimeSpan.FromSeconds(60);   // rows, but none for this monitor
            }
            catch (Exception ex)
            {
                wmiBrightRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                if (!wmiBrightLogged && !(ex is ManagementException && ((ManagementException)ex).ErrorCode == ManagementStatus.NotSupported)) { wmiBrightLogged = true; Log.Error("WMI brightness read", ex); }
            }
        }
        return false;
    }

    static bool WmiWrite(MonitorInfo m, int level)
    {
        lock (WmiLock)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (var results = s.Get())
                    foreach (ManagementObject o in results)
                        using (o)
                        {
                            if (!WmiMatches((string)o["InstanceName"], m)) continue;
                            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });   // timeout (s), percent
                            return true;
                        }
            }
            catch (Exception ex) { Log.Error("WMI set brightness", ex); }
        }
        return false;
    }

    // Absolute value (slider): clamp to 0-100 and pick the nearest allowed level.
    static int Snap(int v, int[] levels)
    {
        v = Math.Max(0, Math.Min(100, v));
        if (levels == null || levels.Length == 0) return v;
        return levels.OrderBy(l => Math.Abs(l - v)).First();
    }

    // Relative change (hotkeys): moves to a level strictly past cur in the direction of delta (nearest to
    // cur + delta), clamping at the ends, so coarse panels never get stuck on the current level.
    static int SnapStep(int cur, int delta, int[] levels)
    {
        int target = Math.Max(0, Math.Min(100, cur + delta));
        if (levels == null || levels.Length == 0 || delta == 0) return Snap(target, levels);
        var beyond = (delta > 0 ? levels.Where(l => l > cur) : levels.Where(l => l < cur)).ToList();
        if (beyond.Count == 0) return delta > 0 ? levels.Max() : levels.Min();
        return beyond.OrderBy(l => Math.Abs(l - target)).First();
    }

    // One INFO line per monitor for bug reports; only written when something other than the live values changed.
    static void LogDiagnostics(List<MonitorInfo> list)
    {
        var lines = new List<string>();
        foreach (var m in list.OrderBy(x => x.Device, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add("Monitor Device=" + m.Device + " ShortId=" + m.ShortId + " UnitId=" + m.UnitId + " Adapter=\"" + m.Adapter + "\""
                + " DdcOk=" + m.DdcOk + " Handle=" + (m.NoHandle ? "none" : "ok") + " BrightnessVia=" + m.BrightnessVia
                + " Inputs=[" + string.Join(",", m.Inputs.Select(i => "0x" + i.ToString("X2")).ToArray()) + "]"
                + " Quirk=" + (m.Quirk != null ? m.Quirk.Key : "none")
                + (m.SwitchedAway ? " (switched away)" : ""));
        }
        var sig = string.Join("\n", lines.ToArray());
        lock (DiagLock)
        {
            if (sig == lastDiag) return;
            lastDiag = sig;
        }
        Log.Info("Monitor scan: " + list.Count + " display(s)");
        foreach (var l in lines) Log.Info(l);
    }

    class IdInfo { public string ShortId, UnitId, PortId, Instance; }

    class DisplayEntry
    {
        public IntPtr HMon; public Native.MONITORINFOEX Info; public string Adapter; public List<IdInfo> Ids;
    }

    // Visits every physical monitor with its handle open; handles are destroyed afterwards. Caller holds Lock.
    static void ForEach(Action<MonitorInfo, IntPtr> visit, bool allowWmiReload = false)
    {
        var hmons = new List<IntPtr>();
        Native.MonitorEnumProc cb = (h, dc, r, d) => { hmons.Add(h); return true; };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        // Pass 1: ids of every unit, so identical serials can be detected before anything is visited.
        var entries = new List<DisplayEntry>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var hMon in hmons)
        {
            var mi = new Native.MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(mi);
            if (!Native.GetMonitorInfo(hMon, ref mi)) continue;
            var e = new DisplayEntry { HMon = hMon, Info = mi, Ids = new List<IdInfo>() };
            string adapter;
            foreach (var devId in MonitorDeviceIds(mi.szDevice, out adapter))
            {
                string shortId, instance;
                ParseDeviceId(devId, out shortId, out instance);
                if (shortId.Length == 0) shortId = mi.szDevice.Replace(@"\\.\", "");
                var info = new IdInfo { ShortId = shortId, Instance = instance, PortId = MakePortId(shortId, instance) };
                info.UnitId = MakeUnitId(shortId, instance, info.PortId, allowWmiReload);
                int c; seen.TryGetValue(info.UnitId, out c); seen[info.UnitId] = c + 1;
                e.Ids.Add(info);
            }
            e.Adapter = adapter;
            entries.Add(e);
        }

        // Pass 2: open handles and visit. Units sharing a UnitId (same EDID serial) fall back to the port form,
        // so Set/AdjustBrightness/List all see the same ids.
        foreach (var e in entries)
        {
            var mi = e.Info;
            var dm = new Native.DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(dm);
            Native.EnumDisplaySettings(mi.szDevice, Native.ENUM_CURRENT_SETTINGS, ref dm);

            uint n;
            bool got = Native.GetNumberOfPhysicalMonitorsFromHMONITOR(e.HMon, out n) && n > 0;
            var phys = got ? new Native.PHYSICAL_MONITOR[n] : null;
            if (got) got = Native.GetPhysicalMonitorsFromHMONITOR(e.HMon, n, phys);
            if (!got)
            {
                // No physical-monitor handle (typical for laptop panels, some adapters/virtual displays):
                // still list it so it gets a card, but without DDC.
                var none = NewMonitor(e, dm, 0, seen);
                none.NoHandle = true;
                none.Name = none.Device;
                visit(none, NoHandle);
                continue;
            }
            try
            {
                for (int p = 0; p < n; p++)
                {
                    var m = NewMonitor(e, dm, p, seen);
                    m.Name = phys[p].szPhysicalMonitorDescription;
                    visit(m, phys[p].hPhysicalMonitor);
                }
            }
            finally { Native.DestroyPhysicalMonitors(n, phys); }
        }
    }

    static MonitorInfo NewMonitor(DisplayEntry e, Native.DEVMODE dm, int p, Dictionary<string, int> seen)
    {
        var mi = e.Info;
        var m = new MonitorInfo();
        m.Device = mi.szDevice.Replace(@"\\.\", "");
        m.Adapter = e.Adapter;
        m.Primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
        m.X = mi.rcMonitor.Left; m.Y = mi.rcMonitor.Top;
        m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight; m.Hz = dm.dmDisplayFrequency;
        if (p < e.Ids.Count)
        {
            var info = e.Ids[p];
            m.ShortId = info.ShortId; m.PortId = info.PortId; m.Instance = info.Instance;
            m.UnitId = seen[info.UnitId] > 1 ? info.PortId : info.UnitId;
        }
        else m.ShortId = m.UnitId = m.PortId = m.Device;
        m.Quirk = Quirks.Find(m.ShortId);
        return m;
    }

    // Interface path \\?\DISPLAY#ACR075A#5&2302f849&1&UID4357#{guid} -> model id + instance (5&2302f849&1&UID4357).
    // Also accepts the plain form MONITOR\ACR075A\{guid}\0001 (model id only).
    static void ParseDeviceId(string id, out string shortId, out string instance)
    {
        shortId = ""; instance = "";
        if (id.StartsWith(@"\\?\"))
        {
            var p = id.Substring(4).Split('#');
            if (p.Length > 1) shortId = p[1];
            if (p.Length > 2) instance = p[2];
        }
        else
        {
            var p = id.Split('\\');
            if (p.Length > 1) shortId = p[1];
        }
    }

    // ShortId + "-" + the port token (UID4357, or the sanitized instance). Without an instance: ShortId.
    static string MakePortId(string shortId, string instance)
    {
        if (instance.Length == 0) return shortId;
        var token = instance.Substring(instance.LastIndexOf('&') + 1);
        if (!token.StartsWith("UID", StringComparison.OrdinalIgnoreCase)) token = instance;
        return shortId + "-" + Sanitize(token);
    }

    // ShortId + "-" + EDID serial when there is one, else the port form. No commas/whitespace.
    static string MakeUnitId(string shortId, string instance, string portId, bool allowReload)
    {
        if (instance.Length == 0) return shortId;
        string serial = LookupSerial(instance, allowReload);
        return serial != null ? shortId + "-" + Sanitize(serial) : portId;
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_');
        return sb.ToString();
    }

    // Serial from WMI, or null if the monitor has none. Every instance WMI reports is cached (as "" without a
    // serial); only an instance WMI has never reported triggers a reload, rate limited.
    // Only List (allowReload) may query WMI again; Set/AdjustBrightness use whatever is cached (loaded once if never).
    static string LookupSerial(string instance, bool allowReload)
    {
        if (allowReload || serials == null) EnsureWmi(false);
        string v;
        if (!serials.TryGetValue(instance, out v) && allowReload && wmiOk && DateTime.UtcNow - wmiLoaded > TimeSpan.FromSeconds(10))
        {
            EnsureWmi(true);
            serials.TryGetValue(instance, out v);
        }
        return string.IsNullOrEmpty(v) ? null : v;
    }

    // Active monitor device ids (interface paths) under a display, in the same order as its physical monitors.
    static List<string> MonitorDeviceIds(string display, out string adapter)
    {
        adapter = "";
        var ad = new Native.DISPLAY_DEVICE(); ad.cb = Marshal.SizeOf(ad);
        for (uint a = 0; Native.EnumDisplayDevices(null, a, ref ad, 0); a++)
            if (ad.DeviceName == display) { adapter = ad.DeviceString; break; }

        var ids = new List<string>();
        var md = new Native.DISPLAY_DEVICE(); md.cb = Marshal.SizeOf(md);
        for (uint i = 0; Native.EnumDisplayDevices(display, i, ref md, Native.EDD_GET_DEVICE_INTERFACE_NAME); i++)
            if ((md.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0) ids.Add(md.DeviceID);
        return ids;
    }

    static List<int> Inputs(MonitorInfo m, IntPtr h)
    {
        var shortId = m.ShortId;
        if (m.Quirk != null && m.Quirk.FixedInputs != null) return new List<int>(m.Quirk.FixedInputs);   // capabilities string is wrong/missing
        List<int> cached;
        if (CapsCache.TryGetValue(shortId, out cached)) return new List<int>(cached);

        var list = ParseInputs(ReadCapabilities(h));
        if (m.Quirk != null) list = list.Select(c => m.Quirk.FromDevice(c)).Distinct().ToList();   // displayed codes everywhere
        CapsCache[shortId] = list;
        if (list.Count > 0)
        {
            var handler = InputsDiscovered;
            if (handler != null) handler(shortId, new List<int>(list));
        }
        return new List<int>(list);
    }

    static string ReadCapabilities(IntPtr h)
    {
        for (int i = 0; i < Attempts; i++)
        {
            uint len;
            if (Native.GetCapabilitiesStringLength(h, out len) && len > 0)
            {
                var sb = new StringBuilder((int)len);
                if (Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return sb.ToString();
            }
            if (i < Attempts - 1) Thread.Sleep(RetryDelayMs);
        }
        return "";
    }

    // e.g. "...vcp(02 04 10 12 60(0F 11 12) 62 ...)" -> [0x0F, 0x11, 0x12]
    static List<int> ParseInputs(string caps)
    {
        var list = new List<int>();
        var mt = Regex.Match(caps, @"\b60\s*\(([0-9A-Fa-f\s]+)\)");
        if (!mt.Success) return list;
        foreach (var hex in mt.Groups[1].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int v;
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out v) && !list.Contains(v)) list.Add(v);
        }
        return list;
    }

    static string FriendlyName(string shortId)
    {
        EnsureWmi(false);
        string n;
        return friendlyNames.TryGetValue(shortId, out n) ? n : null;
    }

    // A failed query is not final: it is retried (at most every 10 s) so ids don't flip between forms.
    static void EnsureWmi(bool force)
    {
        if (friendlyNames != null && !force && (wmiOk || DateTime.UtcNow - wmiLoaded < TimeSpan.FromSeconds(10))) return;
        wmiOk = LoadWmi();
        wmiLoaded = DateTime.UtcNow;
    }

    // One WmiMonitorID query fills both caches: friendly names (by ShortId) and EDID serials (by instance).
    static bool LoadWmi()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serialMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName, SerialNumberID FROM WmiMonitorID"))
            using (var results = s.Get())
                foreach (ManagementObject o in results)
                    using (o)
                    {
                        // DISPLAY\ACR075A\5&2302f849&1&UID4357_0
                        var parts = ((string)o["InstanceName"] ?? "").Split('\\');
                        if (parts.Length < 2) continue;
                        var name = WmiText(o["UserFriendlyName"]);
                        if (name.Length > 0) names[parts[1]] = name;
                        if (parts.Length < 3) continue;
                        var instance = parts[2];
                        int us = instance.LastIndexOf('_');
                        if (us > 0) instance = instance.Substring(0, us);
                        var serial = WmiText(o["SerialNumberID"]);
                        serialMap[instance] = serial.Length > 0 && serial != "0" ? serial : "";
                    }
        }
        catch (Exception ex)
        {
            Log.Error("WMI monitor names", ex);
            if (friendlyNames == null) { friendlyNames = names; serials = serialMap; }   // keep earlier good data
            return false;
        }
        friendlyNames = names;
        serials = serialMap;
        return true;
    }

    static string WmiText(object value)
    {
        var raw = value as ushort[];
        if (raw == null) return "";
        return new string(raw.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
    }
}
