using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// A small stamped-metal key used for the panel chrome: title-bar controls, the footer
/// commands, and toggles. When <see cref="ShowLamp"/> is set it carries its own indicator,
/// so a toggle reads the same way as the device keys do.
/// </summary>
internal sealed class PlateButton : ChassisControl
{
    private bool _hot;
    private bool _pressed;

    public bool ShowLamp { get; set; }
    public bool IsOn { get; set; }
    public Color LampColor { get; set; } = PanelTheme.LampGreen;

    /// <summary>Tints the key red on hover; used for the close control.</summary>
    public bool Destructive { get; set; }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (_pressed) { _pressed = false; Invalidate(); } base.OnMouseUp(e); }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
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

        float radius = Scaled(4);
        var face = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        Color top, bottom;
        if (_pressed)
        {
            top = PanelTheme.ButtonBottom;
            bottom = PanelTheme.ButtonTop;
        }
        else if (_hot && Destructive)
        {
            top = Color.FromArgb(0x7A, 0x3A, 0x30);
            bottom = Color.FromArgb(0x53, 0x26, 0x20);
        }
        else if (_hot)
        {
            top = PanelTheme.ButtonTopHot;
            bottom = PanelTheme.ButtonBottomHot;
        }
        else
        {
            top = PanelTheme.ButtonTop;
            bottom = PanelTheme.ButtonBottom;
        }

        using (var path = PanelTheme.RoundedRect(face, radius))
        {
            using (var fill = new LinearGradientBrush(
                       new RectangleF(face.X, face.Y, face.Width, face.Height + 1), top, bottom, 90f))
                g.FillPath(fill, path);
            using (var bevel = new Pen(Color.FromArgb(_pressed ? 18 : 60, 255, 255, 255)))
                g.DrawLine(bevel, face.X + radius, face.Y + 1f, face.Right - radius, face.Y + 1f);
            using (var outline = new Pen(Color.FromArgb(190, PanelTheme.EdgeShadow)))
                g.DrawPath(outline, path);

            if (ShowLamp && IsOn && PanelTheme.Bloom > 0.01f)
            {
                using var glow = new Pen(Color.FromArgb(PanelTheme.BloomAlpha(90f), LampColor), 1.2f);
                using var glowPath = PanelTheme.RoundedRect(
                    RectangleF.Inflate(face, -1.2f, -1.2f), Math.Max(1f, radius - 1f));
                g.DrawPath(glow, glowPath);
            }
        }

        int pad = Scaled(8);
        int textLeft = pad;

        if (ShowLamp)
        {
            int lampSize = Scaled(12);
            var lampRect = new RectangleF(pad, (Height - lampSize) / 2f, lampSize, lampSize);
            PanelTheme.DrawLamp(g, lampRect, LampColor, IsOn, 0.75f);
            textLeft = pad + lampSize + Scaled(7);
        }

        var textRect = new Rectangle(textLeft, 0, Width - textLeft - pad, Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
                    (ShowLamp ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);

        Color textColor = ShowLamp && IsOn
            ? PanelTheme.TextActive
            : (_hot ? PanelTheme.Blend(PanelTheme.TextPrimary, Color.White, 0.2f) : PanelTheme.TextPrimary);

        PanelTheme.DrawEngraved(g, Text, Font, textRect, textColor, flags);

        if (Focused)
        {
            using var focusPen = new Pen(Color.FromArgb(140, PanelTheme.TextPrimary)) { DashStyle = DashStyle.Dot };
            using var focusPath = PanelTheme.RoundedRect(RectangleF.Inflate(face, -3f, -3f), Math.Max(1f, radius - 2f));
            g.DrawPath(focusPen, focusPath);
        }
    }
}
