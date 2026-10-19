using System.Drawing;
using System.Windows.Forms;

static class Dialogs
{
    // Returns the pressed combination, or Keys.None if cancelled.
    public static Keys CaptureHotkey(IWin32Window owner, string what)
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
                if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey) { lbl.Text = KeyText.Format(e.Modifiers) + "+…"; return; }
                if ((e.Modifiers & (Keys.Control | Keys.Alt)) == 0) { lbl.Text = "Use Ctrl and/or Alt with the key"; return; }
                result = code | e.Modifiers; f.Close();
            };
            f.ShowDialog(owner);
        }
        return result;
    }

    // Returns the entered text, or null if cancelled.
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
