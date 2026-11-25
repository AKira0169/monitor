using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// One monitor: title, details, input buttons and brightness slider.
class MonitorCard : Panel
{
    const int BrightnessDebounceMs = 150;

    readonly MonitorInfo monitor;
    Timer brightnessTimer;

    public event Action<MonitorInfo, int> SwitchRequested;             // input code
    public event Action<MonitorInfo, int, Control, Point> InputMenuRequested;
    public event Action<MonitorInfo, int> BrightnessChanged;           // raw VCP value, debounced

    // numberKeys: this is the monitor the 1-9 keys switch, so its buttons show those keys.
    public MonitorCard(MonitorInfo m, int width, bool numberKeys)
    {
        monitor = m;
        Width = width; BackColor = Theme.Card;
        Margin = new Padding(0, 0, 0, 12); Padding = new Padding(14, 12, 14, 6);

        var title = new Label { Text = m.Name, Font = Theme.Title, AutoSize = true, Location = new Point(14, 10) };
        Controls.Add(title);
        if (m.Primary)
            Controls.Add(new Label { Text = "Primary", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(title.Right + 6, 16) });
        Controls.Add(new Label
        {
            Text = m.Width + " × " + m.Height + ", " + m.Hz + " Hz\n" + m.Device + " (" + m.UnitId + ") on " + m.Adapter + ", at " + m.X + "," + m.Y,
            ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, 38)
        });

        int y = AddSupport(82);
        if (m.DdcOk) y = AddInputs(y, numberKeys);
        if (m.Brightness >= 0) y = AddBrightness(y);
        Height = y + 12;
    }

    // Compact "what works on this display" line, with the most likely reason when nothing does.
    int AddSupport(int y)
    {
        var m = monitor;
        string text; bool warn = false;
        if (!m.Loaded) text = "Reading inputs…";
        else if (m.SwitchedAway) text = "Switched to another input, so it isn't answering (normal). It returns when it shows this PC again.";
        else if (m.DdcOk)
        {
            string via = m.BrightnessVia == BrightnessSource.Wmi ? "Windows" : "DDC/CI";
            text = "Input switching " + (m.CanSwitchInput ? "✓" : "✗ (no input list)") + " · Brightness " + (m.Brightness >= 0 ? "✓ (" + via + ")" : "✗");
        }
        else if (m.BrightnessVia == BrightnessSource.Wmi) text = "Brightness only (Windows/laptop panel). Input switching isn't available for this display.";
        else
        {
            warn = true;
            text = "Not controllable: DDC/CI did not respond. Enable DDC/CI in the monitor's on-screen menu and avoid docks, adapters or KVMs that block it, then press F5.";
            if (m.NoHandle) text = "Not controllable: Windows exposes no DDC/CI for this display (virtual display, or a laptop panel without brightness support).";
            else if (m.Adapter != null && (m.Adapter.IndexOf("DisplayLink", StringComparison.OrdinalIgnoreCase) >= 0 || m.Adapter.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0))
                text = "Not controllable: this display is behind a USB/DisplayLink adapter, which usually blocks DDC/CI.";
            if (m.Quirk != null && !string.IsNullOrEmpty(m.Quirk.Note)) text += " " + m.Quirk.Note;
        }
        var lbl = new Label
        {
            Text = text, Font = Theme.Small, ForeColor = warn ? Theme.Warn : Theme.Muted,
            AutoSize = true, MaximumSize = new Size(Width - 32, 0), Location = new Point(16, y)
        };
        Controls.Add(lbl);
        return y + lbl.GetPreferredSize(new Size(Width - 32, 0)).Height + 8;
    }

    int AddInputs(int y, bool numberKeys)
    {
        var inputs = new FlowLayoutPanel { Location = new Point(14, y), Width = Width - 28, AutoSize = true, MaximumSize = new Size(Width - 28, 0), WrapContents = true };
        for (int i = 0; i < monitor.Inputs.Count; i++)
        {
            int code = monitor.Inputs[i];
            var label = Settings.Label(monitor, code);
            var hk = Settings.FindBinding(monitor, code);
            var text = (numberKeys && i < 9 ? (i + 1) + "   " : "") + (label ?? Ddc.InputName(code));
            var hint = (label != null ? Ddc.InputName(code) : "") + (label != null && hk != null ? "  " : "") + (hk != null ? KeyText.Format(hk.Key) : "");
            var btn = new FlatBtn(text, hint.Length > 0 ? hint : null, code == monitor.Input);
            btn.Click += (s, e) => { var h = SwitchRequested; if (h != null) h(monitor, code); };
            btn.MouseUp += (s, e) =>
            {
                var h = InputMenuRequested;
                if (e.Button == MouseButtons.Right && h != null) h(monitor, code, btn, e.Location);
            };
            inputs.Controls.Add(btn);
        }
        Controls.Add(inputs);
        return y + inputs.PreferredSize.Height;
    }

    int AddBrightness(int y)
    {
        var bar = new Meter
        {
            Caption = "Brightness", Maximum = monitor.BrightnessMax, Value = monitor.Brightness,
            Location = new Point(14, y), Width = Width - 28
        };
        // DDC writes are slow; only send once the slider has settled.
        brightnessTimer = new Timer { Interval = BrightnessDebounceMs };
        brightnessTimer.Tick += (s, e) =>
        {
            brightnessTimer.Stop();
            var h = BrightnessChanged;
            if (h != null) h(monitor, bar.Value);
        };
        bar.ValueChanged += () =>
        {
            monitor.Brightness = bar.Value;
            brightnessTimer.Stop(); brightnessTimer.Start();
        };
        Controls.Add(bar);
        return y + bar.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Frame(e.Graphics, ClientRectangle, Theme.Card);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && brightnessTimer != null)
        {
            // Flush a pending slider change rather than dropping it when the card is re-rendered.
            if (brightnessTimer.Enabled) { brightnessTimer.Stop(); var h = BrightnessChanged; if (h != null) h(monitor, monitor.Brightness); }
            brightnessTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
