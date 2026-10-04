using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// A setting that is either on or off, drawn as the panel draws everything else: a recessed
/// square with a lamp in it, and the label engraved beside it.
///
/// This exists because a WinForms CheckBox paints its own rectangle of background, which on a
/// gradient chassis shows up as a patch. Being a <see cref="ChassisControl"/> it blits the
/// matching slice of the chassis instead, so it genuinely sits on the metal.
/// </summary>
internal sealed class PlateCheck : ChassisControl
{
    private bool _hot;
    private bool _checked;

    public PlateCheck() => Cursor = Cursors.Hand;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            CheckedChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public event EventHandler? CheckedChanged;

    public Color LampColor { get; set; } = PanelTheme.LampGreen;

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackdrop(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int box = Scaled(16);
        var well = new RectangleF(0.5f, (Height - box) / 2f, box, box);

        PanelTheme.DrawRecess(g, well, Scaled(3));

        float lamp = box * 0.56f;
        var lampRect = new RectangleF(well.X + (box - lamp) / 2f, well.Y + (box - lamp) / 2f, lamp, lamp);
        PanelTheme.DrawLamp(g, lampRect, LampColor, Checked, 0.7f);

        int textLeft = box + Scaled(10);
        Color ink = Checked
            ? PanelTheme.TextPrimary
            : (_hot ? PanelTheme.TextPrimary : PanelTheme.TextSecondary);

        PanelTheme.DrawEngraved(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft, Height),
            ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        if (Focused)
        {
            using var pen = new Pen(Color.FromArgb(120, PanelTheme.TextPrimary)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
