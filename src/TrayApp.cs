using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

// Owns the tray icon, hotkeys and main window; runs DDC work on the thread pool.
class TrayApp : IDisposable
{
    const string AppName = "MonitorSwitch";

    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkeys;
    readonly Control ui = new Control();   // marshals worker-thread results back to the UI thread
    readonly Osd osd = new Osd();
    MainForm form;

    // Key repeat fires faster than DDC can write, so presses are summed and applied by one worker.
    readonly object brightLock = new object();
    int pendingDelta;
    bool adjusting;

    public TrayApp(bool hidden)
    {
        ui.CreateControl();
        Settings.Load();
        Ddc.SeedInputs(Settings.InputCache);
        Ddc.InputsDiscovered += (id, inputs) => OnUi(() =>
        {
            Settings.InputCache[id] = inputs;
            Settings.Save();
        });
        // Keep the startup entry pointing at wherever this exe lives now (it may have been moved).
        if (Autostart.Enabled) Autostart.Enabled = true;

        hotkeys = new HotkeyWindow();
        hotkeys.ShowRequested += ShowWindow;
        hotkeys.ToggleRequested += ToggleWindow;
        hotkeys.BindingPressed += b => Switch(b.MonitorId, null, b.Input);
        hotkeys.BrightnessPressed += AdjustBrightness;
        hotkeys.Register();

        tray = new NotifyIcon { Icon = AppIcon.Instance, Text = AppName, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
        BuildMenu();

        if (!hidden) ShowWindow();
        WarnFailedHotkeys();
        Log.Info("Started");
    }

    void BuildMenu()
    {
        var menu = tray.ContextMenuStrip;
        DisposeItems(menu.Items);
        menu.Items.Add("Open", null, (s, e) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var b in Settings.Bindings)
        {
            var binding = b;
            var text = (Settings.Label(b.MonitorId, b.Input) ?? Ddc.InputName(b.Input)) + "   (" + KeyText.Format(b.Key) + ")";
            menu.Items.Add(text, null, (s, e) => Switch(binding.MonitorId, null, binding.Input));
        }
        menu.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled };
        auto.Click += (s, e) => Autostart.Enabled = !auto.Checked;
        menu.Items.Add(auto);
        menu.Items.Add("Edit settings file", null, (s, e) => EditSettings());
        menu.Items.Add("Reload settings", null, (s, e) => ReloadHotkeys(true));
        menu.Items.Add("Exit", null, (s, e) => Application.Exit());
    }

    static void DisposeItems(ToolStripItemCollection items)
    {
        var old = new List<ToolStripItem>();
        foreach (ToolStripItem i in items) old.Add(i);
        items.Clear();
        foreach (var i in old) i.Dispose();
    }

    void EditSettings()
    {
        if (!Settings.Save()) { Warn("Could not write " + Settings.FilePath); return; }
        try { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); }
        catch (Exception ex) { Log.Error("Opening settings", ex); Warn(ex.Message); }
    }

    // Persists settings and re-registers hotkeys after the UI changed a binding or label.
    public void SettingsChanged()
    {
        if (!Settings.Save()) Warn("Could not save settings (see " + Log.FilePath + ")");
        ReloadHotkeys(false);
    }

    public void ReloadHotkeys(bool fromDisk)
    {
        if (fromDisk)
        {
            Settings.Load();
            Ddc.SeedInputs(Settings.InputCache);
        }
        hotkeys.Register();
        WarnFailedHotkeys();
        if (FormVisible) form.RefreshHotkeys(hotkeys.Failed);
    }

    void WarnFailedHotkeys()
    {
        if (hotkeys.Failed.Count > 0) Warn("Hotkey already in use: " + string.Join(", ", hotkeys.Failed));
    }

    void Warn(string message) { tray.ShowBalloonTip(3000, AppName, message, ToolTipIcon.Warning); }

    void AdjustBrightness(int delta)
    {
        lock (brightLock)
        {
            pendingDelta += delta;
            if (adjusting) return;
            adjusting = true;
        }
        RunInBackground("Brightness", () =>
        {
            try
            {
                while (true)
                {
                    int d;
                    lock (brightLock)
                    {
                        d = pendingDelta; pendingDelta = 0;
                        if (d == 0) { adjusting = false; break; }   // same lock as the check, so no press is lost
                    }
                    int v = Ddc.AdjustBrightness(d);
                    OnUi(() =>
                    {
                        if (v >= 0) osd.ShowValue(v);
                        else Warn("Brightness: no DDC/CI monitor on this PC");
                    });
                }
            }
            catch
            {
                lock (brightLock) adjusting = false;   // don't leave the brightness keys stuck
                throw;
            }
            OnUi(RefreshForm);
        });
    }

    // device narrows to one display (from the UI); null means every monitor with that id (hotkeys).
    public void Switch(string shortId, string device, int input)
    {
        RunInBackground("Switch input", () =>
        {
            var err = Ddc.Set(shortId, device, Ddc.VCP_INPUT, input);
            OnUi(() =>
            {
                if (err != null) Warn(err);
                RefreshForm();
            });
        });
    }

    public void SetBrightness(string shortId, string device, int value)
    {
        RunInBackground("Set brightness", () =>
        {
            var err = Ddc.Set(shortId, device, Ddc.VCP_BRIGHTNESS, value);
            if (err != null) OnUi(() => Warn(err));
        });
    }

    bool FormVisible { get { return form != null && !form.IsDisposed && form.Visible; } }

    void RefreshForm() { if (FormVisible) form.Reload(false); }

    void ShowWindow()
    {
        if (form == null || form.IsDisposed) form = new MainForm(this, hotkeys.Failed);
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.Reload(true);
    }

    void ToggleWindow()
    {
        if (FormVisible && Form.ActiveForm == form) form.Hide();
        else ShowWindow();
    }

    void OnUi(Action a)
    {
        if (ui.IsDisposed) return;
        try { ui.BeginInvoke(a); }
        catch (InvalidOperationException) { }   // shutting down
    }

    static void RunInBackground(string what, Action work)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { work(); }
            catch (Exception ex) { Log.Error(what, ex); }
        });
    }

    public void Dispose()
    {
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Dispose();
        hotkeys.Dispose();
        osd.Dispose();
        if (form != null) form.Dispose();
        ui.Dispose();
        Log.Info("Exited");
    }
}
