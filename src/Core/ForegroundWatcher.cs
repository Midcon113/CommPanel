namespace CommPanel.Core;

/// <summary>
/// Notices when a watched program (a game, a voice client) takes the foreground.
///
/// This deliberately uses a WinEvent hook rather than polling the process list or a WMI
/// process-start subscription: the hook is passive, fires only when the foreground window
/// actually changes, needs no elevation, and costs nothing while a game is running. That
/// matters here - the whole point of CommPanel is to not touch frame times.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private readonly HashSet<uint> _alreadyTriggered = new();
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>Raised on the UI thread with the executable name, e.g. "game.exe".</summary>
    public event Action<string>? WatchedProgramActivated;

    /// <summary>Decides whether an executable name is one the user asked to watch.</summary>
    public Func<string, bool>? ShouldTrigger { get; set; }

    public ForegroundWatcher()
    {
        // Kept in a field so the delegate is not collected while the hook holds it.
        _callback = OnWinEvent;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>
    /// Installs the hook. Must be called from the thread that runs the message loop -
    /// WINEVENT_OUTOFCONTEXT delivers callbacks through that thread's queue, so no extra
    /// thread is created and no cross-thread marshalling is needed.
    /// </summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _alreadyTriggered.Clear();
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                           int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return;

        // Alt-tabbing back into a game should not pop the panel a second time; only the
        // first activation of a given process counts as "the program just opened".
        if (_alreadyTriggered.Contains(pid)) return;

        string? exeName = NativeMethods.GetProcessExeName(pid);
        if (string.IsNullOrEmpty(exeName)) return;

        if (ShouldTrigger?.Invoke(exeName) != true) return;

        // Bound the set so a long uptime with many process launches cannot grow it forever.
        if (_alreadyTriggered.Count > 128) _alreadyTriggered.Clear();
        _alreadyTriggered.Add(pid);

        WatchedProgramActivated?.Invoke(exeName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
