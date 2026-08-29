using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommPanel.Core;

/// <summary>
/// User settings, stored as JSON beside the executable so the whole app stays portable:
/// copy the folder, keep your settings. If the folder is read-only (Program Files, a
/// network share), settings fall back to %APPDATA%\CommPanel.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>Switch the Communications role along with the default device.</summary>
    public bool LinkCommunications { get; set; } = true;

    /// <summary>Start minimised to the notification area instead of showing the panel.</summary>
    public bool StartInTray { get; set; }

    /// <summary>Hide the panel again immediately after a device is selected.</summary>
    public bool AutoHideAfterSwitch { get; set; }

    /// <summary>Register Ctrl+Alt+C as a global show/hide hotkey.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Pop the panel up when one of <see cref="WatchedProcesses"/> comes to the foreground.</summary>
    public bool WatchProcesses { get; set; } = true;

    /// <summary>Executable names, e.g. "helldivers2.exe". Matched case-insensitively.</summary>
    public List<string> WatchedProcesses { get; set; } = new();

    /// <summary>
    /// Watch a supported wireless headset base station over HID, so that powering the
    /// headset off is detected even though Windows keeps its endpoint alive.
    /// </summary>
    public bool WatchHeadsetPower { get; set; } = true;

    /// <summary>
    /// Meter the microphone. This requires opening the microphone while the panel is on
    /// screen, because a capture endpoint reports no level unless something is recording
    /// from it - so Windows will show its microphone-in-use indicator for as long as the
    /// panel is open. Audio is used only to compute a level and is discarded.
    /// </summary>
    public bool MeterMicrophone { get; set; } = true;

    /// <summary>
    /// How much the lamps and lit meter segments glow, 0 to 1. Stored as a slider position
    /// where 0.5 is the reference look; the renderer doubles it to get its multiplier, so
    /// the full range runs from no bloom at all to twice the reference.
    /// </summary>
    public float BloomIntensity { get; set; } = 0.5f;

    /// <summary>The multiplier the renderer actually uses.</summary>
    [JsonIgnore]
    public float BloomMultiplier => Math.Clamp(BloomIntensity, 0f, 1f) * 2f;

    /// <summary>
    /// Scales the panel: fonts and every layout measurement together, so larger text gets a
    /// larger panel to sit in rather than being clipped by fixed-size keys. 1.0 is the
    /// reference size; the window resizes itself to match.
    /// </summary>
    public float FontScale { get; set; } = 1f;

    /// <summary>Clamped, so a corrupt settings file cannot produce an unusable window.</summary>
    [JsonIgnore]
    public float SafeFontScale => Math.Clamp(FontScale, 0.8f, 2.0f);

    /// <summary>Whether the per-application mixer section is expanded.</summary>
    public bool MixerExpanded { get; set; }

    /// <summary>
    /// Ask a supported headset for its state when CommPanel starts and whenever the panel is
    /// opened. This is the only way to know a headset was already off at launch, and it is the
    /// one thing CommPanel writes to the device: a single documented status request. Turn it
    /// off to keep the HID connection strictly read-only.
    /// </summary>
    public bool QueryHeadsetStatus { get; set; } = true;

    /// <summary>Show the level meters and volume faders on the panel.</summary>
    public bool ShowMeters { get; set; } = true;

    /// <summary>Switch back to the headset when it is powered on again.</summary>
    public bool ReturnToHeadset { get; set; } = true;

    /// <summary>
    /// Headset profiles taught to CommPanel by the "Learn my headset" wizard. Stored here
    /// rather than compiled in, because every headset model reports differently and even a
    /// firmware update can change how a model identifies itself. Entries are plain JSON, so
    /// a working profile can be shared with someone who has the same hardware.
    /// </summary>
    public List<HeadsetProfile> HeadsetProfiles { get; set; } = new();

    /// <summary>Endpoint ids the user has chosen not to see on the panel.</summary>
    public List<string> HiddenDeviceIds { get; set; } = new();

    /// <summary>
    /// Move to another device automatically when the one in use goes offline.
    /// </summary>
    public bool AutoFallback { get; set; } = true;

    /// <summary>
    /// Output endpoint ids, most recently chosen first. Drives failover: when a device
    /// drops out, the highest entry still available takes over. Maintained from the user's
    /// own choices, so it needs no configuring.
    /// </summary>
    public List<string> OutputPriority { get; set; } = new();

    /// <summary>Input endpoint ids, most recently chosen first.</summary>
    public List<string> InputPriority { get; set; } = new();

    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;

    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static string PortablePath =>
        Path.Combine(AppContext.BaseDirectory, "CommPanel.settings.json");

    private static string RoamingPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CommPanel", "CommPanel.settings.json");

    public static AppSettings Load()
    {
        foreach (string path in new[] { PortablePath, RoamingPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (loaded is null) continue;
                loaded.FilePath = path;
                return loaded;
            }
            catch
            {
                // A corrupt or unreadable settings file must never stop the app from starting.
            }
        }

        return new AppSettings { FilePath = null };
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);

        // Prefer staying portable; fall back to roaming only if the install folder is read-only.
        foreach (string path in new[] { FilePath ?? PortablePath, RoamingPath })
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
                FilePath = path;
                return;
            }
            catch
            {
                // Try the next location.
            }
        }
    }

    /// <summary>
    /// Records that the user chose this device, putting it at the head of the failover
    /// order for its direction. Only deliberate choices are recorded - an automatic
    /// failover must not rewrite the preference that drove it.
    /// </summary>
    public void RememberChoice(bool isOutput, string deviceId)
    {
        var order = isOutput ? OutputPriority : InputPriority;

        order.RemoveAll(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase));
        order.Insert(0, deviceId);

        // Devices come and go; without a cap this would accumulate ids forever.
        const int limit = 16;
        if (order.Count > limit) order.RemoveRange(limit, order.Count - limit);
    }

    public List<string> PriorityFor(bool isOutput) => isOutput ? OutputPriority : InputPriority;

    public bool IsWatched(string exeName) =>
        WatchedProcesses.Any(p => string.Equals(p, exeName, StringComparison.OrdinalIgnoreCase));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
