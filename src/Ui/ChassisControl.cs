namespace CommPanel.Ui;

/// <summary>
/// Base for controls that sit on the painted chassis. Rather than relying on WinForms
/// transparency - which repaints the whole parent for every hover - each control blits the
/// matching slice of the parent's cached chassis bitmap. One clipped copy per repaint.
/// </summary>
internal abstract class ChassisControl : Control
{
    protected ChassisControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    /// <summary>
    /// Scales this control's internal measurements - lamp sizes, padding, bar insets - so a
    /// key that has been made larger for readability keeps its proportions.
    /// </summary>
    public float UiScale { get; set; } = 1f;

    /// <summary>A logical measurement scaled for both DPI and the user's size setting.</summary>
    protected int Scaled(int logical) =>
        LogicalToDeviceUnits((int)MathF.Round(logical * UiScale));

    /// <summary>The parent's cached chassis image, in parent client coordinates.</summary>
    public Bitmap? Backdrop { get; set; }

    protected void PaintBackdrop(Graphics g)
    {
        var backdrop = Backdrop;
        if (backdrop is null)
        {
            g.Clear(BackColor == Color.Transparent ? PanelTheme.ChassisBottom : BackColor);
            return;
        }

        var source = new Rectangle(Left, Top, Width, Height);
        if (source.Right > backdrop.Width || source.Bottom > backdrop.Height ||
            source.X < 0 || source.Y < 0)
        {
            g.Clear(BackColor == Color.Transparent ? PanelTheme.ChassisBottom : BackColor);
            return;
        }

        g.DrawImage(backdrop, new Rectangle(0, 0, Width, Height), source, GraphicsUnit.Pixel);
    }
}
