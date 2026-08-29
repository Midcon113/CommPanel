using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// Colours and drawing helpers for the panel: an aged steel control plate carrying rows of
/// illuminated indicator lamps. Everything is drawn with GDI+ primitives - there are no
/// bitmap assets to ship, and the expensive parts (plate texture) are rendered once into a
/// cache rather than on every paint.
/// </summary>
internal static class PanelTheme
{
    // Plate / chassis
    public static readonly Color ChassisTop = Color.FromArgb(0x3C, 0x38, 0x32);
    public static readonly Color ChassisBottom = Color.FromArgb(0x23, 0x21, 0x1E);
    public static readonly Color PlateRecess = Color.FromArgb(0x1E, 0x1C, 0x1A);
    public static readonly Color EdgeHighlight = Color.FromArgb(0x6A, 0x64, 0x59);
    public static readonly Color EdgeShadow = Color.FromArgb(0x13, 0x12, 0x10);
    public static readonly Color ScrewMetal = Color.FromArgb(0x8C, 0x84, 0x74);

    // Buttons
    public static readonly Color ButtonTop = Color.FromArgb(0x4B, 0x46, 0x3E);
    public static readonly Color ButtonBottom = Color.FromArgb(0x33, 0x30, 0x2A);
    public static readonly Color ButtonTopHot = Color.FromArgb(0x5C, 0x56, 0x4C);
    public static readonly Color ButtonBottomHot = Color.FromArgb(0x3F, 0x3B, 0x33);
    public static readonly Color ButtonTopActive = Color.FromArgb(0x51, 0x59, 0x43);
    public static readonly Color ButtonBottomActive = Color.FromArgb(0x37, 0x3E, 0x2E);

    // Text
    public static readonly Color TextPrimary = Color.FromArgb(0xE6, 0xDF, 0xCD);
    public static readonly Color TextSecondary = Color.FromArgb(0x9C, 0x93, 0x84);
    public static readonly Color TextEngraveShadow = Color.FromArgb(0x14, 0x13, 0x11);
    public static readonly Color TextActive = Color.FromArgb(0xF2, 0xFF, 0xE8);

    // Lamps
    public static readonly Color LampGreen = Color.FromArgb(0x54, 0xE8, 0x62);
    public static readonly Color LampAmber = Color.FromArgb(0xFF, 0xB8, 0x36);
    public static readonly Color LampRed = Color.FromArgb(0xFF, 0x54, 0x3E);
    public static readonly Color LampBlue = Color.FromArgb(0x5A, 0xC8, 0xFF);

    /// <summary>
    /// How much every lamp and lit meter segment glows, as a multiplier. 0 turns bloom off
    /// entirely, 1 is the reference look, 2 is heavy.
    ///
    /// This is deliberately global rather than a property threaded through every control:
    /// the whole point is that all the panel's light sources agree with each other, and a
    /// single window has exactly one lighting setting.
    /// </summary>
    public static float Bloom { get; set; } = 1f;

    /// <summary>Scales an alpha by the bloom setting, clamped to a legal value.</summary>
    public static int BloomAlpha(float baseAlpha, float intensity = 1f) =>
        (int)Math.Clamp(baseAlpha * intensity * Bloom, 0f, 255f);

