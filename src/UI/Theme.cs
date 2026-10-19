using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

static class Theme
{
    // Two-tone: electric-lime surface, near-black ink, beige raised controls with 2px ink borders.
    public static readonly Color Bg = Color.FromArgb(204, 255, 0);           // lime surface
    public static readonly Color Ink = Color.FromArgb(20, 22, 12);           // all text, borders, active fills
    public static readonly Color Card = Color.FromArgb(240, 230, 204);       // beige raised surface
    public static readonly Color CardBorder = Ink;
    public static readonly Color Text = Ink;
    public static readonly Color Muted = Color.FromArgb(78, 84, 52);         // secondary text, readable on lime and beige
    public static readonly Color Accent = Ink;                               // active input, meter fill
    public static readonly Color AccentText = Bg;                            // text on Accent
    public static readonly Color AccentHint = Color.FromArgb(160, 200, 30);  // secondary text on Accent
    public static readonly Color Btn = Card;
    public static readonly Color BtnHover = Color.FromArgb(226, 213, 178);
    public static readonly Color BtnDown = Color.FromArgb(208, 193, 152);
    public static readonly Color Warn = Color.FromArgb(150, 36, 0);
    public const int Border = 2;
    public static readonly Font Body = new Font("Segoe UI", 9.5f);
    public static readonly Font Small = new Font("Segoe UI", 8.5f);
    public static readonly Font Title = new Font("Consolas", 12.5f, FontStyle.Bold);   // terminal-style display face, ships with Windows
    public static readonly Font BtnFont = new Font("Segoe UI Semibold", 9.5f);
    public static readonly Font Mono = new Font("Consolas", 8.5f);                      // key and port hints

    // Lime caption bar with ink title (Windows 11; ignored elsewhere). small: crisp small-radius corners and an ink border.
    public static void StyleWindow(Form f, bool small)
    {
        f.HandleCreated += (s, e) =>
        {
            int caption = ColorTranslator.ToWin32(Bg), text = ColorTranslator.ToWin32(Ink), border = text;
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_CAPTION_COLOR, ref caption, 4);
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_TEXT_COLOR, ref text, 4);
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_BORDER_COLOR, ref border, 4);
            if (!small) return;
            int corner = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
        };
    }

    // 2px ink frame inside r, filled with fill.
    public static void Frame(Graphics g, Rectangle r, Color fill)
    {
        using (var b = new SolidBrush(Ink)) g.FillRectangle(b, r);
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, Rectangle.Inflate(r, -Border, -Border));
    }
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
                using (var b = new SolidBrush(Theme.Ink)) g.FillRectangle(b, 2, 4, 28, 19);
                using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 5, 7, 22, 13);
                using (var b = new SolidBrush(Theme.Ink)) { g.FillRectangle(b, 13, 23, 6, 4); g.FillRectangle(b, 8, 27, 16, 3); }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

// Owner-drawn button: main text plus an optional dimmer hint on the right (input port, hotkey, shortcut key).
class FlatBtn : Button
{
    const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    readonly string hint;
    bool active, hover, down;

    public FlatBtn(string text, bool active) : this(text, null, active) { }

    public FlatBtn(string text, string hint, bool active)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = text; this.hint = hint; this.active = active;
        Font = Theme.BtnFont; Cursor = Cursors.Hand;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Font.Height * 2 / 3, Font.Height / 3, Font.Height * 2 / 3, Font.Height / 3);
        Margin = new Padding(0, 0, 8, 8);
    }

    int HintGap { get { return Font.Height; } }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        if (hint != null) s.Width += HintGap + TextRenderer.MeasureText(hint, Theme.Mono, Size.Empty, Flags).Width;
        return new Size(s.Width + Padding.Horizontal, s.Height + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (down) { down = false; Invalidate(); } base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bg = active ? Theme.Accent : down ? Theme.BtnDown : hover ? Theme.BtnHover : Theme.Btn;
        var fg = active ? Theme.AccentText : Theme.Text;
        Theme.Frame(g, ClientRectangle, bg);

        var r = new Rectangle(Padding.Left, 0, Width - Padding.Horizontal, Height);
        if (hint == null)
            TextRenderer.DrawText(g, Text, Font, r, fg, Flags | TextFormatFlags.HorizontalCenter);
        else
        {
            TextRenderer.DrawText(g, Text, Font, r, fg, Flags);
            TextRenderer.DrawText(g, hint, Theme.Mono, r, active ? Theme.AccentHint : Theme.Muted, Flags | TextFormatFlags.Right);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3), fg, bg);
    }
}

// Owner-drawn level bar with its caption and value inside: one control instead of TrackBar + label.
// Mouse only (click to jump, drag to adjust): scrolling over the window or keys (Ctrl+Alt+arrows) must not move it.
class Meter : Control
{
    int value, max = 100;
    bool dragging;

    public string Caption;
    public event Action ValueChanged;   // user drags only, not programmatic sets

    public Meter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false; Cursor = Cursors.Hand; Font = Theme.Small;
        Height = Font.Height * 2;
    }

    public int Maximum { get { return max; } set { max = Math.Max(1, value); Invalidate(); } }
    public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(value, max)); Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true; Capture = true;
        Track(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Track(e.X); }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

    void Track(int x)
    {
        int v = Math.Max(0, Math.Min(max, (int)Math.Round((double)x * max / Math.Max(1, Width - 1))));
        if (v == value) return;
        value = v;
        Invalidate();
        var h = ValueChanged;
        if (h != null) h();
    }

    protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics, ClientRectangle, Caption, value, max, Font); }

    // Shared with the OSD so the overlay looks like the slider it mirrors.
    public static void Draw(Graphics g, Rectangle r, string caption, int value, int max, Font font)
    {
        Theme.Frame(g, r, Theme.Btn);
        var inner = Rectangle.Inflate(r, -Theme.Border, -Theme.Border);
        var fill = new Rectangle(inner.X, inner.Y, (int)((long)inner.Width * value / Math.Max(1, max)), inner.Height);
        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, fill);

        // Text is ink over the empty track and lime over the fill, split exactly at the fill edge.
        var state = g.Save();
        g.SetClip(fill, CombineMode.Exclude);
        DrawText(g, inner, caption, value, font, Theme.Text);
        g.Restore(state);
        state = g.Save();
        g.SetClip(fill);
        DrawText(g, inner, caption, value, font, Theme.AccentText);
        g.Restore(state);
    }

    static void DrawText(Graphics g, Rectangle r, string caption, int value, Font font, Color color)
    {
        const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        r = Rectangle.Inflate(r, -font.Height * 2 / 3, 0);
        if (caption != null) TextRenderer.DrawText(g, caption, font, r, color, f);
        TextRenderer.DrawText(g, value.ToString(), Theme.Title, r, color, f | TextFormatFlags.Right);
    }
}
