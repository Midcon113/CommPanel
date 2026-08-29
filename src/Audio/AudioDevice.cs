namespace CommPanel.Audio;

/// <summary>An immutable snapshot of one audio endpoint at enumeration time.</summary>
internal sealed class AudioDevice
{
    public required string Id { get; init; }

    /// <summary>Short endpoint name, e.g. "Speakers" or "Headset Earphone".</summary>
    public required string ShortName { get; init; }

    /// <summary>Adapter / driver name, e.g. "Realtek High Definition Audio".</summary>
    public required string Adapter { get; init; }

    /// <summary>Full name as the Sound control panel shows it.</summary>
    public required string FullName { get; init; }

    public required EDataFlow Flow { get; init; }
    public required FormFactor FormFactor { get; init; }

    /// <summary>True when this endpoint is the default for the Console/Multimedia roles.</summary>
    public bool IsDefault { get; set; }

    /// <summary>True when this endpoint is the default for the Communications role.</summary>
    public bool IsDefaultCommunications { get; set; }

    /// <summary>
    /// True when the endpoint still exists as far as Windows is concerned but the hardware
    /// behind it is known to be powered down - a wireless headset that has timed out and
    /// switched itself off, whose base station is still plugged in. Selecting it would
    /// produce silence, so the panel says so rather than offering it as if it were live.
    /// </summary>
    public bool IsOffline { get; set; }

    public override string ToString() => FullName;
}
