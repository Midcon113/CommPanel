using System.Runtime.InteropServices;
using CommPanel.Audio;

namespace CommPanel.Voice;

/// <summary>
/// Plays the far end's voice through an output device.
///
/// The render buffer must be kept fed whatever happens: if the network is late, silence goes
/// in. Leaving the buffer short does not pause playback, it produces a glitch, so an underrun
/// has to be filled rather than skipped.
/// </summary>
internal sealed class VoiceRender : IDisposable
{
    private const int ShareModeShared = 0;
    private const int ClsCtxAll = 23;

    /// <summary>100 ms of device buffer, kept roughly half full.</summary>
    private const long BufferDuration = 1_000_000;

    private readonly Func<short[]?> _frameSource;
    private readonly List<float> _pending = new();

    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private VoiceResampler? _resampler;
    private float[] _scratch = Array.Empty<float>();
    private int _channels;
    private bool _isFloat;
    private uint _bufferFrames;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;

    private VoiceRender(string deviceId, Func<short[]?> frameSource)
    {
        DeviceId = deviceId;
        _frameSource = frameSource;
    }

    public string DeviceId { get; }

    /// <summary>Loudest sample played since the last read, for the panel's receive meter.</summary>
    public float Peak { get; private set; }

    private float _healthPeak;

    /// <summary>
    /// Peak for the health monitor, kept separate from the meter's so the two do not
    /// consume each other's readings.
    /// </summary>
    public float TakeHealthPeak()
    {
        float peak = _healthPeak;
        _healthPeak = 0f;
        return peak;
    }

    public static VoiceRender? Open(IMMDeviceEnumerator enumerator, string deviceId,
                                    Func<short[]?> frameSource, out string? error)
    {
        error = null;

        if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null)
        {
            error = "device unavailable";
            return null;
        }

        var render = new VoiceRender(deviceId, frameSource);
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

            render._client = client;
            VoiceDevice.MarkAsCommunications(client);

            if (client.GetMixFormat(out format) != 0 || format == IntPtr.Zero)
            {
                error = "no mix format";
                return null;
            }

            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(format);
            render._channels = Math.Max(1, (int)waveFormat.Channels);
            render._isFloat = waveFormat.BitsPerSample == 32;

            if (waveFormat.BitsPerSample is not (16 or 32))
            {
                error = "unsupported sample format";
                return null;
            }

            render._resampler = new VoiceResampler(VoiceFormat.SampleRate, (int)waveFormat.SamplesPerSecond);

            int hr = client.Initialize(ShareModeShared, 0, BufferDuration, 0, format, IntPtr.Zero);
            if (hr != 0)
            {
                error = VoiceDevice.Describe(hr, "speaker");
                return null;
            }

            if (client.GetBufferSize(out render._bufferFrames) != 0 || render._bufferFrames == 0)
            {
                error = "no render buffer";
                return null;
            }

            var renderIid = typeof(IAudioRenderClient).GUID;
            if (client.GetService(ref renderIid, out object? renderObject) != 0 ||
                renderObject is not IAudioRenderClient renderClient)
            {
                error = "no render client";
                return null;
            }

            render._render = renderClient;

            if (client.Start() != 0)
            {
                error = "could not start playback";
                return null;
            }

            render._thread = new Thread(render.Run)
            {
                IsBackground = true,
                Name = "CommPanel voice render",
                Priority = ThreadPriority.AboveNormal
            };
            render._thread.Start();

            var opened = render;
            render = null!;
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
            render?.Dispose();
        }
    }

    private void Run()
    {
        const int pollMilliseconds = VoiceFormat.FrameMilliseconds / 2;

        while (!_stopping)
        {
            try
            {
                Fill();
            }
            catch
            {
                return; // device went away; the session will reopen it
            }

            Thread.Sleep(pollMilliseconds);
        }
    }

    private void Fill()
    {
        var client = _client;
        var render = _render;
        var resampler = _resampler;
        if (client is null || render is null || resampler is null) return;

        if (client.GetCurrentPadding(out uint padding) != 0) return;

        uint free = _bufferFrames > padding ? _bufferFrames - padding : 0;
        if (free == 0) return;

        EnsurePending((int)free, resampler);

        if (render.GetBuffer(free, out IntPtr buffer) != 0 || buffer == IntPtr.Zero) return;

        float peak = Peak;
        int needed = (int)free * _channels;

        if (_scratch.Length < needed) _scratch = new float[needed];

        for (int frame = 0; frame < free; frame++)
        {
            float sample = frame < _pending.Count ? _pending[frame] : 0f;
            float magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;

            int offset = frame * _channels;
            for (int c = 0; c < _channels; c++) _scratch[offset + c] = sample;
        }

        _pending.RemoveRange(0, Math.Min((int)free, _pending.Count));
        Peak = peak;
        if (peak > _healthPeak) _healthPeak = peak;

        if (_isFloat)
        {
            Marshal.Copy(_scratch, 0, buffer, needed);
        }
        else
        {
            var pcm = new short[needed];
            for (int i = 0; i < needed; i++)
                pcm[i] = (short)(Math.Clamp(_scratch[i], -1f, 1f) * short.MaxValue);
            Marshal.Copy(pcm, 0, buffer, needed);
        }

        render.ReleaseBuffer(free, 0);
    }

    /// <summary>
    /// Pulls frames from the link until there is enough audio to fill the request, padding
    /// with silence when the network has nothing ready.
    /// </summary>
    private void EnsurePending(int needed, VoiceResampler resampler)
    {
        int guard = 0;

        while (_pending.Count < needed && guard++ < 16)
        {
            var frame = _frameSource();
            if (frame is null) break;

            foreach (short sample in frame) resampler.Push(sample / 32768f);
            resampler.Drain(_pending);
        }

        while (_pending.Count < needed) _pending.Add(0f);
    }

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

        VoiceDevice.Release(_render);
        VoiceDevice.Release(_client);
        _render = null;
        _client = null;
        _thread = null;
    }
}
