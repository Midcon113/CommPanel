using System.Runtime.InteropServices;
using CommPanel.Audio;

namespace CommPanel.Voice;

/// <summary>Small pieces shared by the capture and render halves of the voice link.</summary>
internal static class VoiceDevice
{
    /// <summary>
    /// Declares a stream as voice communications before it is initialised.
    ///
    /// This is what invites Windows to apply echo cancellation and noise suppression on
    /// hardware that offers them, and it matches the Communications endpoint role the panel
    /// already exposes. Not every device implements it, so a refusal is ignored rather than
    /// treated as a failure - the call still works, it simply does not get the processing.
    /// </summary>
    public static void MarkAsCommunications(IAudioClient client)
    {
        try
        {
            if (client is not IAudioClient2 client2) return;

            var properties = new AudioClientProperties
            {
                Size = (uint)Marshal.SizeOf<AudioClientProperties>(),
                IsOffload = false,
                Category = AudioStreamCategory.Communications,
                Options = 0
            };

            client2.SetClientProperties(ref properties);
        }
        catch
        {
            // Older or unusual drivers may not expose IAudioClient2 at all.
        }
    }

    /// <summary>Turns an IAudioClient initialise failure into something worth showing a user.</summary>
    public static string Describe(int hr, string what) => (uint)hr switch
    {
        0x80070005 => what + " access blocked by Windows privacy settings",
        0x88890004 => what + " was disconnected",
        0x8889000A => what + " already in exclusive use",
        0x88890008 => what + " format not supported",
        _ => string.Format("cannot open {0} (0x{1:X8})", what, hr)
    };

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); }
            catch { /* shutdown races are harmless */ }
        }
    }
}
