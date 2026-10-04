using System.Net;
using CommPanel.Audio;

namespace CommPanel.Voice;

/// <summary>
/// One voice call: the microphone, the link, and the speaker wired together.
///
/// Both ends deliberately use the **Communications** endpoints rather than the default ones.
/// That is the role Windows reserves for talking to a person, it is the one CommPanel already
/// exposes with the blue COMMS lamp, and it means a call can land in the headset while a game
/// carries on through the speakers.
///
/// The microphone is opened when a call starts and closed when it ends, never merely because
/// voice is switched on. Holding it open to wait for a call would light the Windows microphone
/// indicator for as long as CommPanel was running, which is not a thing to do quietly to
/// somebody - and the link cannot connect without this end dialling anyway.
/// </summary>
internal sealed class VoiceSession : IDisposable, IVoiceRepair
{
    private readonly AudioEndpointService _audio;
    private readonly object _gate = new();

    private readonly VoiceHealth _health;

    private System.Threading.Timer? _monitor;
    private long _lastCaptureFrames;

    /// <summary>
    /// Held open while a call runs so the microphone's Windows mute state can be read each
    /// second without activating the device every time.
    /// </summary>
    private EndpointControls? _captureControls;

    /// <summary>
    /// Set when the call has been moved off the Communications endpoint because nothing was
    /// coming out of it. Held so that following device changes does not quietly move it back
    /// to the device that was not working.
    /// </summary>
    private string? _renderOverrideId;

    private VoiceLink? _link;
    private VoiceCapture? _capture;
    private VoiceRender? _render;
    private bool _muted;
    private bool _disposed;

    public VoiceSession(AudioEndpointService audio)
    {
        _audio = audio;
        _health = new VoiceHealth(this);
    }

    /// <summary>What the health monitor makes of the call.</summary>
    public VoiceHealthCode HealthCode => _health.Code;

    /// <summary>One line about the call's health, for the panel.</summary>
    public string HealthMessage => _health.Message;

    public VoiceLinkState State => _link?.State ?? VoiceLinkState.Idle;

    /// <summary>True once the socket is open and we are reachable.</summary>
    public bool IsOpen => _link is not null;

    /// <summary>True while the audio devices are held, which is exactly during a call.</summary>
    public bool IsInCall => _capture is not null || _render is not null;

    /// <summary>Our own link code once STUN has answered, for reading out to the other person.</summary>
    public string? LinkCode { get; private set; }

    public int LocalPort => _link?.LocalPort ?? 0;

    public string? LastError { get; private set; }

    /// <summary>Microphone level going out, 0 to 1.</summary>
    public float SendPeak => _capture?.TakePeak() ?? 0f;

    /// <summary>Far end's level coming in, 0 to 1.</summary>
    public float ReceivePeak => _render?.TakePeak() ?? 0f;

