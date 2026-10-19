using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 27);
    public static readonly Color Card = Color.FromArgb(36, 36, 41);
    public static readonly Color CardBorder = Color.FromArgb(56, 56, 63);
    public static readonly Color Text = Color.FromArgb(236, 236, 240);
    public static readonly Color Muted = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(77, 163, 255);
    public static readonly Color Btn = Color.FromArgb(50, 50, 57);
    public static readonly Color BtnHover = Color.FromArgb(66, 66, 75);
    public static readonly Color Warn = Color.FromArgb(240, 170, 80);
    public static readonly Font Body = new Font("Segoe UI", 9.5f);
    public static readonly Font Small = new Font("Segoe UI", 8.5f);
    public static readonly Font Title = new Font("Segoe UI Semibold", 12f);
    public static readonly Font BtnFont = new Font("Segoe UI Semibold", 9.5f);
}

// Drawn once and shared: Icon.FromHandle does not own the HICON, so making one per window leaks it.
static class AppIcon
{
    public static readonly Icon Instance = Make();

    static Icon Make()
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 2, 4, 28, 19);
                using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 5, 7, 22, 13);
                using (var b = new SolidBrush(Theme.Accent)) { g.FillRectangle(b, 13, 23, 6, 4); g.FillRectangle(b, 8, 27, 16, 3); }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

class FlatBtn : Button
{
    public FlatBtn(string text, bool active)
    {
        Text = text; FlatStyle = FlatStyle.Flat; Font = Theme.BtnFont; Cursor = Cursors.Hand;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(10, 4, 10, 4);
        Margin = new Padding(0, 0, 8, 8);
        SetActive(active);
    }

    public void SetActive(bool active)
    {
        BackColor = active ? Theme.Accent : Theme.Btn;
        ForeColor = active ? Color.White : Theme.Text;
        FlatAppearance.BorderColor = active ? Theme.Accent : Theme.CardBorder;
        FlatAppearance.MouseOverBackColor = active ? Theme.Accent : Theme.BtnHover;
    }
}

// Mouse-drag only: scrolling over the window or keys (Ctrl+Alt+arrows) must not move it.
class NoWheelTrackBar : TrackBar
{
    const int WM_KEYFIRST = 0x0100, WM_KEYLAST = 0x0109, WM_MOUSEWHEEL = 0x020A;

    public NoWheelTrackBar() { TabStop = false; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL || (m.Msg >= WM_KEYFIRST && m.Msg <= WM_KEYLAST)) return;
        base.WndProc(ref m);
    }
}
