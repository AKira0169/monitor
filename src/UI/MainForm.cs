using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

class MainForm : Form
{
    const int HeaderHeight = 48, FooterHeight = 54, MinClientHeight = 220, MaxClientHeight = 760;

    readonly TrayApp app;
    readonly FlowLayoutPanel list;
    readonly Label status;
    readonly Label hotkeyInfo;
    readonly ContextMenuStrip inputMenu = new ContextMenuStrip();   // reused: one per right-click would leak
    List<MonitorInfo> monitors = new List<MonitorInfo>();
    int loadVersion;   // only the newest Reload may publish its result

    public MainForm(TrayApp app, IList<string> failedHotkeys)
    {
        this.app = app;
        Text = "MonitorSwitch"; Icon = AppIcon.Instance;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 360);
        Theme.StyleWindow(this, false);

        var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, Padding = new Padding(16, 12, 16, 0) };
        header.Controls.Add(new Label { Text = "Monitors", Font = Theme.Title, AutoSize = true, Location = new Point(16, 12) });
        var refresh = new FlatBtn("Refresh", "F5", false) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refresh.Click += (s, e) => Reload(true);
        header.Controls.Add(refresh);
        header.Layout += (s, e) => refresh.Location = new Point(header.Width - refresh.Width - 16, 10);

        list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(16, 4, 16, 8)
        };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, Padding = new Padding(16, 4, 16, 6) };
        hotkeyInfo = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small, AutoEllipsis = true };
        status = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Small };
        footer.Controls.Add(status); footer.Controls.Add(hotkeyInfo);
        // Bold ink rules close off the header and footer sections.
        header.Paint += (s, e) => Rule(e.Graphics, header.Height - Theme.Border);
        footer.Paint += (s, e) => Rule(e.Graphics, 0);

        Controls.Add(list); Controls.Add(footer); Controls.Add(header);
        RefreshHotkeys(failedHotkeys);

        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyDown += OnKey;
    }

    public void RefreshHotkeys(IList<string> failed)
    {
        if (failed.Count > 0)
        {
            hotkeyInfo.ForeColor = Theme.Warn;
            hotkeyInfo.Text = "Another app already uses " + string.Join(", ", failed) + ". Assign a different hotkey, or edit it from the tray menu.";
        }
        else
        {
            hotkeyInfo.ForeColor = Theme.Muted;
            hotkeyInfo.Text = KeyText.Format(Settings.ShowKey) + " shows this window.  Ctrl+Alt+↑↓ brightness, ←→ fine steps.";
        }
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); return; }
        if (e.KeyCode == Keys.F5) { Reload(true); return; }
        // 1..9 switch the first monitor to its Nth input.
        if (e.Modifiers == Keys.None && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
        {
            var m = NumberKeyTarget();
            int n = e.KeyCode - Keys.D1;
            if (m != null && n < m.Inputs.Count) DoSwitch(m, m.Inputs[n]);
        }
    }

    void Rule(Graphics g, int y)
    {
        using (var b = new SolidBrush(Theme.Ink)) g.FillRectangle(b, 0, y, ClientSize.Width, Theme.Border);
    }

    // The monitor whose inputs the 1-9 keys switch.
    MonitorInfo NumberKeyTarget() { return monitors.FirstOrDefault(x => x.DdcOk); }

    public void Reload(bool showLoading)
    {
        int version = ++loadVersion;
        if (showLoading) status.Text = "Reading monitors over DDC/CI…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Quick pass (no DDC reads) first so the window fills instantly, then the full read.
            if (showLoading) Publish(version, false);
            Publish(version, true);
        });
    }

    // Worker thread: reads monitors and hands the result to the UI thread.
    void Publish(int version, bool withDetails)
    {
        List<MonitorInfo> result; string err = null;
        try { result = Ddc.List(withDetails); }
        catch (Exception ex) { Log.Error("Reading monitors", ex); result = new List<MonitorInfo>(); err = ex.Message; }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || version != loadVersion) return;
                monitors = result;
                Render();
                if (withDetails) status.Text = err ?? StatusText(result.Count);
            }));
        }
        catch (InvalidOperationException) { }   // form handle gone
    }

    static string StatusText(int count)
    {
        return count + " monitor" + (count == 1 ? "" : "s") + ".  Right-click an input to set its hotkey or name.  Esc hides.";
    }

    void Render()
    {
        list.SuspendLayout();
        var old = list.Controls.Cast<Control>().ToList();
        list.Controls.Clear();
        foreach (var c in old) c.Dispose();   // Controls.Clear() alone leaks window handles and timers

        int width = list.ClientSize.Width - list.Padding.Horizontal - 2;
        var numbered = NumberKeyTarget();
        foreach (var m in monitors) list.Controls.Add(Card(m, width, m == numbered));
        if (monitors.Count == 0)
            list.Controls.Add(new Label { Text = "No monitors found. Check the cables, then press F5.", ForeColor = Theme.Muted, AutoSize = true });
        list.ResumeLayout();

        // Grow the window to fit the cards (up to a limit) so there is no scrolling in the usual case.
        int needed = list.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + list.Padding.Vertical + HeaderHeight + FooterHeight;
        ClientSize = new Size(ClientSize.Width, Math.Max(MinClientHeight, Math.Min(needed, MaxClientHeight)));
    }

    Control Card(MonitorInfo m, int width, bool numberKeys)
    {
        var card = new MonitorCard(m, width, numberKeys);
        card.SwitchRequested += DoSwitch;
        card.InputMenuRequested += ShowInputMenu;
        card.BrightnessChanged += (mon, value) => app.SetBrightness(mon.ShortId, mon.Device, value);
        return card;
    }

    void ShowInputMenu(MonitorInfo m, int code, Control anchor, Point at)
    {
        var items = inputMenu.Items.Cast<ToolStripItem>().ToList();
        inputMenu.Items.Clear();
        foreach (var i in items) i.Dispose();

        inputMenu.Items.Add("Assign hotkey…", null, (s, e) => AssignHotkey(m, code));
        inputMenu.Items.Add("Remove hotkey", null, (s, e) =>
        {
            Settings.Bindings.RemoveAll(b => IsBindingFor(b, m, code));
            ApplySettings();
        });
        inputMenu.Items.Add("Rename…", null, (s, e) =>
        {
            var name = Dialogs.AskText(this, "Name for " + Ddc.InputName(code) + " (e.g. Desktop, Laptop)", Settings.Label(m.ShortId, code) ?? "");
            if (name == null) return;
            Settings.SetLabel(m.ShortId, code, name);
            ApplySettings();
        });
        inputMenu.Show(anchor, at);
    }

    void AssignHotkey(MonitorInfo m, int code)
    {
        var k = Dialogs.CaptureHotkey(this, Ddc.InputName(code));
        if (k == Keys.None) return;
        Settings.Bindings.RemoveAll(b => b.Key == k || IsBindingFor(b, m, code));
        Settings.Bindings.Add(new Binding { Key = k, MonitorId = m.ShortId, Input = code });
        ApplySettings();
    }

    // Only bindings for this exact monitor; a "*" binding is left alone.
    static bool IsBindingFor(Binding b, MonitorInfo m, int code)
    {
        return b.Input == code && string.Equals(b.MonitorId, m.ShortId, StringComparison.OrdinalIgnoreCase);
    }

    void ApplySettings()
    {
        app.SettingsChanged();
        Render();
    }

    void DoSwitch(MonitorInfo m, int code)
    {
        status.Text = "Switching " + m.Name + " to " + Ddc.InputName(code) + "…";
        app.Switch(m.ShortId, m.Device, code);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inputMenu.Dispose();
        base.Dispose(disposing);
    }
}
