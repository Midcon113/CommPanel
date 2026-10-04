using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CommPanel.Ui;

/// <summary>
/// Turns the notification-area icon into a miniature output meter.
///
/// The taskbar is the only part of CommPanel visible while a game is running, so this is
/// where a level reading is actually worth having - and it costs no window, nothing on top
/// of the game, and nothing to get in the way.
///
/// Two things keep it honest about CPU. The level is quantised into a handful of steps and
/// the icon is only redrawn when the step changes, so most readings do no drawing at all;
/// and every icon for a given step is built once and kept, so a long session draws each of
/// them exactly once.
/// </summary>
internal sealed class TrayMeter : IDisposable
{
    /// <summary>
    /// GDI icon handles are not garbage collected. An icon built from a bitmap owns one, and
    /// it has to be destroyed by hand or the process leaks a handle per redraw.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Steps in the bargraph. Seven reads as a meter at 16 pixels and gives the eye enough
    /// resolution to see speech move, without redrawing on every tiny fluctuation.
    /// </summary>
    private const int Segments = 7;

    private readonly Icon?[] _icons = new Icon?[Segments + 1];
    private readonly int _size;

    private int _shown = -1;
    private bool _disposed;

    public TrayMeter()
    {
        int small = SystemInformation.SmallIconSize.Width;
        _size = small <= 0 ? 16 : small;
    }

    /// <summary>
    /// Points the tray icon at the level. Returns the icon to show, or null when the display
    /// has not changed and the caller should leave the icon alone.
    /// </summary>
    public Icon? IconFor(float level)
    {
        int lit = Quantise(level);
        if (lit == _shown) return null;

        _shown = lit;
        return _icons[lit] ??= Build(lit);
    }

    /// <summary>Forgets what is on screen, so the next reading is drawn whatever it is.</summary>
    public void Reset() => _shown = -1;

    private static int Quantise(float level)
    {
        if (level <= 0.002f) return 0;

        // Square root spreads speech across the scale: linear amplitude spends most of its
        // time in the bottom segment and reads as a meter that barely moves.
        int lit = (int)MathF.Ceiling(MathF.Sqrt(Math.Clamp(level, 0f, 1f)) * Segments);
        return Math.Clamp(lit, 1, Segments);
    }

    private Icon Build(int lit)
    {
        using var bitmap = new Bitmap(_size, _size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);

            float inset = Math.Max(1f, _size * 0.08f);
            float width = _size - inset * 2;
            float cell = (_size - inset * 2) / Segments;
            float bar = Math.Max(1f, cell - Math.Max(1f, _size / 16f));

            for (int i = 0; i < Segments; i++)
            {
                // Bottom up, so it fills like a meter rather than draining like one.
                float y = _size - inset - (i + 1) * cell;
                bool on = i < lit;

                Color colour = on ? SegmentColour(i) : Color.FromArgb(70, 30, 34, 28);

                using var brush = new SolidBrush(colour);
                g.FillRectangle(brush, inset, y, width, bar);
            }
        }

        // FromHandle does not take ownership, so the icon is cloned and the handle released
        // immediately - otherwise this leaks one GDI handle for every icon ever built.
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>The panel's own meter colours, so the tray reads the same as the big one.</summary>
    private static Color SegmentColour(int index) => index switch
    {
        < 4 => PanelTheme.LampGreen,
        < 6 => PanelTheme.LampAmber,
        _ => PanelTheme.LampRed
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < _icons.Length; i++)
        {
            _icons[i]?.Dispose();
            _icons[i] = null;
        }
    }
}
