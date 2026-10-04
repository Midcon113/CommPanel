using CommPanel.Voice;

namespace CommPanel.Ui;

/// <summary>
/// The panel's direct voice link: a strip carrying your own code, a key to dial someone
/// else's, a microphone cut, and send and receive meters.
///
/// Kept in its own file because it is a self-contained section with its own timer and its own
/// lifetime - the socket stays open while CommPanel is in the tray, which is the whole point,
/// whereas the metering in the main panel exists only while the window is on screen.
/// </summary>
internal sealed partial class PanelForm
{
    private const int VoiceRowHeight = 30;
    private const int VoiceRowGap = 8;
    private const int VoicePadding = 10;

    private readonly PlateButton _voiceToggle = new();
    private readonly PlateLabel _voiceCode = new();
    private readonly PlateButton _voiceCopy = new();
    private readonly PlateButton _voiceCall = new();
    private readonly PlateButton _voiceMute = new();
    private readonly LevelMeter _voiceSend = new();
    private readonly LevelMeter _voiceReceive = new();

    /// <summary>
    /// Drives the voice strip's meters and keeps its keys in step with the link's state.
    ///
    /// The link's own state changes arrive on its background thread; this polls instead, so
    /// there is no marshalling to get wrong and no handler that can fire into a disposed form.
    /// It runs only while the panel is on screen - a call in the tray needs no painting.
    /// </summary>
    private readonly System.Windows.Forms.Timer _voiceTimer = new();

    private VoiceSession? _voice;
    private VoiceLinkState _voiceStateShown = VoiceLinkState.Idle;
    private string? _voiceCodeShown;

    private Rectangle _voicePlate;
    private Rectangle _voiceHeader;

    private string VoiceToggleCaption => _settings.VoiceEnabled ? "VOICE  ▲" : "VOICE  ▼";

    private bool VoiceExpanded => _settings.VoiceEnabled;

    private void BuildVoiceStrip()
    {
        _voiceToggle.Font = _stencilFont;
        _voiceToggle.ShowLamp = true;
        _voiceToggle.LampColor = PanelTheme.LampBlue;
        _voiceToggle.IsOn = _settings.VoiceEnabled;
        _voiceToggle.Text = VoiceToggleCaption;
        _voiceToggle.Click += (_, _) => ToggleVoice();

        // The one thing on this panel that gets read out loud, so it takes the larger face
        // the device keys use rather than the small stencil everything else is set in.
        _voiceCode.Font = _labelFont;
        _voiceCode.ShowLamp = true;
        _voiceCode.LampColor = PanelTheme.LampBlue;
        _voiceCode.Text = "MY CODE   — — — — —";

        _voiceCopy.Font = _stencilFont;
        _voiceCopy.Text = "COPY";
        _voiceCopy.Click += (_, _) => CopyVoiceCode();

        _voiceCall.Font = _stencilFont;
        _voiceCall.Text = "CALL…";
        _voiceCall.Click += (_, _) => OnVoiceCallKey();

        _voiceMute.Font = _stencilFont;
        _voiceMute.Text = "MIC";
        _voiceMute.ShowLamp = true;
        _voiceMute.LampColor = PanelTheme.LampGreen;
        _voiceMute.Click += (_, _) => ToggleVoiceMute();

        _voiceSend.Caption = "SEND";
        _voiceSend.CaptionFont = _stencilFont;
        _voiceReceive.Caption = "RECV";
        _voiceReceive.CaptionFont = _stencilFont;

        _voiceTimer.Interval = 66; // ~15 Hz: enough for a talk indicator, a third of the meters' cost
        _voiceTimer.Tick += OnVoiceTick;

        Controls.AddRange(new Control[]
        {
            _voiceToggle, _voiceCode, _voiceCopy, _voiceCall, _voiceMute,
            _voiceSend, _voiceReceive
        });
    }

