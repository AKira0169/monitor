using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Small brightness indicator near the bottom of the screen; never takes focus.
class Osd : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;

    readonly Timer hideTimer = new Timer { Interval = 1200 };
    int value;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; BackColor = Theme.Card;
        Size = new Size(260, 56); DoubleBuffered = true;
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
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(Theme.Text)) g.DrawString("☀  " + value, Theme.Title, b, 14, 14);
        var bar = new Rectangle(90, 25, Width - 108, 6);
        using (var b = new SolidBrush(Theme.Btn)) g.FillRectangle(b, bar);
        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, bar.X, bar.Y, bar.Width * value / 100, bar.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
