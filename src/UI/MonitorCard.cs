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

    public MonitorCard(MonitorInfo m, int width)
    {
        monitor = m;
        Width = width; BackColor = Theme.Card;
        Margin = new Padding(0, 0, 0, 12); Padding = new Padding(14, 12, 14, 6);

        Controls.Add(new Label { Text = m.Name + (m.Primary ? "   ★ primary" : ""), Font = Theme.Title, AutoSize = true, Location = new Point(14, 10) });
        Controls.Add(new Label
        {
            Text = m.Width + "×" + m.Height + " @ " + m.Hz + " Hz   ·   " + m.Device + "   ·   " + m.ShortId + "\n" + m.Adapter
                + "   ·   at " + m.X + "," + m.Y,
            ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, 38)
        });

        int y = 82;
        if (!m.DdcOk)
        {
            Controls.Add(new Label
            {
                Text = m.Loaded ? "DDC/CI not available (enable DDC/CI in the monitor's OSD menu)" : "Reading inputs…",
                ForeColor = m.Loaded ? Theme.Warn : Theme.Muted, AutoSize = true, Location = new Point(16, y)
            });
            Height = y + 34;
            return;
        }

        y = AddInputs(y);
        if (m.Brightness >= 0) y = AddBrightness(y);
        Height = y + 6;
    }

    int AddInputs(int y)
    {
        Controls.Add(new Label { Text = "INPUT", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, y) });
        y += 22;
        var inputs = new FlowLayoutPanel { Location = new Point(14, y), Width = Width - 28, AutoSize = true, MaximumSize = new Size(Width - 28, 0), WrapContents = true };
        for (int i = 0; i < monitor.Inputs.Count; i++)
        {
            int code = monitor.Inputs[i];
            var label = Settings.Label(monitor.ShortId, code);
            var hk = Settings.Bindings.FirstOrDefault(b => b.Targets(monitor.ShortId, code));
            var text = (i + 1) + "  " + (label != null ? label + " · " : "") + Ddc.InputName(code) + (hk != null ? "   " + KeyText.Format(hk.Key) : "");
            var btn = new FlatBtn(text, code == monitor.Input);
            btn.Click += (s, e) => { var h = SwitchRequested; if (h != null) h(monitor, code); };
            btn.MouseUp += (s, e) =>
            {
                var h = InputMenuRequested;
                if (e.Button == MouseButtons.Right && h != null) h(monitor, code, btn, e.Location);
            };
            inputs.Controls.Add(btn);
        }
        Controls.Add(inputs);
        return y + inputs.PreferredSize.Height + 6;
    }

    int AddBrightness(int y)
    {
        var caption = new Label { Text = "BRIGHTNESS  " + monitor.Brightness, ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Location = new Point(16, y) };
        Controls.Add(caption);
        y += 20;
        var bar = new NoWheelTrackBar
        {
            Minimum = 0, Maximum = monitor.BrightnessMax, Value = Math.Max(0, Math.Min(monitor.Brightness, monitor.BrightnessMax)),
            TickStyle = TickStyle.None, Location = new Point(10, y), Width = Width - 24, BackColor = Theme.Card
        };
        // DDC writes are slow; only send once the slider has settled.
        brightnessTimer = new Timer { Interval = BrightnessDebounceMs };
        brightnessTimer.Tick += (s, e) =>
        {
            brightnessTimer.Stop();
            var h = BrightnessChanged;
            if (h != null) h(monitor, bar.Value);
        };
        bar.Scroll += (s, e) =>
        {
            caption.Text = "BRIGHTNESS  " + bar.Value;
            monitor.Brightness = bar.Value;
            brightnessTimer.Stop(); brightnessTimer.Start();
        };
        Controls.Add(bar);
        return y + bar.Height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(Theme.CardBorder)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
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
