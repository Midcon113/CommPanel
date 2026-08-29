using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// Asked when a device goes offline and nothing on the panel can take over, but a hidden
/// device could.
///
/// Hidden means hidden: CommPanel never switches to a hidden device on its own. This is the
/// one case where it would otherwise leave the user on a dead device with a working one
/// sitting right there, so it asks instead of either guessing or staying silent.
/// </summary>
internal sealed class OfflineFallbackDialog : Form
{
    private static readonly Color Background = Color.FromArgb(0x26, 0x24, 0x21);
    private static readonly Color Surface = Color.FromArgb(0x33, 0x30, 0x2B);
    private static readonly Color Ink = Color.FromArgb(0xE6, 0xDF, 0xCD);
    private static readonly Color InkDim = Color.FromArgb(0x9C, 0x93, 0x84);

    private readonly List<AudioDevice> _candidates;
    private readonly ComboBox _deviceBox = new();
    private readonly ThemedCheckBox _unhide = new();

    public OfflineFallbackDialog(string lostDeviceName, bool isOutput, List<AudioDevice> candidates)
    {
        _candidates = candidates;

        Text = "Device offline";
        Icon = AppIcon.Load(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(470, 236);

        // The panel may well be in the tray with a game in front; this has to be seen.
        TopMost = true;

        BuildLayout(lostDeviceName, isOutput);
    }

    /// <summary>The device the user chose, or null if they declined.</summary>
    public AudioDevice? Chosen { get; private set; }

    /// <summary>True when the user asked for the chosen device to be shown from now on.</summary>
    public bool ShouldUnhide => _unhide.Checked;

    private void BuildLayout(string lostDeviceName, bool isOutput)
    {
        const int margin = 18;
        int width = ClientSize.Width - margin * 2;

        string kind = isOutput ? "output" : "input";

        var heading = new Label
        {
            Text = lostDeviceName + " has powered off",
            Bounds = new Rectangle(margin, margin, width, 22),
            ForeColor = Ink,
            Font = new Font("Segoe UI Semibold", 11f)
        };
        Controls.Add(heading);

        var body = new Label
        {
            Text = "No " + kind + " device on your panel can take over, so CommPanel has left it "
                 + "selected. These devices are hidden, but one of them could be used:",
            Bounds = new Rectangle(margin, margin + 28, width, 52),
            ForeColor = InkDim,
            Font = new Font("Segoe UI", 8.5f)
        };
        Controls.Add(body);

        _deviceBox.SetBounds(margin, margin + 86, width, 24);
        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.BackColor = Surface;
        _deviceBox.ForeColor = Ink;
        _deviceBox.FlatStyle = FlatStyle.Flat;
        foreach (var device in _candidates) _deviceBox.Items.Add(device.FullName);
        if (_deviceBox.Items.Count > 0) _deviceBox.SelectedIndex = 0;
        Controls.Add(_deviceBox);

        _unhide.SetBounds(margin, margin + 120, width, 22);
        _unhide.Text = "Also show this device on the panel from now on";
        _unhide.ForeColor = Ink;
        _unhide.BackColor = Background;
        _unhide.FlatAppearance.BorderColor = Color.FromArgb(0x6A, 0x64, 0x59);
        Controls.Add(_unhide);

        var switchButton = DialogButton("Switch to it", ClientSize.Width - margin - 210, ClientSize.Height - margin - 30, 110);
        switchButton.Click += (_, _) =>
        {
            int index = _deviceBox.SelectedIndex;
            if (index >= 0 && index < _candidates.Count) Chosen = _candidates[index];
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(switchButton);

        var stay = DialogButton("Stay put", ClientSize.Width - margin - 92, ClientSize.Height - margin - 30, 92);
        stay.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(stay);

        AcceptButton = switchButton;
        CancelButton = stay;
    }

    private static Button DialogButton(string text, int x, int y, int width)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Ink,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(0x55, 0x50, 0x48);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x45, 0x41, 0x39);
        return button;
    }
}
