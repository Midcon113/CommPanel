namespace CommPanel.Audio;

/// <summary>
/// Decides which endpoint to move to when the one in use disappears - a headset powered
/// off, a USB dongle pulled, a monitor switched off.
///
/// Windows performs a fallback of its own here, but it picks from its internal preference
/// order, which regularly lands on a monitor's HDMI audio. This picks the device the user
/// would have picked: most recently chosen first, then by how plausible the device is as
/// something a person actually listens through.
///
/// Kept free of UI and COM so the choice can be tested against synthetic device lists.
/// </summary>
internal static class FallbackPolicy
{
    /// <summary>
    /// Returns the best available replacement, or null when nothing is left to switch to.
    /// </summary>
    /// <param name="available">Currently active endpoints the user has not hidden.</param>
    /// <param name="priority">Device ids most-recently-chosen first.</param>
    /// <param name="lostDeviceId">The endpoint that went away; never returned.</param>
    public static AudioDevice? ChooseReplacement(
        IReadOnlyList<AudioDevice> available,
        IReadOnlyList<string> priority,
        string? lostDeviceId)
    {
        AudioDevice? best = null;
        int bestPriority = int.MaxValue;
        int bestForm = int.MaxValue;

        foreach (var device in available)
        {
            if (lostDeviceId is not null &&
                string.Equals(device.Id, lostDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int devicePriority = PriorityOf(priority, device.Id);
            int deviceForm = FormFactorRank(device.FormFactor);

            // Recent use wins outright; form factor only breaks ties between devices the
            // user has never chosen, and enumeration order breaks anything still level.
            bool better = devicePriority < bestPriority ||
                          (devicePriority == bestPriority && deviceForm < bestForm);

            if (best is null || better)
            {
                best = device;
                bestPriority = devicePriority;
                bestForm = deviceForm;
            }
        }

        return best;
    }

    private static int PriorityOf(IReadOnlyList<string> priority, string deviceId)
    {
        for (int i = 0; i < priority.Count; i++)
        {
            if (string.Equals(priority[i], deviceId, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// How likely a device is to be one someone actually listens or talks through. Used
    /// only when there is no usage history to go on - it is what stops a fresh install
    /// from failing over onto a monitor.
    /// </summary>
    private static int FormFactorRank(FormFactor formFactor) => formFactor switch
    {
        FormFactor.Speakers => 0,
        FormFactor.Headphones => 0,
        FormFactor.Headset => 0,
        FormFactor.Microphone => 0,
        FormFactor.Handset => 1,
        FormFactor.LineLevel => 2,
        FormFactor.Spdif => 3,
        FormFactor.UnknownDigitalPassthrough => 3,
        FormFactor.DigitalAudioDisplayDevice => 4, // a monitor: rarely what anyone wants
        FormFactor.RemoteNetworkDevice => 5,
        _ => 2
    };
}
