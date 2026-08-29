using System.Runtime.InteropServices;

namespace CommPanel.Audio;

/// <summary>
/// Measures the level of a microphone by opening a capture stream on it.
///
/// This is necessary rather than merely convenient: IAudioMeterInformation on a capture
/// endpoint reports nothing at all unless something is actively recording from the device.
/// Measured on real hardware - with no stream open the endpoint meter returned 0.00000 while
/// a signal was present, and 0.19873 for the same signal with a stream open. Windows' own
/// microphone bar behaves the same way; the Settings app opens a stream to make it move.
///
/// Audio is read only to compute a peak and is then discarded - nothing is recorded, stored
/// or transmitted. Opening the microphone does make Windows show its microphone-in-use
/// indicator, so the stream is held only while the panel is on screen and is closed the
/// moment it hides.
/// </summary>
internal sealed class CaptureMeter : IDisposable
{
    private const int ShareModeShared = 0;
    private const int ClsCtxAll = 23;
    private const uint BufferFlagsSilent = 0x2;

    /// <summary>200ms of buffer: ample for a 30 Hz meter, and small in memory.</summary>
    private const long BufferDuration = 2_000_000;

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private float[] _floatBuffer = Array.Empty<float>();
    private short[] _shortBuffer = Array.Empty<short>();
    private int _channels;
    private bool _isFloat;
    private bool _started;
    private bool _disposed;

    private CaptureMeter(string deviceId) => DeviceId = deviceId;

    public string DeviceId { get; }

    public static CaptureMeter? Open(IMMDeviceEnumerator enumerator, string deviceId, out string? error)
    {
        error = null;

        if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null)
        {
            error = "device unavailable";
            return null;
        }

        var meter = new CaptureMeter(deviceId);
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

            meter._client = client;

            if (client.GetMixFormat(out format) != 0 || format == IntPtr.Zero)
            {
                error = "no mix format";
                return null;
            }

            var waveFormat = Marshal.PtrToStructure<WaveFormatEx>(format);
            meter._channels = Math.Max(1, (int)waveFormat.Channels);

            // The shared-mode mix format is float32 in practice; 16-bit is handled as a
            // fallback so an unusual device still meters rather than reading flat zero.
            meter._isFloat = waveFormat.BitsPerSample == 32;
            if (waveFormat.BitsPerSample is not (16 or 32))
            {
                error = "unsupported sample format";
                return null;
            }

            int hr = client.Initialize(ShareModeShared, 0, BufferDuration, 0, format, IntPtr.Zero);
            if (hr != 0)
            {
                error = DescribeInitialiseFailure(hr);
                return null;
            }

            var captureIid = typeof(IAudioCaptureClient).GUID;
            if (client.GetService(ref captureIid, out object? captureObject) != 0 ||
                captureObject is not IAudioCaptureClient capture)
            {
                error = "no capture client";
                return null;
            }

            meter._capture = capture;

            if (client.Start() != 0)
            {
                error = "could not start capture";
                return null;
            }

            meter._started = true;
            var opened = meter;
            meter = null!; // ownership transferred; skip the cleanup below
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
            meter?.Dispose();
        }
    }

    private static string DescribeInitialiseFailure(int hr) => (uint)hr switch
    {
        0x80070005 => "microphone access blocked by Windows privacy settings",
        0x88890004 => "device was disconnected",
        0x8889000A => "device already in exclusive use",
        _ => string.Format("cannot open microphone (0x{0:X8})", hr)
    };

    /// <summary>
    /// Drains everything captured since the previous call and returns the loudest sample,
    /// 0.0 to 1.0. Draining matters as much as measuring - an unread capture buffer simply
    /// fills up and stops being useful.
    /// </summary>
    public float ReadPeak()
    {
        var capture = _capture;
        if (capture is null || !_started) return 0f;

        float max = 0f;

        try
        {
            while (capture.GetNextPacketSize(out uint available) == 0 && available > 0)
            {
                if (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) != 0)
                    break;

                try
                {
                    if (frames > 0 && data != IntPtr.Zero && (flags & BufferFlagsSilent) == 0)
                    {
                        int samples = checked((int)frames * _channels);
                        max = Math.Max(max, Scan(data, samples));
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }
        catch
        {
            // The device can vanish mid-read; treat it as silence and let the panel's
            // device refresh rebuild the meter.
            return 0f;
        }

        return Math.Clamp(max, 0f, 1f);
    }

    private float Scan(IntPtr data, int samples)
    {
        float max = 0f;

        if (_isFloat)
        {
            if (_floatBuffer.Length < samples) _floatBuffer = new float[samples];
            Marshal.Copy(data, _floatBuffer, 0, samples);

            for (int i = 0; i < samples; i++)
            {
                float magnitude = Math.Abs(_floatBuffer[i]);
                if (magnitude > max) max = magnitude;
            }
        }
        else
        {
            if (_shortBuffer.Length < samples) _shortBuffer = new short[samples];
            Marshal.Copy(data, _shortBuffer, 0, samples);

            for (int i = 0; i < samples; i++)
            {
                float magnitude = Math.Abs(_shortBuffer[i]) / 32768f;
                if (magnitude > max) max = magnitude;
            }
        }

        return max;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_started) _client?.Stop();
        }
        catch
        {
            // Nothing useful to do if the device already went away.
        }

        _started = false;
        Release(_capture);
        Release(_client);
        _capture = null;
        _client = null;
        _floatBuffer = Array.Empty<float>();
        _shortBuffer = Array.Empty<short>();
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); }
            catch { /* shutdown races are harmless */ }
        }
    }
}
