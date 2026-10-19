using System;
using System.Drawing;
using System.Windows.Forms;

// Small brightness indicator near the bottom of the screen; never takes focus. Drawn like the window's brightness bar.
class Osd : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, Inset = 8;

    readonly Timer hideTimer = new Timer { Interval = 1200 };
    int value;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Theme.Bg; Font = Theme.Body;
        Size = new Size(280, Font.Height * 2 + Inset * 2); DoubleBuffered = true;
        Theme.StyleWindow(this, true);
        hideTimer.Tick += (s, e) => { hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ShowValue(int percent)
    {
        value = Math.Max(0, Math.Min(100, percent));
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 80);
        if (!Visible) Show();
        Invalidate();
        hideTimer.Stop(); hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Meter.Draw(e.Graphics, Rectangle.Inflate(ClientRectangle, -Inset, -Inset), "Brightness", value, 100, Font);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
