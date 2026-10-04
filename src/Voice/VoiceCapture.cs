using System.Runtime.InteropServices;
using CommPanel.Audio;

namespace CommPanel.Voice;

/// <summary>
/// Captures the microphone and hands out 20 ms frames at the voice rate.
///
/// Separate from <see cref="CaptureMeter"/> on purpose: that one is polled by the UI for a
/// peak level and may be read late without consequence, whereas this has to deliver every
/// frame on time or the far end hears gaps. It runs its own thread and declares itself a
/// communications stream so Windows can apply echo cancellation where the hardware allows.
/// </summary>
internal sealed class VoiceCapture : IDisposable
{
    private const int ShareModeShared = 0;
    private const int ClsCtxAll = 23;
    private const uint BufferFlagsSilent = 0x2;

    /// <summary>100 ms of capture buffer: ample headroom for a 20 ms cadence.</summary>
    private const long BufferDuration = 1_000_000;

    private readonly Action<short[]> _onFrame;
    private readonly List<float> _resampled = new();
    private readonly List<short> _frame = new(VoiceFormat.SamplesPerFrame);

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private VoiceResampler? _resampler;
    private float[] _scratch = Array.Empty<float>();
    private short[] _shortScratch = Array.Empty<short>();
    private int _channels;
    private bool _isFloat;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    private VoiceCapture(string deviceId, Action<short[]> onFrame)
    {
        DeviceId = deviceId;
        _onFrame = onFrame;
    }

    public string DeviceId { get; }

    /// <summary>Loudest sample seen since the last read, for the panel's send meter.</summary>
    public float Peak { get; private set; }

    /// <summary>Silences the outgoing stream without tearing the call down.</summary>
    public bool Muted { get; set; }

    public static VoiceCapture? Open(IMMDeviceEnumerator enumerator, string deviceId,
                                     Action<short[]> onFrame, out string? error)
    {
        error = null;

        if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null)
        {
            error = "device unavailable";
            return null;
        }

        var capture = new VoiceCapture(deviceId, onFrame);
        IntPtr format = IntPtr.Zero;

        try
        {
            var clientIid = typeof(IAudioClient).GUID;
            if (device.Activate(ref clientIid, ClsCtxAll, IntPtr.Zero, out object? clientObject) != 0 ||
                clientObject is not IAudioClient client)
            {
                error = "no audio client";
                return null;
            }

            capture._client = client;
            VoiceDevice.MarkAsCommunications(client);

            if (client.GetMixFormat(out format) != 0 || format == IntPtr.Zero)
            {
                error = "no mix format";
                return null;
            }

            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(format);
            capture._channels = Math.Max(1, (int)waveFormat.Channels);
            capture._isFloat = waveFormat.BitsPerSample == 32;

            if (waveFormat.BitsPerSample is not (16 or 32))
            {
                error = "unsupported sample format";
                return null;
            }

            capture._resampler = new VoiceResampler((int)waveFormat.SamplesPerSecond, VoiceFormat.SampleRate);

            int hr = client.Initialize(ShareModeShared, 0, BufferDuration, 0, format, IntPtr.Zero);
            if (hr != 0)
            {
                error = VoiceDevice.Describe(hr, "microphone");
                return null;
            }

            var captureIid = typeof(IAudioCaptureClient).GUID;
            if (client.GetService(ref captureIid, out object? captureObject) != 0 ||
                captureObject is not IAudioCaptureClient captureClient)
            {
                error = "no capture client";
                return null;
            }

            capture._capture = captureClient;

            if (client.Start() != 0)
            {
                error = "could not start capture";
                return null;
            }

            capture._thread = new Thread(capture.Run)
            {
                IsBackground = true,
                Name = "CommPanel voice capture",
                Priority = ThreadPriority.AboveNormal
            };
            capture._thread.Start();

            var opened = capture;
            capture = null!;
            return opened;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            capture?.Dispose();
        }
    }

    private void Run()
    {
        // Half a frame: often enough that a 20 ms packet is never late, cheap enough that the
        // thread is asleep almost all the time.
        const int pollMilliseconds = VoiceFormat.FrameMilliseconds / 2;

        while (!_stopping)
        {
            try
            {
                Drain();
            }
            catch
            {
                return; // device went away; the session will reopen it
            }

            Thread.Sleep(pollMilliseconds);
        }
    }

    private void Drain()
    {
        var capture = _capture;
        var resampler = _resampler;
        if (capture is null || resampler is null) return;

        float peak = Peak;

        while (!_stopping && capture.GetNextPacketSize(out uint available) == 0 && available > 0)
        {
            if (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) != 0)
                break;

            try
            {
                if (frames == 0 || data == IntPtr.Zero) continue;

                int samples = checked((int)frames * _channels);
                bool silent = (flags & BufferFlagsSilent) != 0;

                for (int frame = 0; frame < frames; frame++)
                {
                    float mono = 0f;

                    if (!silent)
                    {
                        mono = ReadMono(data, frame, samples);
                        float magnitude = Math.Abs(mono);
                        if (magnitude > peak) peak = magnitude;
                    }

                    resampler.Push(mono);
                }
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }

        Peak = peak;

        _resampled.Clear();
        resampler.Drain(_resampled);

        foreach (float sample in _resampled)
        {
            float clamped = Math.Clamp(Muted ? 0f : sample, -1f, 1f);
            _frame.Add((short)(clamped * short.MaxValue));

            if (_frame.Count < VoiceFormat.SamplesPerFrame) continue;

            var ready = _frame.ToArray();
            _frame.Clear();

            try { _onFrame(ready); }
            catch { /* a failing consumer must not kill the capture thread */ }
        }
    }

    /// <summary>Reads one frame's worth of channels and averages them to mono.</summary>
    private float ReadMono(IntPtr data, int frameIndex, int totalSamples)
    {
        if (_isFloat)
        {
            if (_scratch.Length < totalSamples)
            {
                _scratch = new float[totalSamples];
                Marshal.Copy(data, _scratch, 0, totalSamples);
            }
            else if (frameIndex == 0)
            {
                Marshal.Copy(data, _scratch, 0, totalSamples);
            }

            float sum = 0f;
            int offset = frameIndex * _channels;
            for (int c = 0; c < _channels; c++) sum += _scratch[offset + c];
            return sum / _channels;
        }

        if (_shortScratch.Length < totalSamples)
        {
            _shortScratch = new short[totalSamples];
            Marshal.Copy(data, _shortScratch, 0, totalSamples);
        }
        else if (frameIndex == 0)
        {
            Marshal.Copy(data, _shortScratch, 0, totalSamples);
        }

        int sumShort = 0;
        int shortOffset = frameIndex * _channels;
        for (int c = 0; c < _channels; c++) sumShort += _shortScratch[shortOffset + c];
        return sumShort / (float)_channels / 32768f;
    }

    /// <summary>Reads and clears the peak, so the meter decays rather than latching.</summary>
    public float TakePeak()
    {
        float peak = Peak;
        Peak = 0f;
        return peak;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;

        try { _thread?.Join(400); } catch { }

        try { _client?.Stop(); } catch { }

        VoiceDevice.Release(_capture);
        VoiceDevice.Release(_client);
        _capture = null;
        _client = null;
        _thread = null;
    }
}
