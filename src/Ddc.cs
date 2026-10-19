using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

class MonitorInfo
{
    public string ShortId;      // e.g. ACR075A - stable across reboots, used for hotkeys
    public string Name;         // friendly name from EDID (WMI) or driver description
    public string Device;       // DISPLAY1
    public string Adapter;
    public int Width, Height, Hz, X, Y;
    public bool Primary;
    public bool Loaded;         // DDC values were read (false on the quick first pass)
    public bool DdcOk;
    public int Input = -1;
    public int Brightness = -1, BrightnessMax = 100;
    public List<int> Inputs = new List<int>();
}

// All DDC/CI access. Every public method is thread-safe and blocking (DDC is slow: call off the UI thread).
static class Ddc
{
    public const byte VCP_INPUT = 0x60, VCP_BRIGHTNESS = 0x10;
    public const string AllMonitors = "*";

    static readonly object Lock = new object();   // DDC/I2C is slow and must not overlap
    static readonly Dictionary<string, List<int>> CapsCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> friendlyNames;

    // Raised (on a worker thread) when a monitor's input list was read from its capabilities string.
    public static event Action<string, List<int>> InputsDiscovered;

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
            });
        }
        return result.OrderBy(x => x.X).ThenBy(x => x.Y).ToList();
    }

    // Changes brightness on every DDC monitor by delta; returns the new percentage of the first one, or -1.
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

    // Sets a VCP value on monitors matching shortId (or all if AllMonitors). device, when given, narrows
    // the match to one display so two identical monitor models can be told apart. Returns null on success.
    public static string Set(string shortId, string device, byte code, int value)
    {
        int matched = 0;
        var failed = new List<string>();
        lock (Lock)
        {
            ForEach((m, h) =>
            {
                if (shortId != AllMonitors && !string.Equals(m.ShortId, shortId, StringComparison.OrdinalIgnoreCase)) return;
                if (device != null && !string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)) return;
                matched++;
                if (!Native.SetVCPFeature(h, code, (uint)value)) failed.Add(m.ShortId);
            });
        }
        if (matched == 0) return "Monitor " + shortId + " not found (is it showing this PC?)";
        return failed.Count == 0 ? null : "DDC/CI write failed on " + string.Join(", ", failed);
    }

    static void ReadDetails(MonitorInfo m, IntPtr h)
    {
        uint cur, max;
        if (Native.GetVCPFeatureAndVCPFeatureReply(h, VCP_INPUT, IntPtr.Zero, out cur, out max))
        { m.DdcOk = true; m.Input = (int)(cur & 0xFF); }
        if (Native.GetVCPFeatureAndVCPFeatureReply(h, VCP_BRIGHTNESS, IntPtr.Zero, out cur, out max))
        { m.DdcOk = true; m.Brightness = (int)cur; m.BrightnessMax = (int)Math.Max(1, max); }
        if (m.DdcOk) m.Inputs = Inputs(m.ShortId, h);
        if (m.Input > 0 && !m.Inputs.Contains(m.Input)) m.Inputs.Insert(0, m.Input);
    }

    // Visits every physical monitor with its handle open; handles are destroyed afterwards. Caller holds Lock.
    static void ForEach(Action<MonitorInfo, IntPtr> visit)
    {
        var hmons = new List<IntPtr>();
        Native.MonitorEnumProc cb = (h, dc, r, d) => { hmons.Add(h); return true; };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        foreach (var hMon in hmons)
        {
            var mi = new Native.MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(mi);
            if (!Native.GetMonitorInfo(hMon, ref mi)) continue;

            string adapter;
            var ids = MonitorDeviceIds(mi.szDevice, out adapter);
            var dm = new Native.DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(dm);
            Native.EnumDisplaySettings(mi.szDevice, Native.ENUM_CURRENT_SETTINGS, ref dm);

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
                    m.Primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
                    m.X = mi.rcMonitor.Left; m.Y = mi.rcMonitor.Top;
                    m.Width = dm.dmPelsWidth; m.Height = dm.dmPelsHeight; m.Hz = dm.dmDisplayFrequency;
                    var parts = (p < ids.Count ? ids[p] : "").Split('\\');
                    m.ShortId = parts.Length > 1 ? parts[1] : m.Device;
                    m.Name = phys[p].szPhysicalMonitorDescription;
                    visit(m, phys[p].hPhysicalMonitor);
                }
            }
            finally { Native.DestroyPhysicalMonitors(n, phys); }
        }
    }

    // Active monitor device ids under a display, in the same order as its physical monitors.
    static List<string> MonitorDeviceIds(string display, out string adapter)
    {
        adapter = "";
        var ad = new Native.DISPLAY_DEVICE(); ad.cb = Marshal.SizeOf(ad);
        for (uint a = 0; Native.EnumDisplayDevices(null, a, ref ad, 0); a++)
            if (ad.DeviceName == display) { adapter = ad.DeviceString; break; }

        var ids = new List<string>();
        var md = new Native.DISPLAY_DEVICE(); md.cb = Marshal.SizeOf(md);
        for (uint i = 0; Native.EnumDisplayDevices(display, i, ref md, 0); i++)
            if ((md.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0) ids.Add(md.DeviceID);
        return ids;
    }

    static List<int> Inputs(string shortId, IntPtr h)
    {
        List<int> cached;
        if (CapsCache.TryGetValue(shortId, out cached)) return new List<int>(cached);

        var list = ParseInputs(ReadCapabilities(h));
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
        uint len;
        if (!Native.GetCapabilitiesStringLength(h, out len) || len == 0) return "";
        var sb = new StringBuilder((int)len);
        return Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len) ? sb.ToString() : "";
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
        if (friendlyNames == null) friendlyNames = LoadFriendlyNames();
        string n;
        return friendlyNames.TryGetValue(shortId, out n) ? n : null;
    }

    static Dictionary<string, string> LoadFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
            using (var results = s.Get())
                foreach (ManagementObject o in results)
                    using (o)
                    {
                        var parts = ((string)o["InstanceName"] ?? "").Split('\\');
                        var raw = o["UserFriendlyName"] as ushort[];
                        if (parts.Length < 2 || raw == null) continue;
                        var name = new string(raw.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                        if (name.Length > 0) names[parts[1]] = name;
                    }
        }
        catch (Exception ex) { Log.Error("WMI monitor names", ex); }
        return names;
    }
}
