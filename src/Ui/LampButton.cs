using System.Drawing.Drawing2D;
using CommPanel.Audio;

namespace CommPanel.Ui;

/// <summary>
/// One device on the panel: a bolted metal key with a signal lamp that is lit when this
/// endpoint is the Windows default. Clicking anywhere on the key switches to it.
/// </summary>
internal sealed class LampButton : ChassisControl
{
    private bool _hot;
    private bool _pressed;

    public AudioDevice? Device { get; set; }

    /// <summary>Lamp colour when lit - green for outputs, amber for inputs.</summary>
    public Color LampColor { get; set; } = PanelTheme.LampGreen;

    public bool IsActive { get; set; }

    /// <summary>Lights the small secondary lamp marking the Communications default.</summary>
    public bool IsCommunications { get; set; }

    public Font? PrimaryFont { get; set; }
    public Font? SecondaryFont { get; set; }
    public Font? StencilFont { get; set; }

    /// <summary>Raised on right-click: assign this device the Communications role only.</summary>
    public event EventHandler? CommsRequested;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && ClientRectangle.Contains(e.Location))
            CommsRequested?.Invoke(this, EventArgs.Empty);

        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }
        base.OnMouseUp(e);
    }

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

        float radius = Scaled(5);
        var face = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        bool offline = Device?.IsOffline == true;

        Color top, bottom;
        if (offline)
        {
            // Sunk and desaturated: the key is still clickable, but it should not look like
            // something that will produce sound.
            top = PanelTheme.Blend(PanelTheme.ButtonTop, Color.Black, _hot ? 0.30f : 0.42f);
            bottom = PanelTheme.Blend(PanelTheme.ButtonBottom, Color.Black, 0.42f);
        }
        else if (_pressed) { top = PanelTheme.ButtonBottom; bottom = PanelTheme.ButtonTop; }
        else if (IsActive) { top = _hot ? PanelTheme.Blend(PanelTheme.ButtonTopActive, Color.White, 0.10f) : PanelTheme.ButtonTopActive; bottom = PanelTheme.ButtonBottomActive; }
        else if (_hot) { top = PanelTheme.ButtonTopHot; bottom = PanelTheme.ButtonBottomHot; }
        else { top = PanelTheme.ButtonTop; bottom = PanelTheme.ButtonBottom; }

        using (var path = PanelTheme.RoundedRect(face, radius))
        {
            using (var fill = new LinearGradientBrush(
                       new RectangleF(face.X, face.Y, face.Width, face.Height + 1), top, bottom, 90f))
                g.FillPath(fill, path);

            // Top bevel highlight and bottom shadow give the key its physical depth.
            using (var bevel = new Pen(Color.FromArgb(_pressed ? 20 : 70, 255, 255, 255)))
                g.DrawLine(bevel, face.X + radius, face.Y + 1f, face.Right - radius, face.Y + 1f);
            using (var outline = new Pen(Color.FromArgb(200, PanelTheme.EdgeShadow), 1f))
                g.DrawPath(outline, path);

            if (IsActive && !offline && PanelTheme.Bloom > 0.01f)
            {
                // A lit key gets a coloured rim, as if the lamp is bleeding onto the bezel.
                using var glow = new Pen(Color.FromArgb(PanelTheme.BloomAlpha(110f), LampColor), 1.4f);
                using var glowPath = PanelTheme.RoundedRect(
                    RectangleF.Inflate(face, -1.4f, -1.4f), Math.Max(1f, radius - 1f));
                g.DrawPath(glow, glowPath);
            }
            else if (IsActive && offline)
            {
                // Still the selected device, but dead. A dull red rim says "this is the one
                // you are on" without any of the brightness that would imply it is working.
                using var rim = new Pen(Color.FromArgb(120, PanelTheme.LampRed), 1.4f);
                using var rimPath = PanelTheme.RoundedRect(
                    RectangleF.Inflate(face, -1.4f, -1.4f), Math.Max(1f, radius - 1f));
                g.DrawPath(rim, rimPath);
            }
        }

        int pad = Scaled(10);
        int lampSize = Scaled(24);
        var lampRect = new RectangleF(pad, (Height - lampSize) / 2f, lampSize, lampSize);
        PanelTheme.DrawLamp(g, lampRect, offline ? PanelTheme.LampRed : LampColor, IsActive && !offline);

        int rightReserve = 0;

        if (offline)
        {
            var offlineRect = new Rectangle(
                Width - pad - Scaled(58), 0, Scaled(58), Height);

            if (StencilFont is not null)
            {
                PanelTheme.DrawEngraved(g, "OFFLINE", StencilFont, offlineRect,
                    PanelTheme.Blend(PanelTheme.LampRed, PanelTheme.TextSecondary, 0.3f),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }

            rightReserve = Scaled(64);
        }

        // Communications sub-lamp on the trailing edge.
        int commsSize = Scaled(11);
        if (!offline && IsCommunications)
        {
            var commsRect = new RectangleF(
                Width - pad - commsSize,
                (Height / 2f) - commsSize - Scaled(1),
                commsSize, commsSize);
            PanelTheme.DrawLamp(g, commsRect, PanelTheme.LampBlue, true, 0.7f);

            var tagRect = new Rectangle(
                Width - pad - Scaled(44),
                (int)(Height / 2f) + Scaled(1),
                Scaled(44),
                Scaled(14));
            if (StencilFont is not null)
            {
                PanelTheme.DrawEngraved(g, "COMMS", StencilFont, tagRect,
                    PanelTheme.Blend(PanelTheme.LampBlue, Color.Black, 0.25f),
                    TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            rightReserve = Scaled(50);
        }

        int textLeft = pad + lampSize + Scaled(11);
        int textWidth = Width - textLeft - pad - rightReserve;
        if (textWidth < Scaled(30)) textWidth = Scaled(30);

        var device = Device;
        string primary = device?.ShortName ?? "—";
        string secondary = device is null
            ? string.Empty
            : (string.IsNullOrEmpty(device.Adapter) ? DescribeFormFactor(device.FormFactor) : device.Adapter);

        var primaryFont = PrimaryFont ?? Font;
        var secondaryFont = SecondaryFont ?? Font;

        int primaryHeight = TextRenderer.MeasureText(g, "Ag", primaryFont).Height;
        int secondaryHeight = TextRenderer.MeasureText(g, "Ag", secondaryFont).Height;
        int block = primaryHeight + secondaryHeight;
        int textTop = (Height - block) / 2;

        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        Color primaryColour = offline
            ? PanelTheme.Blend(PanelTheme.TextPrimary, Color.Black, 0.45f)
            : (IsActive ? PanelTheme.TextActive : PanelTheme.TextPrimary);

        PanelTheme.DrawEngraved(g, primary, primaryFont,
            new Rectangle(textLeft, textTop, textWidth, primaryHeight),
            primaryColour, flags);

        if (!string.IsNullOrEmpty(secondary))
        {
            TextRenderer.DrawText(g, secondary, secondaryFont,
                new Rectangle(textLeft, textTop + primaryHeight, textWidth, secondaryHeight),
                IsActive
                    ? PanelTheme.Blend(PanelTheme.TextSecondary, Color.White, 0.25f)
                    : PanelTheme.TextSecondary,
                flags);
        }

        if (Focused)
        {
            using var focusPen = new Pen(Color.FromArgb(150, PanelTheme.TextPrimary))
            {
                DashStyle = DashStyle.Dot
            };
            using var focusPath = PanelTheme.RoundedRect(
                RectangleF.Inflate(face, -3f, -3f), Math.Max(1f, radius - 2f));
            g.DrawPath(focusPen, focusPath);
        }
    }

    private static string DescribeFormFactor(FormFactor formFactor) => formFactor switch
    {
        FormFactor.Speakers => "Speakers",
        FormFactor.Headphones => "Headphones",
        FormFactor.Headset => "Headset",
        FormFactor.Microphone => "Microphone",
        FormFactor.LineLevel => "Line level",
        FormFactor.Spdif => "S/PDIF",
        FormFactor.DigitalAudioDisplayDevice => "Display audio",
        FormFactor.Handset => "Handset",
        FormFactor.RemoteNetworkDevice => "Network device",
        _ => "Audio endpoint"
    };
}
