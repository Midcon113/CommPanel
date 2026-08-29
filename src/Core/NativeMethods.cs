using System.Runtime.InteropServices;
using System.Text;

namespace CommPanel.Core;

internal static class NativeMethods
{
    // ---- Window activation -------------------------------------------------

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    // ---- Cross-instance signalling ----------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- Global hotkey -----------------------------------------------------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Foreground-change hook (used to spot a watched game starting) -----

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                      int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                                                WinEventProc lpfnWinEventProc, uint idProcess,
                                                uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- Process identity --------------------------------------------------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
                                                         StringBuilder lpExeName, ref int lpdwSize);

    /// <summary>
    /// Returns the executable file name (e.g. "game.exe") for a process id, or null.
    /// Uses QueryFullProcessImageName rather than Process.GetProcessById so that a
    /// protected or already-exited process costs a failed handle open, not an exception.
    /// </summary>
    public static string? GetProcessExeName(uint processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            int capacity = 260;
            var buffer = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(handle, 0, buffer, ref capacity)) return null;

            string full = buffer.ToString(0, capacity);
            int slash = full.LastIndexOf('\\');
            return slash >= 0 ? full[(slash + 1)..] : full;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- Working-set trimming ---------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Asks Windows to page out the working set. Called when the panel hides to the tray so
    /// a background CommPanel holds almost no physical memory while a game is running.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try { K32EmptyWorkingSet(GetCurrentProcess()); }
        catch { /* purely an optimisation */ }
    }
}
