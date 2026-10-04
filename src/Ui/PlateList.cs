using System.Drawing.Drawing2D;

namespace CommPanel.Ui;

/// <summary>
/// A list drawn entirely in the panel's own hand: dark well, engraved rows, a stamped scroll
/// thumb, and - when <see cref="ShowLamps"/> is set - a lamp per row that the user clicks to
/// turn a device on or off, in place of a Windows tick box.
///
/// A WinForms ListBox was the obvious choice and was wrong: its scrollbar is painted by the
/// system and comes out as a bright white slab in the middle of a stamped-steel panel, with
/// no supported way to restyle it. Drawing the whole control is less work than fighting that.
/// </summary>
internal sealed class PlateList : ChassisControl
{
    private readonly List<string> _items = new();
    private readonly HashSet<int> _lit = new();

    private int _selected = -1;
    private int _top;           // first visible row
    private int _hotRow = -1;
    private bool _draggingThumb;
    private int _dragOffset;

    public PlateList()
    {
        Cursor = Cursors.Default;
        TabStop = true;
    }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Draws a lamp per row; clicking it toggles that row rather than selecting it.</summary>
    public bool ShowLamps { get; set; }

    public int RowHeight { get; set; } = 20;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value < 0 || value >= _items.Count ? -1 : value;
            EnsureVisible(_selected);
            Invalidate();
        }
    }

    public event EventHandler? SelectedIndexChanged;

    public void Add(string text)
    {
        _items.Add(text);
        Invalidate();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        _items.RemoveAt(index);

        // Lamp state is keyed by position, so everything above the hole shifts down one.
        var moved = new HashSet<int>();
        foreach (int lit in _lit)
        {
            if (lit < index) moved.Add(lit);
            else if (lit > index) moved.Add(lit - 1);
        }
        _lit.Clear();
        foreach (int lit in moved) _lit.Add(lit);

        if (_selected >= _items.Count) _selected = _items.Count - 1;
        ClampScroll();
        Invalidate();
    }

    public void Clear()
    {
        _items.Clear();
        _lit.Clear();
        _selected = -1;
        _top = 0;
        Invalidate();
    }

    public bool IsLit(int index) => _lit.Contains(index);

    public void SetLit(int index, bool lit)
    {
        if (lit) _lit.Add(index); else _lit.Remove(index);
        Invalidate();
    }

    private int Row => Math.Max(1, Scaled(RowHeight));

    private int VisibleRows => Math.Max(1, Height / Row);

    private bool NeedsScroll => _items.Count > VisibleRows;

    private int ScrollWidth => Scaled(10);

    private int MaxTop => Math.Max(0, _items.Count - VisibleRows);

    private void ClampScroll() => _top = Math.Clamp(_top, 0, MaxTop);

    private void EnsureVisible(int index)
    {
        if (index < 0) return;
        if (index < _top) _top = index;
        else if (index >= _top + VisibleRows) _top = index - VisibleRows + 1;
        ClampScroll();
    }

    private int RowAt(Point point)
    {
        if (point.Y < 0) return -1;
        int index = _top + point.Y / Row;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    private Rectangle ThumbBounds()
    {
        if (!NeedsScroll) return Rectangle.Empty;

        int trackLeft = Width - ScrollWidth;
        float fraction = VisibleRows / (float)_items.Count;
        int thumbHeight = Math.Max(Scaled(18), (int)(Height * fraction));
        int travel = Height - thumbHeight;
        int offset = MaxTop == 0 ? 0 : (int)(travel * (_top / (float)MaxTop));

        return new Rectangle(trackLeft + Scaled(2), offset, ScrollWidth - Scaled(4), thumbHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        if (e.Button == MouseButtons.Left && NeedsScroll && e.X >= Width - ScrollWidth)
        {
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _draggingThumb = true;
                _dragOffset = e.Y - thumb.Top;
            }
            else
            {
                // Click the track: page towards the click, the way a scrollbar does.
                _top += e.Y < thumb.Top ? -VisibleRows : VisibleRows;
                ClampScroll();
                Invalidate();
            }
            base.OnMouseDown(e);
            return;
        }

        int index = RowAt(e.Location);
        if (index >= 0)
        {
            if (ShowLamps && e.X < Scaled(7) + Scaled(9) + Scaled(5))
            {
                SetLit(index, !IsLit(index));
            }
            else
            {
                _selected = index;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingThumb)
        {
            int thumbHeight = ThumbBounds().Height;
            int travel = Math.Max(1, Height - thumbHeight);
            float position = Math.Clamp((e.Y - _dragOffset) / (float)travel, 0f, 1f);
            _top = (int)MathF.Round(position * MaxTop);
            Invalidate();
        }
        else
        {
            int row = RowAt(e.Location);
            if (row != _hotRow)
            {
                _hotRow = row;
                Invalidate();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _draggingThumb = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hotRow = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (NeedsScroll)
        {
            _top -= Math.Sign(e.Delta) * 3;
            ClampScroll();
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Space or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Move(-1); e.Handled = true; break;
            case Keys.Down: Move(1); e.Handled = true; break;
            case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
            case Keys.End: SelectedIndex = _items.Count - 1; e.Handled = true; break;
            case Keys.Space when ShowLamps && _selected >= 0:
                SetLit(_selected, !IsLit(_selected));
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        SelectedIndex = Math.Clamp(_selected + delta, 0, _items.Count - 1);
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnResize(EventArgs e)
    {
        ClampScroll();
        base.OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var well = new SolidBrush(PanelTheme.FieldBack))
            g.FillRectangle(well, 0, 0, Width, Height);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        int row = Row;
        int listRight = NeedsScroll ? Width - ScrollWidth : Width;

        for (int i = _top; i < _items.Count; i++)
        {
            int y = (i - _top) * row;
            if (y >= Height) break;

            bool selected = i == _selected;

            if (selected)
            {
                using var fill = new SolidBrush(PanelTheme.RowSelected);
                g.FillRectangle(fill, 0, y, listRight, row);
            }
            else if (i == _hotRow)
            {
                using var fill = new SolidBrush(Color.FromArgb(40, PanelTheme.EdgeHighlight));
                g.FillRectangle(fill, 0, y, listRight, row);
            }

            int left = Scaled(7);

            if (ShowLamps)
            {
                int lamp = Scaled(9);
                var rect = new RectangleF(left, y + (row - lamp) / 2f, lamp, lamp);
                PanelTheme.DrawLamp(g, rect, PanelTheme.LampGreen, IsLit(i), 0.6f);
                left += lamp + Scaled(9);
            }

            // A row that is switched off is dimmed, so the list reads at a glance rather than
            // needing every lamp to be examined one by one.
            Color ink = !ShowLamps || IsLit(i) ? PanelTheme.TextPrimary : PanelTheme.TextSecondary;

            TextRenderer.DrawText(g, _items[i], Font,
                new Rectangle(left, y, listRight - left - Scaled(6), row), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        if (NeedsScroll) DrawScrollThumb(g);

        if (Focused)
        {
            using var pen = new Pen(Color.FromArgb(110, PanelTheme.TextPrimary)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private void DrawScrollThumb(Graphics g)
    {
        int trackLeft = Width - ScrollWidth;

        using (var track = new SolidBrush(Color.FromArgb(90, PanelTheme.EdgeShadow)))
            g.FillRectangle(track, trackLeft, 0, ScrollWidth, Height);

        var thumb = ThumbBounds();
        if (thumb.IsEmpty) return;

        using var path = PanelTheme.RoundedRect(thumb, Math.Max(1f, thumb.Width / 2f));
        using (var fill = new LinearGradientBrush(
                   new RectangleF(thumb.X, thumb.Y, thumb.Width, thumb.Height + 1),
                   PanelTheme.ButtonTop, PanelTheme.ButtonBottom, 0f))
            g.FillPath(fill, path);
        using (var edge = new Pen(Color.FromArgb(160, PanelTheme.EdgeShadow)))
            g.DrawPath(edge, path);
    }
}
