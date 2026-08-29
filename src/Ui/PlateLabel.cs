namespace CommPanel.Ui;

/// <summary>
/// Engraved text sitting on the chassis. A control rather than text drawn by the form, so
/// that rows whose contents change - the application mixer - can be repositioned and
/// relabelled without rebuilding the cached chassis bitmap.
/// </summary>
internal sealed class PlateLabel : ChassisControl
{
    public PlateLabel()
    {
        TabStop = false;
        Cursor = Cursors.Default;
    }

    public Color TextColor { get; set; } = PanelTheme.TextPrimary;

    /// <summary>Draws a small lamp before the text, lit when <see cref="IsLit"/> is set.</summary>
    public bool ShowLamp { get; set; }

    public bool IsLit { get; set; }

    public Color LampColor { get; set; } = PanelTheme.LampGreen;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackdrop(g);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int left = 0;

        if (ShowLamp)
        {
            int lampSize = Scaled(9);
            var lampRect = new RectangleF(0, (Height - lampSize) / 2f, lampSize, lampSize);
            PanelTheme.DrawLamp(g, lampRect, LampColor, IsLit, 0.6f);
            left = lampSize + Scaled(8);
        }

        PanelTheme.DrawEngraved(g, Text, Font, new Rectangle(left, 0, Width - left, Height),
            TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
