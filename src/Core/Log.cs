using System.Text;

namespace CommPanel.Core;

/// <summary>
/// A small rolling log beside the executable.
///
/// This exists because of a question that could not be answered: a voice call that worked
/// but showed errors, and nothing written down anywhere to say what they were. Everything
/// the panel announces goes in here, along with the device and call events behind it, so the
/// next time something looks wrong there is a record of it rather than a recollection.
///
/// Writes only happen on events - a device change, a call, a status line - never on a timer,
/// so an idle CommPanel writes nothing at all.
/// </summary>
internal static class Log
{
    /// <summary>Beyond this the current file is rolled. Two files, so a session is never lost.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    private static string? _path;
    private static bool _resolved;
    private static bool _failed;

    /// <summary>Where the log is being written, once it has been worked out.</summary>
    public static string? Path
    {
        get { lock (Gate) { Resolve(); return _path; } }
    }

    public static void Write(string category, string message)
    {
        if (_failed) return;

        try
        {
            lock (Gate)
            {
                Resolve();
                if (_path is null) return;

                Roll();

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("  ")
                    .Append(category.PadRight(8))
                    .Append("  ")
                    .Append(message)
                    .AppendLine();

                File.AppendAllText(_path, line.ToString());
            }
        }
        catch
        {
            // A log that throws would be worse than no log. One failure and it stands down.
            _failed = true;
        }
    }

    /// <summary>Notes an HRESULT alongside its meaning, since the number is what is searchable.</summary>
    public static void WriteError(string category, string what, int hr) =>
        Write(category, string.Format("{0} (0x{1:X8})", what, hr));

    public static void Start(string version)
    {
        Write("start", new string('-', 60));
        Write("start", "CommPanel " + version + " starting");
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        foreach (string candidate in new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "CommPanel.log"),
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CommPanel", "CommPanel.log")
        })
        {
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Prove it is writable now rather than discovering it on the first event.
                File.AppendAllText(candidate, string.Empty);
                _path = candidate;
                return;
            }
            catch
            {
                // Try the roaming location, then give up quietly.
            }
        }
    }

    private static void Roll()
    {
        if (_path is null) return;

        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length < MaxBytes) return;

            string previous = _path + ".old";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch
        {
            // Rolling is housekeeping; failing at it must not stop the logging.
        }
    }
}
