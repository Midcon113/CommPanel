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
internal sealed class VoiceSession : IDisposable
{
    private readonly AudioEndpointService _audio;
    private readonly object _gate = new();

    private VoiceLink? _link;
    private VoiceCapture? _capture;
    private VoiceRender? _render;
    private bool _muted;
    private bool _disposed;

    public VoiceSession(AudioEndpointService audio) => _audio = audio;

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
            return true;
        }
    }

    /// <summary>Ends the call and releases the microphone and speaker, keeping the socket open.</summary>
    public void HangUp()
    {
        lock (_gate)
        {
            _link?.HangUp();
            CloseDevices();
        }
    }

    /// <summary>Closes everything, including the socket.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            CloseDevices();
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
            string? outputId = _audio.GetDefaultId(EDataFlow.Render, ERole.Communications);

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
        string? outputId = _audio.GetDefaultId(EDataFlow.Render, ERole.Communications);

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
        _capture?.Dispose();
        _render?.Dispose();
        _capture = null;
        _render = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