    /// <summary>Height the voice plate needs, or zero when the section is collapsed.</summary>
    private int VoicePlateHeight()
    {
        if (!VoiceExpanded) return 0;
        return Scaled(VoicePadding) * 2 + Scaled(VoiceRowHeight) * 2 + Scaled(VoiceRowGap);
    }

    /// <summary>
    /// Places the voice section. Returns the y the footer should start from, so the caller
    /// does not need to know whether this section is showing.
    /// </summary>
    private int LayoutVoice(int top, int width, int margin)
    {
        int headerHeight = Scaled(26);
        int plateHeight = VoicePlateHeight();

        _voiceToggle.Bounds = new Rectangle(margin, top, Scaled(112), headerHeight);
        _voiceHeader = new Rectangle(_voiceToggle.Right + Scaled(12), top,
                                     width - margin - _voiceToggle.Right - Scaled(12), headerHeight);

        int plateTop = top + headerHeight + Scaled(6);
        _voicePlate = new Rectangle(margin, plateTop, width - margin * 2, plateHeight);

        if (plateHeight == 0)
        {
            SetVoiceRowVisible(false);
            return plateTop;
        }

        SetVoiceRowVisible(true);

        int padding = Scaled(VoicePadding);
        int rowHeight = Scaled(VoiceRowHeight);
        int gap = Scaled(10);

        int left = _voicePlate.Left + padding;
        int right = _voicePlate.Right - padding;
        int rowTop = _voicePlate.Top + padding;

        // Row one: your own code, with the keys that act on it.
        int copyWidth = Scaled(78);
        int callWidth = Scaled(104);

        _voiceCopy.Bounds = new Rectangle(right - callWidth - gap - copyWidth, rowTop, copyWidth, rowHeight);
        _voiceCall.Bounds = new Rectangle(right - callWidth, rowTop, callWidth, rowHeight);
        _voiceCode.Bounds = new Rectangle(left, rowTop, _voiceCopy.Left - gap - left, rowHeight);

        // Row two: the microphone cut and the two meters.
        rowTop += rowHeight + Scaled(VoiceRowGap);

        int muteWidth = Scaled(78);
        _voiceMute.Bounds = new Rectangle(left, rowTop, muteWidth, rowHeight);

        int meterLeft = _voiceMute.Right + gap;
        int meterWidth = (right - meterLeft - gap) / 2;
        _voiceSend.Bounds = new Rectangle(meterLeft, rowTop, meterWidth, rowHeight);
        _voiceReceive.Bounds = new Rectangle(meterLeft + meterWidth + gap, rowTop, meterWidth, rowHeight);

        return _voicePlate.Bottom;
    }

    private void SetVoiceRowVisible(bool visible)
    {
        _voiceCode.Visible = visible;
        _voiceCopy.Visible = visible;
        _voiceCall.Visible = visible;
        _voiceMute.Visible = visible;
        _voiceSend.Visible = visible;
        _voiceReceive.Visible = visible;
    }

