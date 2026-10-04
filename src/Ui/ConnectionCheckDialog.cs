using CommPanel.Core;
using CommPanel.Voice;

namespace CommPanel.Ui;

/// <summary>
/// Runs the connection check and shows what it found.
///
/// Written for the person at the other end of the telephone as much as the person at the
/// machine: the headline is big enough to read out, and COPY puts a single line on the
/// clipboard that can be pasted into a chat window.
/// </summary>
internal sealed class ConnectionCheckDialog : PanelDialog
{
    private const int LogicalHeight = 340;

    private readonly PlateButton _again;
    private readonly PlateButton _copy;

    private Rectangle _introRect;
    private Rectangle _verdictRecess;
    private Rectangle _detailRect;
    private Rectangle _addressRect;

    private NatReport? _report;
    private bool _running;
    private CancellationTokenSource? _cancel;

    public ConnectionCheckDialog(AppSettings settings)
        : base(settings, "CONNECTION CHECK", "CAN A DIRECT CALL GET THROUGH")
    {
        (_again, _copy) = BuildLayout();
        Start();
    }

    private (PlateButton Again, PlateButton Copy) BuildLayout()
    {
        int margin = EdgeMargin;
        int width = Scaled(470) - margin * 2;
        int y = BodyTop;

        _introRect = new Rectangle(margin, y, width, Scaled(30));
        y += Scaled(36);

        _verdictRecess = new Rectangle(margin, y, width, Scaled(44));
        y += Scaled(52);

        _detailRect = new Rectangle(margin, y, width, Scaled(84));
        y += Scaled(90);

        _addressRect = new Rectangle(margin, y, width, Scaled(18));
        y += Scaled(26);

        int keyHeight = Scaled(30);
        int againWidth = Scaled(108);
        int copyWidth = Scaled(92);
        int closeWidth = Scaled(92);

        var close = Key("CLOSE", new Rectangle(margin + width - closeWidth, y, closeWidth, keyHeight),
                        (_, _) => { DialogResult = DialogResult.OK; Close(); });

        var copy = Key("COPY", new Rectangle(close.Left - Scaled(8) - copyWidth, y, copyWidth, keyHeight),
                       (_, _) => CopySummary());
        copy.Enabled = false;

        var again = Key("CHECK AGAIN", new Rectangle(margin, y, againWidth, keyHeight),
                        (_, _) => Start());
        again.ShowLamp = true;
        again.LampColor = PanelTheme.LampGreen;

        y += keyHeight + margin;

        ClientSize = new Size(Scaled(470), y);
        PlaceHeader();
        RebuildChassis();

        return (again, copy);
    }

    protected override void DrawChassis(Graphics g)
    {
        PanelTheme.DrawRecess(g, _verdictRecess, Scaled(5));

        DrawBody(g, "Asks three public servers what address this machine appears to come from. "
                  + "Nothing is sent to the other person and no call is placed.", _introRect);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;

        string headline = _running ? "CHECKING…" : _report?.Headline ?? "—";
        Color ink = _running
            ? PanelTheme.TextSecondary
            : _report is null ? PanelTheme.TextSecondary
            : _report.Good ? PanelTheme.LampGreen : PanelTheme.LampAmber;

        PanelTheme.DrawEngraved(g, headline, TitleFont, _verdictRecess, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        string detail = _running
            ? "Waiting for the servers to answer. This takes a few seconds."
            : _report?.Detail ?? string.Empty;

        TextRenderer.DrawText(g, detail, SmallFont, _detailRect, PanelTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

        if (_report?.PublicEndPoint is not null)
        {
            TextRenderer.DrawText(g,
                "SEEN AS " + _report.PublicEndPoint.Address + "   ·   "
                + _report.ServersAnswered + " OF " + _report.ServersTried + " SERVERS ANSWERED",
                StencilFont, _addressRect, Color.FromArgb(160, PanelTheme.TextSecondary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>
    /// Runs the check on a worker thread. It makes blocking network calls for a couple of
    /// seconds, which on the UI thread would freeze the dialog it is reporting into.
    /// </summary>
    private void Start()
    {
        if (_running) return;

        _running = true;
        _report = null;
        _again.Enabled = false;
        _again.IsOn = true;
        _copy.Enabled = false;
        Invalidate();

        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        var worker = new Thread(() =>
        {
            NatReport report;
            try { report = NatCheck.Run(token); }
            catch (Exception ex)
            {
                report = new NatReport
                {
                    Verdict = NatVerdict.Unclear,
                    Headline = "THE CHECK FAILED",
                    Detail = ex.Message
                };
            }

            if (token.IsCancellationRequested) return;

            try { BeginInvoke(new Action(() => Finish(report))); }
            catch (ObjectDisposedException) { /* dialog closed while checking */ }
            catch (InvalidOperationException) { /* handle gone */ }
        })
        {
            IsBackground = true,
            Name = "CommPanel connection check"
        };

        worker.Start();
    }

    private void Finish(NatReport report)
    {
        _running = false;
        _report = report;
        _again.Enabled = true;
        _again.IsOn = false;
        _copy.Enabled = true;
        _again.Invalidate();
        _copy.Invalidate();
        Invalidate();
    }

    private void CopySummary()
    {
        if (_report is null) return;

        try { Clipboard.SetText(_report.Summary); }
        catch { /* the clipboard can be held by another process; it is on screen regardless */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Escape or Keys.Enter or Keys.Return)
        {
            DialogResult = DialogResult.OK;
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cancel?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
        }
        base.Dispose(disposing);
    }
}
