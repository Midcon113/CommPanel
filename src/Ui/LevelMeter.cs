using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// An LED bargraph level meter, in the manner of the segment meters on a mixing desk:
/// green through the working range, amber approaching the top, red at the ceiling, with a
/// peak-hold segment that hangs at the loudest recent level and falls back slowly.
///
/// The ballistics matter as much as the colours - a meter that simply follows the raw peak
/// looks like noise. Level rises instantly and falls away gradually, which is what makes it
/// readable.
/// </summary>
internal sealed class LevelMeter : ChassisControl
{
    private const int SegmentCount = 24;

    /// <summary>Fraction of the bar that is amber, then red, at the top.</summary>
    private const float AmberFrom = 0.68f;
    private const float RedFrom = 0.88f;

    private float _level;
    private float _peak;
    private int _peakHoldTicks;

    public LevelMeter()
    {
        TabStop = false;
        Cursor = Cursors.Default;
    }

    /// <summary>Greys the meter out when the endpoint is muted or absent.</summary>
    public bool IsInactive { get; set; }

    /// <summary>Caption drawn beneath the bar, e.g. "OUT".</summary>
    public string Caption { get; set; } = string.Empty;

    public Font? CaptionFont { get; set; }

    /// <summary>
    /// Feeds a new raw peak reading, applying meter ballistics. Returns true when the
    /// display actually changed, so the caller can skip repainting a meter at rest.
    /// </summary>
    public bool Feed(float rawPeak)
    {
        float previousLevel = _level;
        float previousPeak = _peak;

        // Fast attack, slow release: jump straight to a louder reading, ease down from it.
        _level = rawPeak >= _level ? rawPeak : _level * 0.78f;
        if (_level < 0.0005f) _level = 0f;

        if (_level >= _peak)
        {
            _peak = _level;
            _peakHoldTicks = 18; // roughly half a second at the panel's tick rate
        }
        else if (_peakHoldTicks > 0)
        {
            _peakHoldTicks--;
        }
        else
        {
            _peak = Math.Max(_level, _peak - 0.02f);
        }

        // Only repaint when a segment boundary could have moved.
        return Quantise(previousLevel) != Quantise(_level) ||
               Quantise(previousPeak) != Quantise(_peak);
    }

    public void Reset()
    {
        _level = 0f;
        _peak = 0f;
        _peakHoldTicks = 0;
        Invalidate();
    }

    private static int Quantise(float value) => (int)(ScaleAmplitude(value) * SegmentCount);

    /// <summary>
    /// Peak values are linear amplitude, where almost everything audible sits near the
    /// bottom. This spreads the useful range across the bar the way a dB scale would.
    /// </summary>
    private static float ScaleAmplitude(float amplitude)
    {
        if (amplitude <= 0.0001f) return 0f;

        const float floorDb = -60f;
        float db = 20f * MathF.Log10(amplitude);
        if (db < floorDb) return 0f;
        return Math.Clamp((db - floorDb) / -floorDb, 0f, 1f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackdrop(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int captionWidth = 0;
        var captionFont = CaptionFont ?? Font;
        if (Caption.Length > 0)
        {
            captionWidth = TextRenderer.MeasureText(g, "OUT", captionFont).Width + Scaled(6);
            PanelTheme.DrawEngraved(g, Caption, captionFont,
                new Rectangle(0, 0, captionWidth, Height),
                Color.FromArgb(170, PanelTheme.TextSecondary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.SingleLine);
        }

        // The housing sits inside a margin so a lit bar's glow has somewhere to spill. Without
        // it the control's own bounds clip the halo and bloom does almost nothing.
        int glowMargin = Math.Min(Scaled(6), Math.Max(0, (Height - Scaled(10)) / 2));

        var track = new Rectangle(captionWidth, glowMargin,
                                  Width - captionWidth, Height - glowMargin * 2);
        if (track.Width <= 4 || track.Height <= 4) return;

        // Recessed housing, so the segments read as sunk into the plate.
        using (var housing = PanelTheme.RoundedRect(
                   new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1.5f, track.Height - 1.5f),
                   Scaled(3)))
        {
            using var fill = new SolidBrush(Color.FromArgb(150, PanelTheme.PlateRecess));
            g.FillPath(fill, housing);
            using var edge = new Pen(Color.FromArgb(160, PanelTheme.EdgeShadow));
            g.DrawPath(edge, housing);
        }

        int inset = Scaled(3);
        var bar = Rectangle.Inflate(track, -inset, -inset);
        if (bar.Width <= SegmentCount || bar.Height <= 2) return;

        float gap = Math.Max(1f, Scaled(1));
        float segmentWidth = (bar.Width - gap * (SegmentCount - 1)) / (float)SegmentCount;
        if (segmentWidth <= 0.5f) return;

        int lit = IsInactive ? 0 : (int)MathF.Round(ScaleAmplitude(_level) * SegmentCount);
        int peakSegment = IsInactive ? -1 : (int)MathF.Round(ScaleAmplitude(_peak) * SegmentCount) - 1;

        Color ColourOf(int index)
        {
            float position = (index + 0.5f) / SegmentCount;
            return position >= RedFrom ? PanelTheme.LampRed
                 : position >= AmberFrom ? PanelTheme.LampAmber
                 : PanelTheme.LampGreen;
        }

        RectangleF SegmentAt(int index) => new(
            bar.X + index * (segmentWidth + gap), bar.Y, segmentWidth, bar.Height);

        // Bloom goes behind the segments so the glow spills onto the plate while the LEDs
        // themselves stay crisp. The lit run is bloomed one colour zone at a time - at most
        // three fills for a full bar, rather than one per segment.
        if (lit > 0 && PanelTheme.Bloom > 0.01f)
        {
            int zoneStart = 0;
            for (int i = 1; i <= lit; i++)
            {
                bool endOfZone = i == lit || ColourOf(i) != ColourOf(zoneStart);
                if (!endOfZone) continue;

                var from = SegmentAt(zoneStart);
                var to = SegmentAt(i - 1);
                PanelTheme.DrawBarBloom(g,
                    RectangleF.FromLTRB(from.Left, from.Top, to.Right, to.Bottom),
                    ColourOf(zoneStart));

                zoneStart = i;
            }
        }

        if (peakSegment >= 0 && peakSegment >= lit && PanelTheme.Bloom > 0.01f)
            PanelTheme.DrawBarBloom(g, SegmentAt(peakSegment), ColourOf(peakSegment), 60f);

        for (int i = 0; i < SegmentCount; i++)
        {
            Color colour = ColourOf(i);

            bool isLit = i < lit;
            bool isPeak = i == peakSegment && peakSegment >= 0;

            var segment = SegmentAt(i);

            Color face = isLit || isPeak
                ? colour
                : PanelTheme.Blend(colour, Color.Black, IsInactive ? 0.93f : 0.86f);

            using (var brush = new SolidBrush(face))
                g.FillRectangle(brush, segment);

            if (isLit)
            {
                // A lit segment gets a brighter core, as an LED does.
                var core = new RectangleF(segment.X, segment.Y + segment.Height * 0.18f,
                                          segment.Width, segment.Height * 0.42f);
                using var highlight = new SolidBrush(Color.FromArgb(110, 255, 255, 255));
                g.FillRectangle(highlight, core);
            }
            else if (isPeak)
            {
                using var outline = new Pen(Color.FromArgb(220, PanelTheme.Blend(colour, Color.White, 0.4f)));
                g.DrawRectangle(outline, segment.X, segment.Y, segment.Width, segment.Height - 1);
            }
        }
    }
}
