using System.Runtime.InteropServices;

namespace CommPanel.Audio;

/// <summary>
/// The meter and volume control for one endpoint, held open so the panel does not have to
/// re-activate COM objects on every meter tick.
///
/// One of these exists per direction at a time - for whichever device is currently default -
/// and is replaced when the default changes.
/// </summary>
internal sealed class EndpointControls : IDisposable
{
    private const int ClsCtxAll = 23;

    private IAudioMeterInformation? _meter;
    private IAudioEndpointVolume? _volume;
    private Guid _eventContext = Guid.NewGuid();
    private bool _disposed;

    private EndpointControls(string deviceId, IAudioMeterInformation? meter, IAudioEndpointVolume? volume)
    {
        DeviceId = deviceId;
        _meter = meter;
        _volume = volume;
    }

    public string DeviceId { get; }

    /// <summary>False when the endpoint refused to give up a volume interface.</summary>
    public bool HasVolume => _volume is not null;

    public bool HasMeter => _meter is not null;

    /// <summary>
    /// Opens the meter and volume interfaces for a device. Returns null when the device is
    /// gone, which happens routinely while devices are being switched.
    /// </summary>
    public static EndpointControls? Open(IMMDeviceEnumerator enumerator, string deviceId)
    {
        if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null) return null;

        try
        {
            var meterId = typeof(IAudioMeterInformation).GUID;
            var volumeId = typeof(IAudioEndpointVolume).GUID;

            IAudioMeterInformation? meter = null;
            IAudioEndpointVolume? volume = null;

            if (device.Activate(ref meterId, ClsCtxAll, IntPtr.Zero, out object? meterObject) == 0)
                meter = meterObject as IAudioMeterInformation;

            if (device.Activate(ref volumeId, ClsCtxAll, IntPtr.Zero, out object? volumeObject) == 0)
                volume = volumeObject as IAudioEndpointVolume;

            if (meter is null && volume is null) return null;

            return new EndpointControls(deviceId, meter, volume);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Peak level since the previous call, 0.0 to 1.0. Returns 0 if unavailable.</summary>
    public float ReadPeak()
    {
        var meter = _meter;
        if (meter is null) return 0f;

        try
        {
            return meter.GetPeakValue(out float peak) == 0 ? Math.Clamp(peak, 0f, 1f) : 0f;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>Master volume as 0.0 to 1.0, or null when the endpoint has no volume control.</summary>
    public float? ReadVolume()
    {
        var volume = _volume;
        if (volume is null) return null;

        try
        {
            return volume.GetMasterVolumeLevelScalar(out float level) == 0
                ? Math.Clamp(level, 0f, 1f)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool? ReadMute()
    {
        var volume = _volume;
        if (volume is null) return null;

        try
        {
            return volume.GetMute(out bool muted) == 0 ? muted : null;
        }
        catch
        {
            return null;
        }
    }

    public void WriteVolume(float level)
    {
        var volume = _volume;
        if (volume is null) return;

        try { volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _eventContext); }
        catch { /* the device may have vanished mid-drag */ }
    }

    public void WriteMute(bool muted)
    {
        var volume = _volume;
        if (volume is null) return;

        try { volume.SetMute(muted, ref _eventContext); }
        catch { /* as above */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release(_meter);
        Release(_volume);
        _meter = null;
        _volume = null;
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
