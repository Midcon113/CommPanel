using Microsoft.Win32;

namespace CommPanel.Core;

/// <summary>
/// Registers CommPanel in the per-user Run key. Per-user (HKCU) keeps the app installable
/// by copying a folder - no elevation, no installer, no service.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CommPanel";

    private static string CommandLine =>
        string.Format("\"{0}\" --tray", Environment.ProcessPath ??
                      Path.Combine(AppContext.BaseDirectory, "CommPanel.exe"));

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string existing && existing.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Returns true when the registry was left in the requested state.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Rewrites the stored path if the app has been moved to a different folder.</summary>
    public static void RefreshPathIfRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string existing) return;
            if (!string.Equals(existing, CommandLine, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        }
        catch
        {
            // Not being able to self-heal the path is not worth bothering the user about.
        }
    }
}