    /// <summary>
    /// Kept here rather than read back from the capture stream, so that muting survives the
    /// microphone being reopened when the device changes mid-call.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            lock (_gate) { if (_capture is not null) _capture.Muted = value; }
        }
    }

    public int BufferedFrames => _link?.BufferedFrames ?? 0;
    public int PacketsSent => _link?.PacketsSent ?? 0;
    public int PacketsReceived => _link?.PacketsReceived ?? 0;

    /// <summary>Raised on a background thread whenever the call's state changes.</summary>
    public event Action<VoiceLinkState>? StateChanged;

    /// <summary>Raised on a background thread once our own link code is known.</summary>
    public event Action<string>? LinkCodeReady;

    /// <summary>
    /// Opens the socket and starts discovering our public address, so a code can be read out.
    /// Touches no audio device and calls nobody.
    /// </summary>
    public bool Start(int listenPort, string? linkCode, out string? error)
    {
        error = null;

        lock (_gate)
        {
            if (_disposed) { error = "voice has been shut down"; return false; }
            if (_link is not null) return true;

            try
            {
                var link = new VoiceLink(listenPort, linkCode);
                link.StateChanged += state => StateChanged?.Invoke(state);
                link.PublicEndPointDiscovered += endPoint =>
                {
                    LinkCode = VoiceAddress.ToCode(endPoint);
                    LinkCodeReady?.Invoke(LinkCode);
                };

                _link = link;
                LastError = null;

                link.DiscoverPublicEndPoint();
                return true;
            }
            catch (Exception ex)
            {
                // Almost always the port already being in use by another copy of CommPanel.
                error = ex.Message;
                LastError = error;
                return false;
            }
        }
    }

    /// <summary>
    /// Dials a peer, opening the microphone and speaker for the call. The other end must dial
    /// back for a direct link to form.
    /// </summary>
    public bool Call(string codeOrAddress, out string? error)
    {
        error = null;

        if (!VoiceAddress.TryParse(codeOrAddress, out IPEndPoint peer))
        {
            error = "that is not a link code or address";
            return false;
        }

        lock (_gate)
        {
            if (_link is null) { error = "voice is not started"; return false; }

            if (!OpenDevices(out error))
            {
                LastError = error;
                return false;
            }

            _link.Call(peer);
            LastError = null;

            // One reading a second: enough to catch a dead audio path within a sentence or
            // two, cheap enough to leave running for a whole call.
            _lastCaptureFrames = 0;
            _monitor = new System.Threading.Timer(_ => Monitor(), null, 1000, 1000);
            return true;
        }
    }

    /// <summary>Ends the call and releases the microphone and speaker, keeping the socket open.</summary>
    public void HangUp()
    {
        StopMonitor();

        lock (_gate)
        {
            _link?.HangUp();
            CloseDevices();
            _renderOverrideId = null;
        }
    }

    /// <summary>Closes everything, including the socket.</summary>
    public void Stop()
    {
        StopMonitor();

        lock (_gate)
        {
            CloseDevices();
            _renderOverrideId = null;
            _link?.Dispose();
            _link = null;
            LinkCode = null;
        }
    }

    /// <summary>
    /// Reopens the audio devices if the Communications endpoints have changed underneath us,
    /// so switching headset mid-call moves the call too rather than stranding it. Does nothing
    /// when there is no call in progress, since there is then nothing open to move.
    /// </summary>
    public void FollowDeviceChanges()
    {
        lock (_gate)
        {
            if (_link is null || !IsInCall) return;

            string? inputId = _audio.GetDefaultId(EDataFlow.Capture, ERole.Communications);
            string? outputId = _renderOverrideId
                               ?? _audio.GetDefaultId(EDataFlow.Render, ERole.Communications);

            if (inputId is not null && _capture is not null &&
                !string.Equals(inputId, _capture.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _capture.Dispose();
                _capture = _audio.OpenVoiceCapture(inputId, _link.SendVoice, out _);
                if (_capture is not null) _capture.Muted = _muted;
            }

            if (outputId is not null && _render is not null &&
                !string.Equals(outputId, _render.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _render.Dispose();
                _render = _audio.OpenVoiceRender(outputId, _link.TakeVoice, out _);
            }
        }
    }

    /// <summary>Opens the microphone and speaker. Called with <see cref="_gate"/> held.</summary>
    private bool OpenDevices(out string? error)
    {
        error = null;
        if (_link is null) return false;
        if (IsInCall) return true;

        string? inputId = _audio.GetDefaultId(EDataFlow.Capture, ERole.Communications);
        string? outputId = _renderOverrideId
                           ?? _audio.GetDefaultId(EDataFlow.Render, ERole.Communications);

        if (inputId is null) { error = "no microphone"; return false; }
        if (outputId is null) { error = "no playback device"; return false; }

        var capture = _audio.OpenVoiceCapture(inputId, _link.SendVoice, out string? captureError);
        if (capture is null)
        {
            error = captureError ?? "could not open the microphone";
            return false;
        }
        capture.Muted = _muted;

        var render = _audio.OpenVoiceRender(outputId, _link.TakeVoice, out string? renderError);
        if (render is null)
        {
            capture.Dispose();
            error = renderError ?? "could not open playback";
            return false;
        }

        _capture = capture;
        _render = render;
        return true;
    }

    /// <summary>Releases the microphone and speaker. Called with <see cref="_gate"/> held.</summary>
    private void CloseDevices()
    {
        _captureControls?.Dispose();
        _captureControls = null;
        _capture?.Dispose();
        _render?.Dispose();
        _capture = null;
        _render = null;
    }

    // ---------------------------------------------------------------- health

    private void StopMonitor()
    {
        var monitor = Interlocked.Exchange(ref _monitor, null);
        monitor?.Dispose();
    }

    /// <summary>
    /// Takes a reading of both ends once a second, lets the monitor judge it, and puts this
    /// end's own verdict on the wire for the other end to read.
    /// </summary>
    private void Monitor()
    {
        try
        {
            VoiceVitals vitals;

            lock (_gate)
            {
                var link = _link;
                if (link is null) return;

                bool inputMuted = ReadInputMuted();

                long frames = _capture?.FramesDelivered ?? 0;
                long delta = frames - _lastCaptureFrames;
                _lastCaptureFrames = frames;

                vitals = new VoiceVitals
                {
                    Connected = link.State == VoiceLinkState.Connected,
                    Muted = _muted,
                    CaptureFrames = delta,
                    CapturePeak = _capture?.TakeHealthPeak() ?? 0f,
                    IncomingPeak = link.TakeIncomingPeak(),
                    StarvedReads = link.TakeStarvedReads(),
                    RenderPeak = _render?.TakeHealthPeak() ?? 0f,
                    HasCapture = _capture is not null,
                    HasRender = _render is not null,
                    InputMutedInWindows = inputMuted,
                    Peer = _health.FreshPeer(link.PeerReport, link.PeerReportAt)
                };
            }

            var before = _health.Code;
            _health.Evaluate(vitals);

            lock (_gate)
            {
                if (_link is not null) _link.LocalReport = _health.BuildReport(vitals);
            }

            if (_health.Code != before) HealthChanged?.Invoke(_health.Code, _health.Message);
        }
        catch
        {
            // A monitor that throws must not take the call down with it.
        }
    }

    /// <summary>
    /// Whether Windows itself has the microphone muted or turned all the way down. Read from
    /// the endpoint rather than guessed from a silent meter, because a quiet room and a dead
    /// microphone look exactly the same on a meter.
    /// </summary>
    private bool ReadInputMuted()
    {
        string? inputId = _capture?.DeviceId;
        if (inputId is null) return false;

        if (_captureControls is not null &&
            !string.Equals(_captureControls.DeviceId, inputId, StringComparison.OrdinalIgnoreCase))
        {
            _captureControls.Dispose();
            _captureControls = null;
        }

        _captureControls ??= _audio.OpenControls(inputId);
        if (_captureControls is null || !_captureControls.HasVolume) return false;

        return (_captureControls.ReadMute() ?? false) || (_captureControls.ReadVolume() ?? 1f) < 0.02f;
    }

    /// <summary>Raised on a timer thread when the verdict about the call changes.</summary>
    public event Action<VoiceHealthCode, string>? HealthChanged;

    // ---- repairs --------------------------------------------------------
    // Each returns true when it actually changed something, and says what in plain words.
    // All of them are things CommPanel has the standing to change: its own mixer entry, its
    // own streams, and the endpoint its own call is using.

    RepairOutcome IVoiceRepair.RestoreOwnVolume(out string? what)
    {
        what = null;

        string? outputId = CurrentRenderId();
        if (outputId is null) return RepairOutcome.NothingToDo;

        using var mixer = _audio.OpenSessionMixer(outputId);
        if (mixer is null) return RepairOutcome.NothingToDo;

        uint us = (uint)Environment.ProcessId;

        foreach (var session in mixer.Refresh())
        {
            if (session.ProcessId != us) continue;

            bool muted = session.ReadMute();
            float level = session.ReadVolume() ?? 1f;

            if (!muted && level >= 0.05f) continue;

            if (muted) session.WriteMute(false);
            if (level < 0.05f) session.WriteVolume(1f);

            what = "COMMPANEL WAS MUTED IN THE WINDOWS MIXER — TURNED BACK UP";
            return RepairOutcome.Fixed;
        }

        return RepairOutcome.NothingToDo;
    }

    RepairOutcome IVoiceRepair.ReportOutputMuted(out string? what) =>
        ReportMuted(CurrentRenderId(), "PLAYBACK DEVICE", "TO HEAR THEM", out what);

    RepairOutcome IVoiceRepair.ReportInputMuted(out string? what) =>
        ReportMuted(_capture?.DeviceId, "MICROPHONE", "TO BE HEARD", out what);

    /// <summary>
    /// Says whether Windows has a device muted or turned down, and changes nothing.
    ///
    /// CommPanel deliberately does not undo this. A muted device is a setting the person
    /// made - a microphone most of all, which is often muted with a button on the headset -
    /// and a program that quietly unmutes it would start playing, or sending, audio somebody
    /// believed was off.
    /// </summary>
    private RepairOutcome ReportMuted(string? deviceId, string label, string why, out string? what)
    {
        what = null;
        if (deviceId is null) return RepairOutcome.NothingToDo;

        using var controls = _audio.OpenControls(deviceId);
        if (controls is null || !controls.HasVolume) return RepairOutcome.NothingToDo;

        bool muted = controls.ReadMute() ?? false;
        float level = controls.ReadVolume() ?? 1f;

        if (!muted && level >= 0.05f) return RepairOutcome.NothingToDo;

        what = muted
            ? "WINDOWS HAS YOUR " + label + " MUTED — UNMUTE IT " + why
            : "YOUR " + label + " IS TURNED ALL THE WAY DOWN — TURN IT UP " + why;

        return RepairOutcome.NeedsYou;
    }

    /// <summary>
    /// The call is playing into the Communications endpoint, which is not always the device
    /// anybody is wearing. If the ordinary default is a different device, move the call there:
    /// that changes nothing in Windows, only where this one stream goes.
    /// </summary>
    RepairOutcome IVoiceRepair.MoveCallToDefaultOutput(out string? what)
    {
        what = null;

        string? current = CurrentRenderId();
        string? console = _audio.GetDefaultId(EDataFlow.Render, ERole.Console);

        if (console is null || current is null) return RepairOutcome.NothingToDo;
        if (string.Equals(current, console, StringComparison.OrdinalIgnoreCase)) return RepairOutcome.NothingToDo;

        lock (_gate)
        {
            if (_link is null) return RepairOutcome.NothingToDo;

            _render?.Dispose();
            _render = _audio.OpenVoiceRender(console, _link.TakeVoice, out string? moveError);
            if (_render is null)
            {
                Core.Log.Write("repair", "could not move the call to the default output: " + (moveError ?? "unknown"));
                return RepairOutcome.NothingToDo;
            }

            _renderOverrideId = console;
        }

        what = "MOVED THE CALL TO " + (NameOf(console, EDataFlow.Render) ?? "YOUR DEFAULT SPEAKERS").ToUpperInvariant();
        return RepairOutcome.Fixed;
    }

    RepairOutcome IVoiceRepair.ReopenRender(out string? what)
    {
        what = null;

        lock (_gate)
        {
            if (_link is null) return RepairOutcome.NothingToDo;

            string? outputId = CurrentRenderId();
            if (outputId is null) return RepairOutcome.NothingToDo;

            _render?.Dispose();
            _render = _audio.OpenVoiceRender(outputId, _link.TakeVoice, out string? renderError);
            if (_render is null) Core.Log.Write("repair", "reopening playback failed: " + (renderError ?? "unknown"));
        }

        what = "REOPENED PLAYBACK";
        return RepairOutcome.Fixed;
    }

    RepairOutcome IVoiceRepair.ReopenCapture(out string? what)
    {
        what = null;

        lock (_gate)
        {
            if (_link is null) return RepairOutcome.NothingToDo;

            string? inputId = _capture?.DeviceId
                              ?? _audio.GetDefaultId(EDataFlow.Capture, ERole.Communications);
            if (inputId is null) return RepairOutcome.NothingToDo;

            _capture?.Dispose();
            _capture = _audio.OpenVoiceCapture(inputId, _link.SendVoice, out string? captureError);
            if (_capture is null) Core.Log.Write("repair", "reopening the microphone failed: " + (captureError ?? "unknown"));
            else _capture.Muted = _muted;
            _lastCaptureFrames = 0;
        }

        what = "REOPENED THE MICROPHONE";
        return RepairOutcome.Fixed;
    }

    private string? CurrentRenderId() =>
        _render?.DeviceId
        ?? _renderOverrideId
        ?? _audio.GetDefaultId(EDataFlow.Render, ERole.Communications);

    private string? NameOf(string deviceId, EDataFlow flow) =>
        _audio.GetDevices(flow)
              .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
              ?.ShortName;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
