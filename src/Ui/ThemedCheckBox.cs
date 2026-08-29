using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// A checkbox that is legible on a dark form. Windows draws the themed glyph for a light
/// background and FlatStyle.Flat only tints it, so the box is drawn here instead: a checked
/// box fills green and carries a light tick, which reads at a glance.
/// </summary>
internal sealed class ThemedCheckBox : CheckBox
{
    private static readonly Color BoxBorder = Color.FromArgb(0x6A, 0x64, 0x59);
    private static readonly Color BoxFill = Color.FromArgb(0x2C, 0x2A, 0x26);
    private static readonly Color BoxFillChecked = Color.FromArgb(0x3F, 0x6E, 0x42);
    private static readonly Color BoxBorderChecked = Color.FromArgb(0x6C, 0xB0, 0x70);
    private static readonly Color Tick = Color.FromArgb(0xEC, 0xFF, 0xE8);

    private bool _hot;

    public ThemedCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int side = LogicalToDeviceUnits(14);
        var box = new Rectangle(0, (Height - side) / 2, side, side);

        using (var fill = new SolidBrush(Checked ? BoxFillChecked : BoxFill))
            g.FillRectangle(fill, box);
        using (var border = new Pen(Checked ? BoxBorderChecked : (_hot ? Color.FromArgb(0x8E, 0x86, 0x77) : BoxBorder)))
            g.DrawRectangle(border, box);

        if (Checked)
        {
            using var tick = new Pen(Tick, Math.Max(1.8f, side * 0.16f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLines(tick, new[]
            {
                new PointF(box.Left + side * 0.24f, box.Top + side * 0.52f),
                new PointF(box.Left + side * 0.44f, box.Top + side * 0.72f),
                new PointF(box.Left + side * 0.78f, box.Top + side * 0.28f)
            });
        }

        int textLeft = box.Right + LogicalToDeviceUnits(8);
        var textRect = new Rectangle(textLeft, 0, Width - textLeft, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(120, ForeColor)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, textLeft - LogicalToDeviceUnits(3), 1,
                Width - textLeft + LogicalToDeviceUnits(2), Height - 3);
        }
    }
}
