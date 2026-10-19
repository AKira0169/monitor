using System;
using System.Collections.Generic;
using System.Windows.Forms;

// Long-lived hidden top-level window (survives the main form closing). It owns the global hotkeys and,
// because it is not message-only, also receives the HWND_BROADCAST "show me" from a second instance.
class HotkeyWindow : NativeWindow, IDisposable
{
    const int ShowId = 0, BindingBaseId = 1, BrightnessBaseId = 100;
    static readonly Keys[] BrightnessKeys = { Keys.Up, Keys.Down, Keys.Right, Keys.Left };

    readonly List<int> registered = new List<int>();
    List<Binding> bindings = new List<Binding>();   // snapshot taken at Register so ids stay valid
    readonly List<string> failed = new List<string>();

    public event Action ShowRequested;     // second instance launched
    public event Action ToggleRequested;   // show/hide hotkey
    public event Action<Binding> BindingPressed;
    public event Action<int> BrightnessPressed;   // signed delta

    public HotkeyWindow() { CreateHandle(new CreateParams { Caption = "MonitorSwitch.Listener" }); }

    // Hotkeys that could not be registered because another app owns them, as display text.
    public IList<string> Failed { get { return failed.AsReadOnly(); } }

    public void Register()
    {
        UnregisterAll();
        failed.Clear();
        bindings = new List<Binding>(Settings.Bindings);
        Add(ShowId, Settings.ShowKey, true);
        for (int i = 0; i < bindings.Count; i++) Add(BindingBaseId + i, bindings[i].Key, true);
        // Brightness keys auto-repeat while held.
        for (int i = 0; i < BrightnessKeys.Length; i++) Add(BrightnessBaseId + i, Keys.Control | Keys.Alt | BrightnessKeys[i], false);
    }

    void Add(int id, Keys k, bool noRepeat)
    {
        uint mods = noRepeat ? Native.MOD_NOREPEAT : 0;
        if ((k & Keys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((k & Keys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((k & Keys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if (Native.RegisterHotKey(Handle, id, mods, (uint)(k & Keys.KeyCode))) registered.Add(id);
        else failed.Add(KeyText.Format(k));
    }

    void UnregisterAll()
    {
        foreach (var id in registered) Native.UnregisterHotKey(Handle, id);
        registered.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) { Dispatch(m.WParam.ToInt32()); return; }
        if (m.Msg == Native.WM_SHOWME) Raise(ShowRequested);
        base.WndProc(ref m);
    }

    void Dispatch(int id)
    {
        if (id == ShowId) { Raise(ToggleRequested); return; }

        if (id >= BrightnessBaseId && id < BrightnessBaseId + BrightnessKeys.Length)
        {
            var k = BrightnessKeys[id - BrightnessBaseId];
            int step = k == Keys.Up || k == Keys.Down ? Settings.BrightnessStep : Settings.FineBrightnessStep;
            var handler = BrightnessPressed;
            if (handler != null) handler(k == Keys.Up || k == Keys.Right ? step : -step);
            return;
        }

        int index = id - BindingBaseId;
        if (index >= 0 && index < bindings.Count)
        {
            var handler = BindingPressed;
            if (handler != null) handler(bindings[index]);
        }
    }

    static void Raise(Action a) { if (a != null) a(); }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        UnregisterAll();
        DestroyHandle();
    }
}