    public static Font TitleFont(float dpiScale) => new("Segoe UI", 12f * dpiScale, FontStyle.Bold, GraphicsUnit.Point);
    public static Font LabelFont(float dpiScale) => new("Segoe UI Semibold", 10f * dpiScale, FontStyle.Regular, GraphicsUnit.Point);
    public static Font SmallFont(float dpiScale) => new("Segoe UI", 7.75f * dpiScale, FontStyle.Regular, GraphicsUnit.Point);
    public static Font StencilFont(float dpiScale) => new("Consolas", 8.25f * dpiScale, FontStyle.Bold, GraphicsUnit.Point);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.1f)
        {
            path.AddRectangle(r);
            return path;
        }

        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Renders the brushed-and-weathered chassis texture once, to be blitted on each paint.
    /// The speckle pattern is seeded so the wear marks stay put between redraws.
    /// </summary>
    public static Bitmap CreateChassis(int width, int height)
    {
        var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var full = new Rectangle(0, 0, bmp.Width, bmp.Height);
        using (var baseBrush = new LinearGradientBrush(full, ChassisTop, ChassisBottom, 90f))
            g.FillRectangle(baseBrush, full);

        // Horizontal brushing.
        var rng = new Random(0x0C0FFEE);
        for (int y = 0; y < bmp.Height; y += 2)
        {
            int alpha = rng.Next(4, 16);
            using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255));
            g.DrawLine(pen, 0, y, bmp.Width, y);
        }

        // Oxidation speckle - sparse, low alpha, warm.
        int speckles = (bmp.Width * bmp.Height) / 900;
        for (int i = 0; i < speckles; i++)
        {
            int x = rng.Next(bmp.Width);
            int y = rng.Next(bmp.Height);
            int size = rng.Next(1, 4);
            int alpha = rng.Next(6, 26);
            var tone = rng.Next(3) switch
            {
                0 => Color.FromArgb(alpha, 0x7A, 0x4A, 0x28),
                1 => Color.FromArgb(alpha, 0x00, 0x00, 0x00),
                _ => Color.FromArgb(alpha, 0xC8, 0xBE, 0xA8)
            };
            using var brush = new SolidBrush(tone);
            g.FillEllipse(brush, x, y, size, size);
        }

        // Vignette so the edges read as a recessed metal box.
        using (var path = new GraphicsPath())
        {
            path.AddRectangle(full);
            using var vignette = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(0, 0, 0, 0),
                SurroundColors = new[] { Color.FromArgb(90, 0, 0, 0) },
                FocusScales = new PointF(0.72f, 0.62f)
            };
            g.FillRectangle(vignette, full);
        }

        // Chassis edge.
        using (var edge = new Pen(EdgeShadow, 2f))
            g.DrawRectangle(edge, 0, 0, bmp.Width - 1, bmp.Height - 1);
        using (var inner = new Pen(Color.FromArgb(60, EdgeHighlight)))
            g.DrawRectangle(inner, 2, 2, bmp.Width - 5, bmp.Height - 5);

        return bmp;
    }

    /// <summary>A recessed sub-plate, used to group a bank of lamps.</summary>
    public static void DrawRecess(Graphics g, RectangleF rect, float radius)
    {
        using var path = RoundedRect(rect, radius);
        using (var fill = new SolidBrush(Color.FromArgb(120, PlateRecess)))
            g.FillPath(fill, path);
        using (var shadow = new Pen(Color.FromArgb(150, EdgeShadow), 1.5f))
            g.DrawPath(shadow, path);

        using var lip = RoundedRect(new RectangleF(rect.X + 1.2f, rect.Y + 1.2f, rect.Width - 2.4f, rect.Height - 2.4f), radius);
        using var lipPen = new Pen(Color.FromArgb(45, EdgeHighlight));
        g.DrawPath(lipPen, lip);
    }

    /// <summary>A panel screw. Purely decorative; sells the "bolted steel box" read.</summary>
    public static void DrawScrew(Graphics g, float cx, float cy, float diameter)
    {
        var r = new RectangleF(cx - diameter / 2f, cy - diameter / 2f, diameter, diameter);
        using (var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            g.FillEllipse(shadow, r.X + 1, r.Y + 1.5f, r.Width, r.Height);
        using (var body = new LinearGradientBrush(r, ScrewMetal, Color.FromArgb(0x4A, 0x45, 0x3C), 60f))
            g.FillEllipse(body, r);
        using (var rim = new Pen(Color.FromArgb(130, 0, 0, 0)))
            g.DrawEllipse(rim, r);
        using (var slot = new Pen(Color.FromArgb(160, 0, 0, 0), Math.Max(1f, diameter * 0.14f)))
        {
            float inset = diameter * 0.22f;
            g.DrawLine(slot, r.X + inset, cy + diameter * 0.12f, r.Right - inset, cy - diameter * 0.12f);
        }
    }

    /// <summary>
    /// An indicator lamp: chrome bezel, coloured glass, and - when lit - a soft halo that
    /// bleeds onto the surrounding plate, which is what makes a panel of these read as "live".
    /// </summary>
    public static void DrawLamp(Graphics g, RectangleF bounds, Color color, bool lit, float intensity = 1f)
    {
        if (lit && intensity > 0f && Bloom > 0.01f)
        {
            // The halo holds full strength out to the rim of the lamp and only falls off
            // beyond it. Without that plateau the glass sits in a faint fog: the bright part
            // of the gradient is hidden underneath the lamp, and only the weak tail shows.
            float reach = bounds.Width * (1f + 1.1f * Math.Min(Bloom, 2f));
            var haloRect = new RectangleF(
                bounds.X + bounds.Width / 2f - reach / 2f,
                bounds.Y + bounds.Height / 2f - reach / 2f,
                reach, reach);

            float plateau = Math.Clamp(bounds.Width / reach, 0.05f, 0.9f);

            using var haloPath = new GraphicsPath();
            haloPath.AddEllipse(haloRect);
            using var haloBrush = new PathGradientBrush(haloPath)
            {
                CenterColor = Color.FromArgb(BloomAlpha(150f, intensity), color),
                SurroundColors = new[] { Color.FromArgb(0, color) },
                FocusScales = new PointF(plateau, plateau)
            };
            g.FillEllipse(haloBrush, haloRect);
        }

        // Bezel.
        using (var bezel = new LinearGradientBrush(bounds,
                   Color.FromArgb(0xA8, 0xA0, 0x90), Color.FromArgb(0x3A, 0x36, 0x30), 65f))
            g.FillEllipse(bezel, bounds);
        using (var bezelEdge = new Pen(Color.FromArgb(180, 0, 0, 0), 1f))
            g.DrawEllipse(bezelEdge, bounds);

        // Glass.
        float inset = bounds.Width * 0.17f;
        var glass = RectangleF.Inflate(bounds, -inset, -inset);
        using (var glassPath = new GraphicsPath())
        {
            glassPath.AddEllipse(glass);
            // An unlit lamp keeps a little of its colour, so a bank of them still reads as
            // red/green/amber glass rather than a row of empty holes.
            // A lit lamp also runs hotter at the core as bloom rises, so the glass itself
            // brightens rather than only gaining a halo around an unchanged centre.
            Color center = lit
                ? Blend(color, Color.White, 0.5f + 0.1f * Math.Min(Bloom, 2f))
                : Blend(color, Color.Black, 0.76f);
            Color surround = lit
                ? Blend(color, Color.Black, 0.20f)
                : Blend(color, Color.Black, 0.90f);

            using var glassBrush = new PathGradientBrush(glassPath)
            {
                CenterColor = center,
                SurroundColors = new[] { surround },
                CenterPoint = new PointF(glass.X + glass.Width * 0.38f, glass.Y + glass.Height * 0.34f)
            };
            g.FillEllipse(glassBrush, glass);
        }

        // Specular highlight - present lit or dark, because the glass is glass either way.
        var spec = new RectangleF(
            glass.X + glass.Width * 0.20f,
            glass.Y + glass.Height * 0.14f,
            glass.Width * 0.40f,
            glass.Height * 0.28f);
        using (var specBrush = new SolidBrush(Color.FromArgb(lit ? 150 : 70, 255, 255, 255)))
            g.FillEllipse(specBrush, spec);

        using (var glassRim = new Pen(Color.FromArgb(120, 0, 0, 0)))
            g.DrawEllipse(glassRim, glass);
    }

    /// <summary>
    /// A soft glow spilling out of a lit rectangle, for meter segments. Drawn as a single
    /// gradient with a plateau across the middle rather than a per-segment halo, so a full
    /// bar costs a handful of fills instead of one per LED.
    /// </summary>
    public static void DrawBarBloom(Graphics g, RectangleF lit, Color color, float baseAlpha = 135f)
    {
        if (Bloom <= 0.01f || lit.Width <= 0f || lit.Height <= 0f) return;

        float spread = lit.Height * 1.15f * Math.Min(Bloom, 2f);
        var glow = RectangleF.Inflate(lit, spread, spread);

        using var path = new GraphicsPath();
        path.AddRectangle(glow);

        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(BloomAlpha(baseAlpha), color),
            SurroundColors = new[] { Color.FromArgb(0, color) },
            CenterPoint = new PointF(lit.X + lit.Width / 2f, lit.Y + lit.Height / 2f),
            FocusScales = new PointF(
                glow.Width <= 0 ? 0f : Math.Clamp(lit.Width / glow.Width, 0f, 0.95f),
                0.05f)
        };

        g.FillPath(brush, path);
    }

    /// <summary>Text with a one-pixel dark drop, so labels read as stamped into the plate.</summary>
    public static void DrawEngraved(Graphics g, string text, Font font, Rectangle bounds,
                                    Color color, TextFormatFlags flags)
    {
        var shadowBounds = bounds;
        shadowBounds.Offset(0, 1);
        TextRenderer.DrawText(g, text, font, shadowBounds, TextEngraveShadow, flags);
        TextRenderer.DrawText(g, text, font, bounds, color, flags);
    }

    public static Color Blend(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            a.A,
            (int)(a.R + (b.R - a.R) * amount),
            (int)(a.G + (b.G - a.G) * amount),
            (int)(a.B + (b.B - a.B) * amount));
    }
}
