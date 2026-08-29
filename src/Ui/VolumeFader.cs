using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// A horizontal fader for endpoint volume: a recessed groove with tick marks and a milled
/// metal cap, plus a percentage readout.
/// </summary>
internal sealed class VolumeFader : ChassisControl
{
    private float _value;
    private bool _dragging;
    private bool _hot;

    public VolumeFader()
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>Volume from 0.0 to 1.0.</summary>
    public float Value
    {
        get => _value;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _value) < 0.0005f) return;
            _value = clamped;
            Invalidate();
        }
    }

    /// <summary>True while the user is dragging, so external updates can be ignored.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Greys the fader when the endpoint has no volume control or is muted.</summary>
    public bool IsInactive { get; set; }

    public Font? ReadoutFont { get; set; }

    /// <summary>Formats the readout. Defaults to a percentage of full scale.</summary>
    public Func<float, string>? ReadoutText { get; set; }

    /// <summary>Raised as the user drags, with the new value.</summary>
    public event Action<float>? ValueChanged;

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !IsInactive)
        {
            _dragging = true;
            Focus();
            ApplyFromMouse(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) ApplyFromMouse(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (IsInactive) return;

        float step = e.Delta > 0 ? 0.02f : -0.02f;
        float updated = Math.Clamp(_value + step, 0f, 1f);
        if (Math.Abs(updated - _value) < 0.0005f) return;

        _value = updated;
        Invalidate();
        ValueChanged?.Invoke(_value);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsInactive) { base.OnKeyDown(e); return; }

        float updated = e.KeyCode switch
        {
            Keys.Left => _value - 0.02f,
            Keys.Right => _value + 0.02f,
            Keys.Home => 0f,
            Keys.End => 1f,
            _ => _value
        };

        if (Math.Abs(updated - _value) > 0.0005f)
        {
            _value = Math.Clamp(updated, 0f, 1f);
            Invalidate();
            ValueChanged?.Invoke(_value);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private void ApplyFromMouse(int x)
    {
        var groove = GrooveBounds();
        if (groove.Width <= 0) return;

        float updated = Math.Clamp((x - groove.X) / (float)groove.Width, 0f, 1f);
        if (Math.Abs(updated - _value) < 0.0005f) return;

        _value = updated;
        Invalidate();
        ValueChanged?.Invoke(_value);
    }

    private Rectangle GrooveBounds()
    {
        int readout = Scaled(38);
        int capHalf = Scaled(7);
        return new Rectangle(capHalf, 0, Math.Max(1, Width - readout - capHalf * 2), Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackdrop(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var groove = GrooveBounds();
        float centreY = Height / 2f;
        float grooveHeight = Math.Max(3f, Scaled(4));

        var track = new RectangleF(groove.X, centreY - grooveHeight / 2f, groove.Width, grooveHeight);
        using (var path = PanelTheme.RoundedRect(track, grooveHeight / 2f))
        {
            using var fill = new SolidBrush(Color.FromArgb(190, PanelTheme.PlateRecess));
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(150, PanelTheme.EdgeShadow));
            g.DrawPath(edge, path);
        }

        // Filled portion up to the cap.
        float filledWidth = groove.Width * _value;
        if (filledWidth > 1f)
        {
            var filled = new RectangleF(groove.X, track.Y, filledWidth, grooveHeight);
            using var path = PanelTheme.RoundedRect(filled, grooveHeight / 2f);
            Color tint = IsInactive
                ? PanelTheme.Blend(PanelTheme.LampGreen, Color.Black, 0.7f)
                : PanelTheme.LampGreen;
            using var fill = new SolidBrush(Color.FromArgb(IsInactive ? 120 : 205, tint));
            g.FillPath(fill, path);
        }

        // Tick marks at quarters.
        using (var tick = new Pen(Color.FromArgb(60, PanelTheme.EdgeHighlight)))
        {
            for (int i = 0; i <= 4; i++)
            {
                float x = groove.X + groove.Width * (i / 4f);
                g.DrawLine(tick, x, centreY + grooveHeight, x, centreY + grooveHeight + Scaled(3));
            }
        }

        // The cap.
        float capWidth = Scaled(13);
        float capHeight = Math.Min(Height - 2, Scaled(20));
        float capX = groove.X + groove.Width * _value - capWidth / 2f;
        var cap = new RectangleF(capX, centreY - capHeight / 2f, capWidth, capHeight);

        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillRectangle(shadow, cap.X + 1, cap.Y + 1.5f, cap.Width, cap.Height);

        using (var path = PanelTheme.RoundedRect(cap, Scaled(3)))
        {
            Color top = _hot || _dragging
                ? PanelTheme.Blend(PanelTheme.ButtonTopHot, Color.White, 0.12f)
                : PanelTheme.ButtonTopHot;
            using var fill = new LinearGradientBrush(
                new RectangleF(cap.X, cap.Y, cap.Width, cap.Height + 1), top, PanelTheme.ButtonBottom, 90f);
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(200, PanelTheme.EdgeShadow));
            g.DrawPath(edge, path);
        }

        // Milling on the cap face.
        using (var mill = new Pen(Color.FromArgb(70, 255, 255, 255)))
        {
            for (int i = -1; i <= 1; i++)
            {
                float x = cap.X + cap.Width / 2f + i * Scaled(3);
                g.DrawLine(mill, x, cap.Y + cap.Height * 0.24f, x, cap.Y + cap.Height * 0.76f);
            }
        }

        // Percentage readout.
        var readoutRect = new Rectangle(Width - Scaled(36), 0, Scaled(36), Height);
        string readout = ReadoutText?.Invoke(_value) ?? ((int)MathF.Round(_value * 100f)) + "%";
        PanelTheme.DrawEngraved(g, readout, ReadoutFont ?? Font, readoutRect,
            IsInactive ? Color.FromArgb(120, PanelTheme.TextSecondary) : PanelTheme.TextPrimary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine);

        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(130, PanelTheme.TextPrimary)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 1, 1, Width - readoutRect.Width - 2, Height - 3);
        }
    }
}
