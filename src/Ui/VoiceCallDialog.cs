using CommPanel.Core;
using CommPanel.Voice;

namespace CommPanel.Ui;

/// <summary>
/// Asks for the other person's link code.
///
/// Deliberately a dialog rather than a field on the panel: a code is typed once at the start
/// of a call and never again, and a text box sitting permanently on a stamped-metal panel
/// would look like neither. It also gives room to say what the code is and where to get it,
/// which a 10-character box could not.
/// </summary>
internal sealed class VoiceCallDialog : PanelDialog
{
    /// <summary>Close enough to the recess fill that the field reads as part of the panel.</summary>
    private static readonly Color FieldBack = Color.FromArgb(0x22, 0x20, 0x1D);

    private readonly Func<string?> _codeProvider;
    private readonly TextBox _theirCode = new();
    private readonly PlateButton _callKey = new();
    private readonly PlateButton _cancelKey = new();

    /// <summary>Address discovery may still be in flight when this opens; poll until it lands.</summary>
    private readonly System.Windows.Forms.Timer _codePoll = new();

    private Rectangle _introRect;
    private Rectangle _yourCaption;
    private Rectangle _yourRecess;
    private Rectangle _theirCaption;
    private Rectangle _theirRecess;
    private Rectangle _problemRect;

    private string? _shownCode;
    private string _problem = string.Empty;

    public VoiceCallDialog(AppSettings settings, Func<string?> codeProvider, string? lastPeer)
        : base(settings, "CALL", "DIRECT VOICE LINK")
    {
        _codeProvider = codeProvider;
        _shownCode = codeProvider();

        BuildLayout(lastPeer);

        _codePoll.Interval = 400;
        _codePoll.Tick += (_, _) =>
        {
            string? code = _codeProvider();
            if (code == _shownCode) return;
            _shownCode = code;
            Invalidate(_yourRecess);
        };
        _codePoll.Start();
    }

    /// <summary>The code or address the user entered, once it has been shown to parse.</summary>
    public string? Code { get; private set; }

    private void BuildLayout(string? lastPeer)
    {
        int margin = EdgeMargin;
        int width = Scaled(420) - margin * 2;
        int y = BodyTop;

        _introRect = new Rectangle(margin, y, width, Scaled(42));
        y += Scaled(50);

        int captionHeight = Scaled(16);
        int fieldHeight = Scaled(34);

        _yourCaption = new Rectangle(margin, y, width, captionHeight);
        y += captionHeight + Scaled(4);
        _yourRecess = new Rectangle(margin, y, width, fieldHeight);
        y += fieldHeight + Scaled(12);

        _theirCaption = new Rectangle(margin, y, width, captionHeight);
        y += captionHeight + Scaled(4);
        _theirRecess = new Rectangle(margin, y, width, fieldHeight);
        y += fieldHeight + Scaled(6);

        _problemRect = new Rectangle(margin, y, width, Scaled(18));
        y += Scaled(18) + Scaled(12);

        // The field sits inside the recess rather than drawing its own border, so the
        // stamped lip around it is the only edge you see.
        int inset = Scaled(5);
        _theirCode.Bounds = new Rectangle(_theirRecess.Left + inset, _theirRecess.Top + inset,
                                          _theirRecess.Width - inset * 2, _theirRecess.Height - inset * 2);
        _theirCode.BorderStyle = BorderStyle.None;
        _theirCode.BackColor = FieldBack;
        _theirCode.ForeColor = PanelTheme.TextPrimary;
        _theirCode.Font = TitleFont;
        _theirCode.TextAlign = HorizontalAlignment.Center;
        _theirCode.CharacterCasing = CharacterCasing.Upper;
        _theirCode.Text = lastPeer ?? string.Empty;
        _theirCode.TextChanged += (_, _) =>
        {
            if (_problem.Length == 0) return;
            _problem = string.Empty;
            Invalidate(_problemRect);
        };
        Controls.Add(_theirCode);

        int keyHeight = Scaled(30);
        int callWidth = Scaled(104);
        int cancelWidth = Scaled(92);

        _callKey.Text = "CALL";
        _callKey.Font = StencilFont;
        _callKey.ShowLamp = true;
        _callKey.IsOn = true;
        _callKey.LampColor = PanelTheme.LampGreen;
        _callKey.Bounds = new Rectangle(margin + width - callWidth, y, callWidth, keyHeight);
        _callKey.Click += (_, _) => TryCall();
        Controls.Add(_callKey);

        _cancelKey.Text = "CANCEL";
        _cancelKey.Font = StencilFont;
        _cancelKey.Bounds = new Rectangle(_callKey.Left - Scaled(8) - cancelWidth, y, cancelWidth, keyHeight);
        _cancelKey.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_cancelKey);

        y += keyHeight + margin;

        ClientSize = new Size(Scaled(420), y);
        PlaceHeader();
        RebuildChassis();

        // Caret at the end rather than a select-all: a Windows selection block is the one
        // thing in here that cannot be themed, and a remembered code is usually the one you
        // want to dial again anyway.
        _theirCode.Select();
        _theirCode.Select(_theirCode.TextLength, 0);
    }

    protected override void DrawChassis(Graphics g)
    {
        float radius = Scaled(5);
        PanelTheme.DrawRecess(g, _yourRecess, radius);
        PanelTheme.DrawRecess(g, _theirRecess, radius);

        DrawBody(g, "Give them your code, type theirs below, and you both press CALL. "
                  + "Audio goes straight between the two machines — no server, nothing recorded.",
                 _introRect);

        DrawCaption(g, "YOUR CODE", _yourCaption);
        DrawCaption(g, "THEIR CODE", _theirCaption);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // Both of these change while the dialog is open, so they are painted rather than
        // baked into the chassis: the code arrives when address discovery answers, and the
        // problem line appears when an entry does not parse.
        string code = _shownCode ?? "FINDING…";

        PanelTheme.DrawEngraved(e.Graphics, code, TitleFont, _yourRecess,
            _shownCode is null ? PanelTheme.TextSecondary : PanelTheme.TextPrimary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);

        if (_problem.Length > 0)
        {
            TextRenderer.DrawText(e.Graphics, _problem, StencilFont, _problemRect,
                PanelTheme.LampAmber,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>
    /// Validates before closing, so a mistyped code is corrected here rather than becoming a
    /// failed call the user has to work out for themselves.
    /// </summary>
    private void TryCall()
    {
        string entered = _theirCode.Text.Trim();

        if (entered.Length == 0)
        {
            ShowProblem("TYPE THE CODE THEY READ OUT TO YOU");
            return;
        }

        if (!VoiceAddress.TryParse(entered, out _))
        {
            ShowProblem("NOT A LINK CODE — THEY LOOK LIKE ABCDE-FGHIJ");
            return;
        }

        Code = entered;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowProblem(string text)
    {
        _problem = text;
        Invalidate(_problemRect);
        _theirCode.Select();
        _theirCode.Select(_theirCode.TextLength, 0);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // The keys here are stamped plates rather than buttons, so Enter and Escape are
        // wired up by hand - AcceptButton and CancelButton only know about IButtonControl.
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Return)
        {
            TryCall();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _codePoll.Stop();
            _codePoll.Dispose();
        }
        base.Dispose(disposing);
    }
}
