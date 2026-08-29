using System.Diagnostics;
using System.Runtime.InteropServices;
using CommPanel.Core;

namespace CommPanel.Audio;

/// <summary>
/// One application's playback session: its own level, volume and mute, exactly as the
/// Windows volume mixer shows it.
/// </summary>
internal sealed class AudioSessionHandle : IDisposable
{
    private IAudioSessionControl? _control;
    private IAudioSessionControl2? _control2;
    private ISimpleAudioVolume? _volume;
    private IAudioMeterInformation? _meter;
    private Guid _eventContext = Guid.NewGuid();
    private bool _disposed;

    internal AudioSessionHandle(string key, IAudioSessionControl control, IAudioSessionControl2? control2,
                                ISimpleAudioVolume? volume, IAudioMeterInformation? meter,
                                uint processId, bool isSystemSounds, string displayName)
    {
        Key = key;
        _control = control;
        _control2 = control2;
        _volume = volume;
        _meter = meter;
        ProcessId = processId;
        IsSystemSounds = isSystemSounds;
        DisplayName = displayName;
    }

    /// <summary>Stable identity for this session, used to match across refreshes.</summary>
    public string Key { get; }

    public uint ProcessId { get; }
    public bool IsSystemSounds { get; }

    /// <summary>Friendly name, e.g. "Google Chrome" or "System Sounds".</summary>
    public string DisplayName { get; }

    public AudioSessionState State
    {
        get
        {
            try { return _control?.GetState(out var state) == 0 ? state : AudioSessionState.Expired; }
            catch { return AudioSessionState.Expired; }
        }
    }

    public float ReadPeak()
    {
        try { return _meter?.GetPeakValue(out float peak) == 0 ? Math.Clamp(peak, 0f, 1f) : 0f; }
        catch { return 0f; }
    }

    public float? ReadVolume()
    {
        try { return _volume?.GetMasterVolume(out float level) == 0 ? Math.Clamp(level, 0f, 1f) : null; }
        catch { return null; }
    }

    public void WriteVolume(float level)
    {
        try { _volume?.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref _eventContext); }
        catch { /* the app may have exited mid-drag */ }
    }

    public bool ReadMute()
    {
        try { return _volume?.GetMute(out bool muted) == 0 && muted; }
        catch { return false; }
    }

    public void WriteMute(bool muted)
    {
        try { _volume?.SetMute(muted, ref _eventContext); }
        catch { /* as above */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release(_meter);
        Release(_volume);
        Release(_control2);
        Release(_control);
        _meter = null;
        _volume = null;
        _control2 = null;
        _control = null;
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

/// <summary>
/// Enumerates the applications playing through an endpoint and keeps their controls open.
///
/// Sessions are matched across refreshes by identity, so a handle - and therefore the fader
/// the user might be dragging - survives a re-enumeration. Only genuinely new applications
/// cost a round of COM activation.
/// </summary>
internal sealed class SessionMixer : IDisposable
{
    private const int ClsCtxAll = 23;

    private readonly Dictionary<string, AudioSessionHandle> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _processNames = new();

    private IAudioSessionManager2? _manager;
    private bool _disposed;

    private SessionMixer(string deviceId, IAudioSessionManager2 manager)
    {
        DeviceId = deviceId;
        _manager = manager;
    }

    public string DeviceId { get; }

    public static SessionMixer? Open(IMMDeviceEnumerator enumerator, string deviceId)
    {
        if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null) return null;

        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? managerObject) != 0 ||
                managerObject is not IAudioSessionManager2 manager)
            {
                return null;
            }

            return new SessionMixer(deviceId, manager);
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

    /// <summary>
    /// Re-enumerates sessions, returning the live ones ordered for display. Handles for
    /// sessions that are still present are reused rather than rebuilt.
    /// </summary>
    public List<AudioSessionHandle> Refresh()
    {
        var manager = _manager;
        if (manager is null) return new List<AudioSessionHandle>();

        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (manager.GetSessionEnumerator(out var sessions) == 0 && sessions is not null)
        {
            try
            {
                if (sessions.GetCount(out int count) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var control) != 0 || control is null) continue;

                        string? key = Adopt(control, out bool reused);
                        if (key is null)
                        {
                            Release(control);
                            continue;
                        }

                        seen.Add(key);
                        if (reused) Release(control); // the stored handle already owns one
                    }
                }
            }
            finally
            {
                Release(sessions);
            }
        }

        // Drop anything that has gone away.
        foreach (string key in _sessions.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _sessions[key].Dispose();
            _sessions.Remove(key);
        }

        // Expired sessions belong to applications that have closed.
        foreach (var expired in _sessions.Values.Where(s => s.State == AudioSessionState.Expired).ToList())
        {
            expired.Dispose();
            _sessions.Remove(expired.Key);
        }

        return _sessions.Values
            .OrderByDescending(s => s.State == AudioSessionState.Active)
            .ThenBy(s => s.IsSystemSounds)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Wraps a session control, or returns its key if we already hold one.</summary>
    private string? Adopt(IAudioSessionControl control, out bool reused)
    {
        reused = false;

        var control2 = control as IAudioSessionControl2;
        if (control2 is null) return null;

        if (control2.GetSessionInstanceIdentifier(out string? key) != 0 || string.IsNullOrEmpty(key))
            return null;

        if (_sessions.ContainsKey(key))
        {
            reused = true;
            return key;
        }

        bool isSystemSounds = control2.IsSystemSoundsSession() == 0;
        control2.GetProcessId(out uint processId);

        var volumeIid = typeof(ISimpleAudioVolume).GUID;
        var meterIid = typeof(IAudioMeterInformation).GUID;

        var volume = control as ISimpleAudioVolume;
        var meter = control as IAudioMeterInformation;

        string name = isSystemSounds ? "System Sounds" : DescribeProcess(processId, control);

        _sessions[key] = new AudioSessionHandle(key, control, control2, volume, meter,
                                                processId, isSystemSounds, name);
        return key;
    }

    /// <summary>
    /// A readable name for the application. The session's own display name is usually empty
    /// for desktop programs, so this falls back to the executable's file description and
    /// then to its file name.
    /// </summary>
    private string DescribeProcess(uint processId, IAudioSessionControl control)
    {
        if (_processNames.TryGetValue(processId, out string? cached)) return cached;

        string name = "Unknown";

        try
        {
            if (control.GetDisplayName(out string? display) == 0 &&
                !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@'))
            {
                name = display.Trim();
            }
            else
            {
                string? exe = NativeMethods.GetProcessExeName(processId);
                if (!string.IsNullOrEmpty(exe))
                {
                    name = Path.GetFileNameWithoutExtension(exe);

                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        string? path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                            if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                        }
                    }
                    catch
                    {
                        // Protected or 32/64-bit mismatched processes refuse MainModule; the
                        // executable name is a perfectly good fallback.
                    }
                }
            }
        }
        catch
        {
            // Keep "Unknown" rather than failing the whole refresh over one session.
        }

        // Bounded: a long session keeps starting and stopping processes.
        if (_processNames.Count > 256) _processNames.Clear();
        _processNames[processId] = name;
        return name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _processNames.Clear();

        Release(_manager);
        _manager = null;
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
