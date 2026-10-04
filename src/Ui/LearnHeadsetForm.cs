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
internal sealed class LearnHeadsetForm : PanelDialog
{
    private const int LogicalHeight = 452;

    private readonly List<AudioDevice> _outputs;
    private readonly PlateList _deviceList = new();
    private readonly PlateButton _next;

    private Rectangle _stepRect;
    private Rectangle _instructionRect;
    private Rectangle _pickCaption;
    private Rectangle _listRecess;
    private Rectangle _statusRect;

    private HeadsetLearnSession? _session;
    private int _step;
    private string _stepText = string.Empty;
    private string _instruction = string.Empty;
    private string _status = string.Empty;

    /// <summary>The learned profile, valid only when the dialog returns OK.</summary>
    public HeadsetProfile? Result { get; private set; }

    public LearnHeadsetForm(AppSettings settings, List<AudioDevice> outputs)
        : base(settings, "LEARN MY HEADSET", "TEACH THE PANEL YOUR POWER SIGNAL", LogicalHeight)
    {
        _outputs = outputs;
        _next = BuildLayout();
        ShowStep();
    }

    private PlateButton BuildLayout()
    {
        int margin = EdgeMargin;
        int width = Scaled(520) - margin * 2;
        int y = BodyTop;

        _stepRect = new Rectangle(margin, y, width, Scaled(18));
        y += Scaled(24);

        _instructionRect = new Rectangle(margin, y, width, Scaled(150));
        y += Scaled(158);

        _pickCaption = new Rectangle(margin, y, width, Scaled(16));
        y += Scaled(20);

        _listRecess = new Rectangle(margin, y, width, Scaled(84));
        _deviceList.Bounds = Inside(_listRecess);
        StyleList(_deviceList);
        foreach (var device in _outputs) _deviceList.Add(device.FullName);
        if (_deviceList.Items.Count > 0) _deviceList.SelectedIndex = GuessHeadsetIndex();
        Controls.Add(_deviceList);
        y += _listRecess.Height + Scaled(10);

        _statusRect = new Rectangle(margin, y, width, Scaled(20));
        y += Scaled(28);

        int keyHeight = Scaled(30);
        int nextWidth = Scaled(112);
        int cancelWidth = Scaled(92);

        var next = Key("START", new Rectangle(margin + width - nextWidth, y, nextWidth, keyHeight),
                       (_, _) => Advance());
        next.ShowLamp = true;
        next.IsOn = true;
        next.LampColor = PanelTheme.LampGreen;

        Key("CANCEL", new Rectangle(next.Left - Scaled(8) - cancelWidth, y, cancelWidth, keyHeight),
            (_, _) => { DialogResult = DialogResult.Cancel; Close(); });

        y += keyHeight + margin;

        ClientSize = new Size(Scaled(520), y);
        PlaceHeader();
        RebuildChassis();

        return next;
    }

    protected override void DrawChassis(Graphics g)
    {
        PanelTheme.DrawRecess(g, _listRecess, Scaled(5));
        DrawCaption(g, "WHICH OUTPUT IS YOUR HEADSET", _pickCaption);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // The step, the instruction and the capture count all change as the wizard runs, so
        // they are painted here rather than baked into the chassis bitmap.
        DrawCaption(e.Graphics, _stepText, _stepRect);

        TextRenderer.DrawText(e.Graphics, _instruction, LabelFont, _instructionRect,
            PanelTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

        TextRenderer.DrawText(e.Graphics, _status, StencilFont, _statusRect,
            Color.FromArgb(170, PanelTheme.TextSecondary),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
        int index = _deviceList.SelectedIndex;
        if (index < 0 || index >= _outputs.Count) return string.Empty;

        var device = _outputs[index];
        return string.IsNullOrWhiteSpace(device.Adapter) ? device.ShortName : device.Adapter;
    }

    private void ShowStep()
    {
        switch (_step)
        {
            case 0:
                _stepText = "STEP 1 OF 4";
                _instruction =
                    "This teaches CommPanel how your headset reports being switched on and off.\r\n\r\n" +
                    "It is needed because a wireless base station stays plugged in whether or not the " +
                    "headset is on, so Windows cannot tell the difference.\r\n\r\n" +
                    "Pick your headset below, make sure it is switched ON, then press START.\r\n\r\n" +
                    "Nothing is written to the device at any point — CommPanel only listens.";
                _next.Text = "START";
                _deviceList.Enabled = true;
                break;

            case 1:
                _stepText = "STEP 2 OF 4 — CAPTURING";
                _instruction =
                    "Switch the headset OFF now.\r\n\r\n" +
                    "Wait for it to finish powering down — a few seconds — then press NEXT.";
                _next.Text = "NEXT";
                _deviceList.Enabled = false;
                break;

            case 2:
                _stepText = "STEP 3 OF 4 — CAPTURING";
                _instruction =
                    "Now switch the headset back ON.\r\n\r\n" +
                    "Wait until it has fully connected, then press NEXT.";
                _next.Text = "NEXT";
                break;

            case 3:
                _stepText = "STEP 4 OF 4 — CAPTURING";
                _instruction =
                    "Switch the headset OFF once more.\r\n\r\n" +
                    "This second power-off is what confirms the reading is genuine rather than a " +
                    "coincidence. Wait for it to power down, then press FINISH.\r\n\r\n" +
                    "You can switch it back on afterwards.";
                _next.Text = "FINISH";
                break;
        }

        UpdateStatus();
        _next.Invalidate();
        Invalidate();
    }

    private void UpdateStatus()
    {
        _status = _session is null
            ? string.Empty
            : string.Format("LISTENING ON {0} INTERFACES · {1} REPORTS CAPTURED",
                            _session.InterfaceCount, _session.CaptureCount);

        Invalidate(_statusRect);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session?.Dispose();
        _session = null;
        base.OnFormClosed(e);
    }
}
