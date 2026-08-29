using CommPanel.Audio;
using CommPanel.Core;

namespace CommPanel.Ui;

/// <summary>
/// Walks the user through teaching CommPanel how their wireless headset reports power state.
///
/// The sequence is off, on, then off again. Two separate power-off captures are what make
/// the result trustworthy: a byte that really encodes power holds the same value in both,
/// while counters, battery drift and stray traffic from other devices do not.
/// </summary>
internal sealed class LearnHeadsetForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x26, 0x24, 0x21);
    private static readonly Color Surface = Color.FromArgb(0x33, 0x30, 0x2B);
    private static readonly Color Ink = Color.FromArgb(0xE6, 0xDF, 0xCD);
    private static readonly Color InkDim = Color.FromArgb(0x9C, 0x93, 0x84);

    private readonly List<AudioDevice> _outputs;
    private readonly ComboBox _deviceBox = new();
    private readonly Label _stepLabel = new();
    private readonly Label _instruction = new();
    private readonly Label _status = new();
    private readonly Button _next = new();
    private readonly Button _cancel = new();

    private HeadsetLearnSession? _session;
    private int _step;

    /// <summary>The learned profile, valid only when the dialog returns OK.</summary>
    public HeadsetProfile? Result { get; private set; }

    public LearnHeadsetForm(List<AudioDevice> outputs)
    {
        _outputs = outputs;

        Text = "Learn my headset";
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
        ClientSize = new Size(520, 404);

        BuildLayout();
        ShowStep();
    }

    private void BuildLayout()
    {
        const int margin = 18;
        int width = ClientSize.Width - margin * 2;

        _stepLabel.SetBounds(margin, margin, width, 18);
        _stepLabel.ForeColor = Color.FromArgb(0xC9, 0xBF, 0xA8);
        _stepLabel.Font = new Font("Consolas", 8.25f, FontStyle.Bold);
        Controls.Add(_stepLabel);

        _instruction.SetBounds(margin, margin + 26, width, 200);
        _instruction.ForeColor = Ink;
        _instruction.Font = new Font("Segoe UI", 10f);
        Controls.Add(_instruction);

        var pickLabel = new Label
        {
            Text = "Which output device is your headset?",
            Bounds = new Rectangle(margin, margin + 232, width, 18),
            ForeColor = InkDim,
            Font = new Font("Segoe UI", 8.25f)
        };
        Controls.Add(pickLabel);

        _deviceBox.SetBounds(margin, margin + 254, width, 24);
        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.BackColor = Surface;
        _deviceBox.ForeColor = Ink;
        _deviceBox.FlatStyle = FlatStyle.Flat;
        foreach (var device in _outputs) _deviceBox.Items.Add(device.FullName);
        if (_deviceBox.Items.Count > 0) _deviceBox.SelectedIndex = GuessHeadsetIndex();
        Controls.Add(_deviceBox);

        _status.SetBounds(margin, margin + 288, width, 34);
        _status.ForeColor = InkDim;
        _status.Font = new Font("Consolas", 8.25f);
        Controls.Add(_status);

        _next.SetBounds(ClientSize.Width - margin - 110, ClientSize.Height - margin - 28, 110, 28);
        StyleButton(_next);
        _next.Click += (_, _) => Advance();
        Controls.Add(_next);

        _cancel.Text = "Cancel";
        _cancel.SetBounds(ClientSize.Width - margin - 200, ClientSize.Height - margin - 28, 84, 28);
        StyleButton(_cancel);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_cancel);

        AcceptButton = _next;
        CancelButton = _cancel;
    }

    /// <summary>Preselects the most headset-looking output, so the common case needs no thought.</summary>
    private int GuessHeadsetIndex()
    {
        for (int i = 0; i < _outputs.Count; i++)
        {
            var device = _outputs[i];
            if (device.FormFactor is FormFactor.Headset or FormFactor.Headphones) return i;
        }

        for (int i = 0; i < _outputs.Count; i++)
        {
            string text = _outputs[i].FullName;
            if (text.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("wireless", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private string SelectedAdapter()
    {
        int index = _deviceBox.SelectedIndex;
        if (index < 0 || index >= _outputs.Count) return string.Empty;

        var device = _outputs[index];
        return string.IsNullOrWhiteSpace(device.Adapter) ? device.ShortName : device.Adapter;
    }

    private void ShowStep()
    {
        switch (_step)
        {
            case 0:
                _stepLabel.Text = "STEP 1 OF 4";
                _instruction.Text =
                    "This teaches CommPanel how your headset reports being switched on and off.\r\n\r\n" +
                    "It is needed because a wireless base station stays plugged in whether or not the " +
                    "headset is on, so Windows cannot tell the difference.\r\n\r\n" +
                    "Pick your headset below, make sure it is switched ON, then click Start.\r\n\r\n" +
                    "Nothing is written to the device at any point — CommPanel only listens.";
                _next.Text = "Start";
                _deviceBox.Enabled = true;
                break;

            case 1:
                _stepLabel.Text = "STEP 2 OF 4 — CAPTURING";
                _instruction.Text =
                    "Switch the headset OFF now.\r\n\r\n" +
                    "Wait for it to finish powering down — a few seconds — then click Next.";
                _next.Text = "Next";
                _deviceBox.Enabled = false;
                break;

            case 2:
                _stepLabel.Text = "STEP 3 OF 4 — CAPTURING";
                _instruction.Text =
                    "Now switch the headset back ON.\r\n\r\n" +
                    "Wait until it has fully connected, then click Next.";
                _next.Text = "Next";
                break;

            case 3:
                _stepLabel.Text = "STEP 4 OF 4 — CAPTURING";
                _instruction.Text =
                    "Switch the headset OFF once more.\r\n\r\n" +
                    "This second power-off is what confirms the reading is genuine rather than a " +
                    "coincidence. Wait for it to power down, then click Finish.\r\n\r\n" +
                    "You can switch it back on afterwards.";
                _next.Text = "Finish";
                break;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            _status.Text = string.Empty;
            return;
        }

        _status.Text = string.Format("listening on {0} interfaces · {1} reports captured",
            _session.InterfaceCount, _session.CaptureCount);
    }

    private void Advance()
    {
        switch (_step)
        {
            case 0:
                if (SelectedAdapter().Length == 0)
                {
                    Warn("Select which output device is your headset first.");
                    return;
                }

                _session = new HeadsetLearnSession();
                int opened = _session.Start();
                if (opened == 0)
                {
                    Warn("No vendor-specific HID interfaces were found, so there is nothing to listen to. "
                       + "This headset cannot be detected this way.");
                    _session.Dispose();
                    _session = null;
                    return;
                }

                _session.BeginPhase(LearnPhase.PoweredOffFirst);
                _step = 1;
                break;

            case 1:
                _session?.BeginPhase(LearnPhase.PoweredOn);
                _step = 2;
                break;

            case 2:
                _session?.BeginPhase(LearnPhase.PoweredOffSecond);
                _step = 3;
                break;

            case 3:
                Finish();
                return;
        }

        ShowStep();
    }

    private void Finish()
    {
        if (_session is null) return;

        _session.BeginPhase(LearnPhase.Idle);
        var captures = _session.Snapshot();
        _session.Dispose();
        _session = null;

        var outcome = HeadsetLearner.Analyse(captures, SelectedAdapter());

        if (!outcome.Succeeded)
        {
            MessageBox.Show(this,
                outcome.Explanation + "\r\n\r\n" +
                string.Format("({0} reports were captured.)", captures.Count),
                "Could not learn this headset", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            _step = 0;
            ShowStep();
            return;
        }

        var profile = outcome.Profile!;

        var confirm = MessageBox.Show(this,
            "CommPanel learned how to detect this headset.\r\n\r\n" +
            profile.Describe() + "\r\n\r\n" +
            outcome.Explanation + "\r\n\r\n" +
            "Save this profile?",
            "Headset learned", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

        if (confirm != DialogResult.OK)
        {
            _step = 0;
            ShowStep();
            return;
        }

        Result = profile;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Learn my headset", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session?.Dispose();
        _session = null;
        base.OnFormClosed(e);
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Surface;
        button.ForeColor = Ink;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = Color.FromArgb(0x55, 0x50, 0x48);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x45, 0x41, 0x39);
    }
}
