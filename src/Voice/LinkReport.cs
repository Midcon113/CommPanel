namespace CommPanel.Voice;

/// <summary>
/// What one end of a call tells the other about itself, once a second.
///
/// This exists because the usual failure in a voice call is invisible from the end that has
/// to fix it. You can see your own microphone working and your own packets leaving, and still
/// have no idea that the other person is hearing nothing - so both people sit there saying
/// "can you hear me now". Four bytes on the wire turn that into something either end can
/// simply read off the panel.
/// </summary>
internal readonly struct LinkReport
{
    private const byte FlagMicMuted = 1 << 0;
    private const byte FlagIncomingSignal = 1 << 1;
    private const byte FlagOutputActive = 1 << 2;
    private const byte FlagTrouble = 1 << 3;

    public LinkReport(bool micMuted, bool incomingSignal, bool outputActive, bool trouble,
                      float micPeak, float playPeak)
    {
        MicMuted = micMuted;
        IncomingSignal = incomingSignal;
        OutputActive = outputActive;
        Trouble = trouble;
        MicPeak = micPeak;
        PlayPeak = playPeak;
    }

    /// <summary>They have cut their own microphone.</summary>
    public bool MicMuted { get; }

    /// <summary>Audio with real signal in it is arriving at their end - they are getting us.</summary>
    public bool IncomingSignal { get; }

    /// <summary>Their playback is actually producing sound - they can hear us.</summary>
    public bool OutputActive { get; }

    /// <summary>Their end has diagnosed a fault of its own and is working on it.</summary>
    public bool Trouble { get; }

    public float MicPeak { get; }

    public float PlayPeak { get; }

    public void Encode(byte[] buffer, int offset)
    {
        byte flags = 0;
        if (MicMuted) flags |= FlagMicMuted;
        if (IncomingSignal) flags |= FlagIncomingSignal;
        if (OutputActive) flags |= FlagOutputActive;
        if (Trouble) flags |= FlagTrouble;

        buffer[offset] = flags;
        buffer[offset + 1] = ToByte(MicPeak);
        buffer[offset + 2] = ToByte(PlayPeak);
        buffer[offset + 3] = 1; // format version, so a later field can be added safely
    }

    public static LinkReport Decode(byte[] buffer, int offset)
    {
        byte flags = buffer[offset];

        return new LinkReport(
            (flags & FlagMicMuted) != 0,
            (flags & FlagIncomingSignal) != 0,
            (flags & FlagOutputActive) != 0,
            (flags & FlagTrouble) != 0,
            buffer[offset + 1] / 255f,
            buffer[offset + 2] / 255f);
    }

    private static byte ToByte(float level) =>
        (byte)Math.Clamp((int)MathF.Round(level * 255f), 0, 255);
}
