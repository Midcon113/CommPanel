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
internal sealed class VoiceCallDialog : Form
{
    private static readonly Color Background = Color.FromArgb(0x26, 0x24, 0x21);
    private static readonly Color Surface = Color.FromArgb(0x33, 0x30, 0x2B);
    private static readonly Color Ink = Color.FromArgb(0xE6, 0xDF, 0xCD);
    private static readonly Color InkDim = Color.FromArgb(0x9C, 0x93, 0x84);
    private static readonly Color Bad = Color.FromArgb(0xD8, 0x7A, 0x5E);

    private readonly TextBox _codeBox = new();
    private readonly Label _problem = new();
    private readonly Button _callButton = new();

    public VoiceCallDialog(string? myCode, string? lastPeer)
    {
        Text = "Call";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(430, 252);

        BuildLayout(myCode, lastPeer);
    }

    /// <summary>The code or address the user entered, once it has been shown to parse.</summary>
    public string? Code { get; private set; }

    private void BuildLayout(string? myCode, string? lastPeer)
    {
        const int margin = 18;
        int width = ClientSize.Width - margin * 2;

        Controls.Add(new Label
        {
            Text = "Call someone directly",
            Bounds = new Rectangle(margin, margin, width, 22),
            ForeColor = Ink,
            Font = new Font("Segoe UI Semibold", 11f)
        });

        Controls.Add(new Label
        {
            Text = "Give them your code, type theirs below, and you both press Call. "
                 + "Audio goes straight between the two machines - no server, nothing recorded.",
            Bounds = new Rectangle(margin, margin + 26, width, 36),
            ForeColor = InkDim,
            Font = new Font("Segoe UI", 8.5f)
        });

        Controls.Add(new Label
        {
            Text = "YOUR CODE",
            Bounds = new Rectangle(margin, margin + 70, width, 16),
            ForeColor = InkDim,
            Font = new Font("Segoe UI", 8f)
        });

        var mine = new TextBox
        {
            Text = myCode ?? "finding your code...",
            Bounds = new Rectangle(margin, margin + 88, width, 26),
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Surface,
            ForeColor = myCode is null ? InkDim : Ink,
            Font = new Font("Consolas", 12f),
            TextAlign = HorizontalAlignment.Center
        };
        // Read-only but selectable, so it can be copied out of here as well as from the panel.
        Controls.Add(mine);

        Controls.Add(new Label
        {
            Text = "THEIR CODE",
            Bounds = new Rectangle(margin, margin + 124, width, 16),
            ForeColor = InkDim,
            Font = new Font("Segoe UI", 8f)
        });

        _codeBox.Bounds = new Rectangle(margin, margin + 142, width, 26);
        _codeBox.BorderStyle = BorderStyle.FixedSingle;
        _codeBox.BackColor = Surface;
        _codeBox.ForeColor = Ink;
        _codeBox.Font = new Font("Consolas", 12f);
        _codeBox.TextAlign = HorizontalAlignment.Center;
        _codeBox.CharacterCasing = CharacterCasing.Upper;
        _codeBox.Text = lastPeer ?? string.Empty;
        _codeBox.TextChanged += (_, _) => { _problem.Text = string.Empty; };
        Controls.Add(_codeBox);

        _problem.Bounds = new Rectangle(margin, margin + 172, width, 18);
        _problem.ForeColor = Bad;
        _problem.Font = new Font("Segoe UI", 8.5f);
        Controls.Add(_problem);

        int buttonTop = ClientSize.Height - margin - 28;

        _callButton.Text = "Call";
        _callButton.SetBounds(ClientSize.Width - margin - 100, buttonTop, 100, 28);
        _callButton.FlatStyle = FlatStyle.Flat;
        _callButton.BackColor = Surface;
        _callButton.ForeColor = Ink;
        _callButton.Click += OnCall;
        Controls.Add(_callButton);

        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(_callButton.Left - 8 - 90, buttonTop, 90, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Ink,
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancel);

        // Enter calls, Escape cancels. AcceptButton rather than a key handler so that the
        // validation below runs on Enter exactly as it does on the button.
        AcceptButton = _callButton;
        CancelButton = cancel;

        _codeBox.Select();
        _codeBox.SelectAll();
    }

    /// <summary>
    /// Validates before closing, so a mistyped code is corrected here rather than becoming a
    /// failed call the user has to work out for themselves.
    /// </summary>
    private void OnCall(object? sender, EventArgs e)
    {
        string entered = _codeBox.Text.Trim();

        if (entered.Length == 0)
        {
            _problem.Text = "Type the code they read out to you.";
            _codeBox.Select();
            return;
        }

        if (!VoiceAddress.TryParse(entered, out _))
        {
            _problem.Text = "That is not a link code. It looks like ABCDE-FGHIJ.";
            _codeBox.Select();
            _codeBox.SelectAll();
            return;
        }

        Code = entered;
        DialogResult = DialogResult.OK;
        Close();
    }
}
