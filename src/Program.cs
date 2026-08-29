using System.Diagnostics;
using CommPanel.Audio;
using CommPanel.Core;
using CommPanel.Ui;

namespace CommPanel;

internal static class Program
{
    private const string MutexName = @"Local\CommPanel.SingleInstance.6F2A";

    [STAThread]
    private static void Main(string[] args)
    {
        // One panel per session. A second launch just brings the running one forward.
        using var instanceLock = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            uint showMessage = NativeMethods.RegisterWindowMessage("CommPanel.ShowPanel.6F2A");
            if (showMessage != 0)
                NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, showMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        // A tray utility has no business competing with a game for CPU. Below-normal keeps
        // CommPanel off the scheduler's critical path without making the UI feel slow.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* not fatal */ }

        var settings = AppSettings.Load();
        Ui.PanelTheme.Bloom = settings.BloomMultiplier;
        StartupRegistration.RefreshPathIfRegistered();

        AudioEndpointService audio;
        try
        {
            audio = new AudioEndpointService();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "CommPanel could not reach the Windows audio service.\r\n\r\n" + ex.Message,
                "CommPanel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (audio)
        using (var panel = new PanelForm(audio, settings))
        {
            bool startHidden = settings.StartInTray ||
                               args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            if (!startHidden) panel.ShowPanel(activate: true);
            else NativeMethods.TrimWorkingSet();

            Application.Run(new ApplicationContext());
        }
    }

    private static void ReportFatal(Exception? exception)
    {
        try
        {
            MessageBox.Show(
                "CommPanel hit an unexpected error.\r\n\r\n" + (exception?.ToString() ?? "Unknown error"),
                "CommPanel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing further we can do.
        }
    }
}
