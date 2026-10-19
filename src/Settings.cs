using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

class Binding
{
    public Keys Key;          // key code | modifier flags
    public string MonitorId;  // short id, or Ddc.AllMonitors
    public int Input;

    public bool Targets(string shortId, int input)
    {
        return Input == input && (MonitorId == Ddc.AllMonitors || string.Equals(MonitorId, shortId, StringComparison.OrdinalIgnoreCase));
    }
}

// User settings persisted as a small ini file. Only touch from the UI thread.
static class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitch");
    public static readonly string FilePath = Path.Combine(Dir, "settings.ini");

    static readonly Keys DefaultShowKey = Keys.Control | Keys.Alt | Keys.D0;
    const int DefaultBrightnessStep = 10;

    public static Keys ShowKey = DefaultShowKey;
    public static int BrightnessStep = DefaultBrightnessStep;   // Ctrl+Alt+Up/Down; Left/Right use FineBrightnessStep
    public const int FineBrightnessStep = 2;
    public static readonly List<Binding> Bindings = new List<Binding>();
    static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // "ACR075A:15" -> "Desktop"
    public static readonly Dictionary<string, List<int>> InputCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

    public static void Load()
    {
        ShowKey = DefaultShowKey; BrightnessStep = DefaultBrightnessStep;
        Bindings.Clear(); Labels.Clear(); InputCache.Clear();
        if (!File.Exists(FilePath))
        {
            // Defaults matching the old Start Menu shortcuts.
            Bindings.Add(new Binding { Key = Keys.Control | Keys.Alt | Keys.D3, MonitorId = "ACR075A", Input = 15 });
            Bindings.Add(new Binding { Key = Keys.Control | Keys.Alt | Keys.D4, MonitorId = "ACR075A", Input = 17 });
            Save();
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(FilePath); }
        catch (Exception ex) { Log.Error("Reading " + FilePath, ex); return; }

        // Lines: show=Ctrl+Alt+M | brightness_step=10 | hotkey=Ctrl+Alt+3,ACR075A,15 | label=ACR075A,15,Desktop | inputs=ACR075A,15,17
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            try { ParseLine(line); }
            catch (Exception ex) { Log.Info("settings.ini line " + (n + 1) + " ignored (" + ex.Message + "): " + line); }
        }
    }

    static void ParseLine(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) throw new FormatException("missing '='");
        var key = line.Substring(0, eq).Trim().ToLowerInvariant();
        var val = line.Substring(eq + 1).Trim();
        var f = val.Split(',').Select(s => s.Trim()).ToArray();

        switch (key)
        {
            case "show": ShowKey = KeyText.Parse(val); break;
            case "brightness_step": BrightnessStep = Math.Max(1, int.Parse(val)); break;
            case "hotkey":
                if (f.Length != 3) throw new FormatException("expected key,monitor,input");
                Bindings.Add(new Binding { Key = KeyText.Parse(f[0]), MonitorId = f[1], Input = int.Parse(f[2]) });
                break;
            case "label":
                if (f.Length < 3) throw new FormatException("expected monitor,input,name");
                Labels[LabelKey(f[0], int.Parse(f[1]))] = string.Join(",", f.Skip(2));
                break;
            case "inputs":
                if (f.Length < 2) throw new FormatException("expected monitor,input,...");
                InputCache[f[0]] = f.Skip(1).Select(int.Parse).ToList();
                break;
            default: throw new FormatException("unknown key");
        }
    }

    // Writes atomically (temp file + replace) so a crash mid-save can't truncate the file. Returns false on failure.
    public static bool Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MonitorSwitch settings. Input codes: 15=DisplayPort 1, 16=DisplayPort 2, 17=HDMI 1, 18=HDMI 2");
        sb.AppendLine("show=" + KeyText.Format(ShowKey));
        sb.AppendLine("brightness_step=" + BrightnessStep);
        foreach (var b in Bindings) sb.AppendLine("hotkey=" + KeyText.Format(b.Key) + "," + b.MonitorId + "," + b.Input);
        foreach (var kv in Labels)
        {
            var sep = kv.Key.LastIndexOf(':');
            sb.AppendLine("label=" + kv.Key.Substring(0, sep) + "," + kv.Key.Substring(sep + 1) + "," + kv.Value);
        }
        foreach (var kv in InputCache) sb.AppendLine("inputs=" + kv.Key + "," + string.Join(",", kv.Value));

        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Saving " + FilePath, ex);
            return false;
        }
    }

    public static string Label(string shortId, int input)
    {
        string l;
        return Labels.TryGetValue(LabelKey(shortId, input), out l) ? l : null;
    }

    // Empty/whitespace name removes the label. Commas would break the file format.
    public static void SetLabel(string shortId, int input, string name)
    {
        name = (name ?? "").Trim().Replace(",", " ");
        if (name.Length == 0) Labels.Remove(LabelKey(shortId, input));
        else Labels[LabelKey(shortId, input)] = name;
    }

    static string LabelKey(string shortId, int input) { return shortId + ":" + input; }
}

// Ctrl+Alt+3 <-> Keys.
static class KeyText
{
    public static string Format(Keys k)
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

    public static Keys Parse(string text)
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
        if ((k & Keys.KeyCode) == Keys.None) throw new FormatException("no key in '" + text + "'");
        return k;
    }
}

// HKCU Run entry.
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MonitorSwitch";

    public static bool Enabled
    {
        get
        {
            try { using (var r = Registry.CurrentUser.OpenSubKey(RunKey)) return r != null && r.GetValue(ValueName) != null; }
            catch (Exception ex) { Log.Error("Reading autostart", ex); return false; }
        }
        set
        {
            try
            {
                using (var r = Registry.CurrentUser.CreateSubKey(RunKey))
                    if (value) r.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else r.DeleteValue(ValueName, false);
            }
            catch (Exception ex) { Log.Error("Writing autostart", ex); }
        }
    }
}
