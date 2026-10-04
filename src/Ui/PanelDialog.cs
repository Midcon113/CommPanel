using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// A dialog built out of the same stamped-metal chassis as the main window: borderless, with
/// its own header strip, close key and drag region, and scaled by the user's PANEL SIZE
/// setting like everything else.
///
/// A dialog that pops out of a machine should look like part of the machine. This exists so
/// that is the default rather than something each dialog has to remember to do, and so the
/// panel's size setting reaches dialogs too.
/// </summary>
internal class PanelDialog : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int HeaderHeight = 46;

    private readonly PlateButton _close = new();
    private readonly string _title;
    private readonly string _subtitle;

    private Bitmap? _chassis;

    protected PanelDialog(AppSettings settings, string title, string subtitle)
    {
        Settings = settings;
        _title = title;
        _subtitle = subtitle;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = PanelTheme.ChassisBottom;
        Icon = AppIcon.Load(32);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        float scale = settings.SafeFontScale;
        TitleFont = PanelTheme.TitleFont(scale);
        LabelFont = PanelTheme.LabelFont(scale);
        SmallFont = PanelTheme.SmallFont(scale);
        StencilFont = PanelTheme.StencilFont(scale);
        Font = SmallFont;

        _close.Text = "✕";
        _close.Font = LabelFont;
        _close.Destructive = true;
        _close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_close);
    }

    protected AppSettings Settings { get; }

    protected Font TitleFont { get; }
    protected Font LabelFont { get; }
    protected Font SmallFont { get; }
    protected Font StencilFont { get; }

    /// <summary>Where a derived dialog's own content may start.</summary>
    protected int BodyTop => Scaled(HeaderHeight) + Scaled(10);

    protected int EdgeMargin => Scaled(16);

    /// <summary>A logical measurement scaled for both DPI and the user's chosen panel size.</summary>
    protected int Scaled(int logical) =>
        LogicalToDeviceUnits((int)MathF.Round(logical * Settings.SafeFontScale));

    /// <summary>
    /// Called to draw the engraved, unchanging parts of this dialog into the chassis bitmap:
    /// recesses, captions, anything that does not move. Drawn once per resize rather than per
    /// paint, exactly as the main panel does it.
    /// </summary>
    protected virtual void DrawChassis(Graphics g) { }

    /// <summary>
    /// Rebuilds the chassis bitmap at the current size and hands it to every child control so
    /// their backgrounds line up with it. Call after setting <see cref="Form.ClientSize"/>.
    /// </summary>
    protected void RebuildChassis()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        _chassis?.Dispose();
        _chassis = PanelTheme.CreateChassis(ClientSize.Width, ClientSize.Height);

        using (var g = Graphics.FromImage(_chassis))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int headerY = Scaled(HeaderHeight);
            using (var dark = new Pen(Color.FromArgb(150, PanelTheme.EdgeShadow)))
                g.DrawLine(dark, 0, headerY, ClientSize.Width, headerY);
            using (var light = new Pen(Color.FromArgb(40, PanelTheme.EdgeHighlight)))
                g.DrawLine(light, 0, headerY + 1, ClientSize.Width, headerY + 1);

            DrawChassis(g);

            float screw = Scaled(8);
            float inset = Scaled(8);
            PanelTheme.DrawScrew(g, inset, inset, screw);
            PanelTheme.DrawScrew(g, ClientSize.Width - inset, inset, screw);
            PanelTheme.DrawScrew(g, inset, ClientSize.Height - inset, screw);
            PanelTheme.DrawScrew(g, ClientSize.Width - inset, ClientSize.Height - inset, screw);
        }

        foreach (Control control in Controls)
        {
            if (control is ChassisControl chassisControl)
            {
                chassisControl.Backdrop = _chassis;
                chassisControl.UiScale = Settings.SafeFontScale;
                chassisControl.Invalidate();
            }
        }

        Invalidate();
    }

    /// <summary>Places the close key. Called by <see cref="RebuildChassis"/>'s caller after sizing.</summary>
    protected void PlaceHeader()
    {
        int width = Scaled(32);
        int height = Scaled(22);
        _close.Bounds = new Rectangle(ClientSize.Width - EdgeMargin - width, Scaled(12), width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2. Ignored before Win11.
            int preference = 2;
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Square corners are a perfectly good fallback.
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_chassis is not null) g.DrawImageUnscaled(_chassis, 0, 0);
        else g.Clear(PanelTheme.ChassisBottom);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int margin = EdgeMargin;
        int headerHeight = Scaled(HeaderHeight);

        int lampSize = Scaled(14);
        var lampRect = new RectangleF(margin, (headerHeight - lampSize) / 2f, lampSize, lampSize);
        PanelTheme.DrawLamp(g, lampRect, PanelTheme.LampBlue, true, 0.85f);

        int titleLeft = margin + lampSize + Scaled(10);
        int titleHeight = TextRenderer.MeasureText(g, "Ag", TitleFont).Height;
        int subtitleHeight = TextRenderer.MeasureText(g, "Ag", StencilFont).Height;
        int top = (headerHeight - (titleHeight + subtitleHeight)) / 2;

        PanelTheme.DrawEngraved(g, _title, TitleFont,
            new Rectangle(titleLeft, top, ClientSize.Width - titleLeft, titleHeight),
            PanelTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        TextRenderer.DrawText(g, _subtitle, StencilFont,
            new Rectangle(titleLeft, top + titleHeight, ClientSize.Width - titleLeft, subtitleHeight),
            Color.FromArgb(160, PanelTheme.TextSecondary),
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTCAPTION = 2;
        const int HTCLIENT = 1;

        base.WndProc(ref m);

        // The header strip behaves as the title bar of a borderless window.
        if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
        {
            long lParam = m.LParam.ToInt64();
            var screenPoint = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            var client = PointToClient(screenPoint);
            if (client.Y >= 0 && client.Y < Scaled(HeaderHeight))
                m.Result = new IntPtr(HTCAPTION);
        }
    }

    /// <summary>Draws a caption above a field, in the stencil the panel labels everything in.</summary>
    protected void DrawCaption(Graphics g, string text, Rectangle bounds) =>
        TextRenderer.DrawText(g, text, StencilFont, bounds,
            Color.FromArgb(150, PanelTheme.TextSecondary),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

    /// <summary>Draws a block of explanatory text in the small face, wrapped.</summary>
    protected void DrawBody(Graphics g, string text, Rectangle bounds) =>
        TextRenderer.DrawText(g, text, SmallFont, bounds,
            PanelTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chassis?.Dispose();
            _chassis = null;
            TitleFont.Dispose();
            LabelFont.Dispose();
            SmallFont.Dispose();
            StencilFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