    /// <summary>Draws the voice section's engraved parts into the cached chassis bitmap.</summary>
    private void DrawVoiceChassis(Graphics g, float radius)
    {
        if (_voicePlate.Height > 0) PanelTheme.DrawRecess(g, _voicePlate, radius);

        if (_voiceHeader.Width > 0)
        {
            TextRenderer.DrawText(g, VoiceExpanded ? "DIRECT VOICE LINK" : "TALK TO ONE PERSON, DIRECT",
                _stencilFont, _voiceHeader, Color.FromArgb(120, PanelTheme.TextSecondary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private void ApplyVoiceFonts()
    {
        _voiceToggle.Font = _stencilFont;
        _voiceCode.Font = _labelFont;
        _voiceCopy.Font = _stencilFont;
        _voiceCall.Font = _stencilFont;
        _voiceMute.Font = _stencilFont;
        _voiceSend.CaptionFont = _stencilFont;
        _voiceReceive.CaptionFont = _stencilFont;
    }

    // ----------------------------------------------------------------- state

    private void ToggleVoice()
    {
        _settings.VoiceEnabled = !_settings.VoiceEnabled;
        _settings.Save();

        _voiceToggle.IsOn = _settings.VoiceEnabled;
        _voiceToggle.Text = VoiceToggleCaption;

        if (_settings.VoiceEnabled) StartVoice();
        else StopVoice();

        LayoutPanel();
        ClampToScreen();
    }

    /// <summary>
    /// Opens the voice socket. Called at startup when the section is switched on, not when the
    /// window opens: being reachable is the point, and the window is usually in the tray.
    /// </summary>
    private void StartVoice()
    {
        if (!_settings.VoiceEnabled) return;

        if (_voice is null)
        {
            _voice = new VoiceSession(_audio);

            if (!_voice.Start(_settings.SafeVoicePort, null, out string? error))
            {
                _voice.Dispose();
                _voice = null;
                SetStatus("VOICE PORT UNAVAILABLE — " + (error ?? "unknown").ToUpperInvariant(),
                          PanelTheme.LampAmber);
                return;
            }
        }

        _voiceCodeShown = null;
        _voiceStateShown = VoiceLinkState.Idle;
        UpdateVoiceUi();
        if (Visible) _voiceTimer.Start();
    }

    /// <summary>
    /// Brings the voice section in line with settings that may just have changed. A changed
    /// port needs a new socket, which also re-runs the address discovery for a fresh code.
    /// </summary>
    private void SyncVoiceWithSettings()
    {
        _voiceToggle.IsOn = _settings.VoiceEnabled;
        _voiceToggle.Text = VoiceToggleCaption;

        if (!_settings.VoiceEnabled)
        {
            StopVoice();
            return;
        }

        if (_voice is not null && _voice.LocalPort != _settings.SafeVoicePort) StopVoice();
        StartVoice();
    }

    private void StopVoice()
    {
        _voiceTimer.Stop();
        _voice?.Dispose();
        _voice = null;
        _voiceSend.Reset();
        _voiceReceive.Reset();
        _voiceCodeShown = null;
        _voiceStateShown = VoiceLinkState.Idle;
        UpdateVoiceUi();
    }

    /// <summary>Starts or stops the voice strip's repaint timer with the window's visibility.</summary>
    private void SetVoiceTimerRunning(bool running)
    {
        if (running)
        {
            if (_voice is not null && !_voiceTimer.Enabled) _voiceTimer.Start();
        }
        else if (_voiceTimer.Enabled)
        {
            _voiceTimer.Stop();
            _voiceSend.Reset();
            _voiceReceive.Reset();
        }
    }

    private void OnVoiceTick(object? sender, EventArgs e)
    {
        var voice = _voice;
        if (voice is null || !Visible)
        {
            SetVoiceTimerRunning(false);
            return;
        }

        if (_voiceSend.Feed(voice.SendPeak)) _voiceSend.Invalidate();
        if (_voiceReceive.Feed(voice.ReceivePeak)) _voiceReceive.Invalidate();

        if (voice.State != _voiceStateShown || voice.LinkCode != _voiceCodeShown)
            UpdateVoiceUi();
    }

    /// <summary>
    /// Brings the strip's keys and labels in line with the link. Also announces a state change
    /// in the footer, which is where the user is already looking for what CommPanel just did.
    /// </summary>
    private void UpdateVoiceUi()
    {
        var voice = _voice;
        var state = voice?.State ?? VoiceLinkState.Idle;
        bool announce = voice is not null && state != _voiceStateShown;

        _voiceStateShown = state;
        _voiceCodeShown = voice?.LinkCode;

        string code = voice?.LinkCode ?? (voice is null ? "— — — — —" : "FINDING…");
        _voiceCode.Text = "MY CODE   " + code;
        _voiceCode.IsLit = voice?.LinkCode is not null;
        _voiceCode.Invalidate();

        _voiceCopy.Enabled = voice?.LinkCode is not null;

        bool inCall = state is VoiceLinkState.Calling or VoiceLinkState.Connected;
        _voiceCall.Text = inCall ? "HANG UP" : "CALL…";
        _voiceCall.ShowLamp = inCall;
        _voiceCall.IsOn = state == VoiceLinkState.Connected;
        _voiceCall.LampColor = state == VoiceLinkState.Connected
            ? PanelTheme.LampGreen
            : PanelTheme.LampAmber;
        _voiceCall.Enabled = voice is not null;
        _voiceCall.Invalidate();

        // Lit means the microphone is live, matching the device keys: lamp on is working.
        _voiceMute.IsOn = inCall && voice?.Muted == false;
        _voiceMute.Text = voice?.Muted == true ? "MUTED" : "MIC";
        _voiceMute.Enabled = inCall;
        _voiceMute.Invalidate();

        _voiceSend.IsInactive = !inCall || voice?.Muted == true;
        _voiceReceive.IsInactive = state != VoiceLinkState.Connected;
        _voiceSend.Invalidate();
        _voiceReceive.Invalidate();

        if (!announce) return;

        switch (state)
        {
            case VoiceLinkState.Calling:
                SetStatus("CALLING — THE OTHER END MUST DIAL YOUR CODE TOO", PanelTheme.LampAmber);
                break;
            case VoiceLinkState.Connected:
                SetStatus("VOICE CONNECTED", PanelTheme.LampGreen);
                break;
            case VoiceLinkState.Idle:
                SetStatus("VOICE CALL ENDED", PanelTheme.TextSecondary);
                break;
        }
    }

    // --------------------------------------------------------------- actions

    private void CopyVoiceCode()
    {
        string? code = _voice?.LinkCode;
        if (code is null) return;

        try
        {
            Clipboard.SetText(code);
            SetStatus("CODE " + code + " COPIED — READ IT OUT OR PASTE IT TO THEM",
                      PanelTheme.LampGreen);
        }
        catch
        {
            // The clipboard can be held by another process; the code is on screen regardless.
            SetStatus("CLIPBOARD BUSY — THE CODE IS " + code, PanelTheme.LampAmber);
        }
    }

    private void OnVoiceCallKey()
    {
        var voice = _voice;
        if (voice is null) return;

        if (voice.State is VoiceLinkState.Calling or VoiceLinkState.Connected)
        {
            voice.HangUp();
            UpdateVoiceUi();
            return;
        }

        bool wasTopMost = TopMost;
        TopMost = false;

        using var dialog = new VoiceCallDialog(_settings, () => _voice?.LinkCode, _settings.LastVoicePeer);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Code is not null)
        {
            _settings.LastVoicePeer = dialog.Code;
            _settings.Save();

            if (voice.Call(dialog.Code, out string? error))
            {
                SetStatus("CALLING " + dialog.Code, PanelTheme.LampAmber);
            }
            else
            {
                SetStatus("CALL FAILED — " + (error ?? "unknown").ToUpperInvariant(),
                          PanelTheme.LampAmber);
            }

            UpdateVoiceUi();
        }

        TopMost = wasTopMost;
    }

    private void ToggleVoiceMute()
    {
        var voice = _voice;
        if (voice is null || !voice.IsInCall) return;

        voice.Muted = !voice.Muted;
        UpdateVoiceUi();
        SetStatus(voice.Muted ? "MICROPHONE MUTED" : "MICROPHONE LIVE",
                  voice.Muted ? PanelTheme.LampAmber : PanelTheme.LampGreen);
    }

    private void DisposeVoice()
    {
        _voiceTimer.Stop();
        _voiceTimer.Dispose();
        _voice?.Dispose();
        _voice = null;
    }
}
