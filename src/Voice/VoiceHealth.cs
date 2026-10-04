namespace CommPanel.Voice;

internal enum VoiceHealthCode
{
    /// <summary>No call, nothing to say.</summary>
    Idle,

    /// <summary>Both ends are working.</summary>
    Healthy,

    /// <summary>Our microphone stream has stopped delivering frames at all.</summary>
    MicStalled,

    /// <summary>Windows has the microphone muted or turned all the way down.</summary>
    MicMutedInWindows,

    /// <summary>Their voice is arriving but nothing is coming out of our speakers.</summary>
    OutputStalled,

    /// <summary>We are talking and sending, but nothing is reaching them.</summary>
    TheyAreNotReceiving,

    /// <summary>They are receiving us, but their own playback is producing nothing.</summary>
    TheyAreNotHearing,

    /// <summary>A fault was found and something is being tried about it.</summary>
    Repairing
}

/// <summary>A reading of both ends of the call, taken once a second.</summary>
internal sealed class VoiceVitals
{
    public required bool Connected { get; init; }
    public required bool Muted { get; init; }

    /// <summary>Frames our microphone delivered in the last second. About 50 when healthy.</summary>
    public required long CaptureFrames { get; init; }

    /// <summary>Loudest thing our microphone heard in the last second.</summary>
    public required float CapturePeak { get; init; }

    /// <summary>Loudest thing that arrived from them in the last second.</summary>
    public required float IncomingPeak { get; init; }

    /// <summary>Loudest thing we actually pushed to the speakers in the last second.</summary>
    public required float RenderPeak { get; init; }

    /// <summary>
    /// How many times in the last second playback asked for audio and the jitter buffer had
    /// none to give. Any at all means the network was late, which explains silence coming
    /// out of the speakers without the speakers being at fault.
    /// </summary>
    public required int StarvedReads { get; init; }

    public required bool HasCapture { get; init; }
    public required bool HasRender { get; init; }

    /// <summary>
    /// Windows has the capture endpoint muted or at zero. A fact read from the device, not
    /// inferred from silence: a quiet room and a dead microphone look identical on a meter,
    /// so only the device itself can tell them apart.
    /// </summary>
    public required bool InputMutedInWindows { get; init; }

    /// <summary>Their last report, if one has arrived recently enough to trust.</summary>
    public required LinkReport? Peer { get; init; }
}

/// <summary>What a repair step managed to do about a fault.</summary>
internal enum RepairOutcome
{
    /// <summary>This was not the problem; try the next thing.</summary>
    NothingToDo = 0,

    /// <summary>Put right, and the call should recover by itself.</summary>
    Fixed,

    /// <summary>
    /// Found, but only the person can put it right. CommPanel does not change Windows'
    /// own mute or volume settings behind someone's back - least of all a muted
    /// microphone, which is usually muted on purpose.
    /// </summary>
    NeedsYou,

    /// <summary>There is no such rung: the ladder has been worked all the way down.</summary>
    Exhausted
}

/// <summary>
/// What the health monitor is allowed to do about a fault. Implemented by the session, which
/// is the thing that actually owns the devices.
/// </summary>
internal interface IVoiceRepair
{
    /// <summary>CommPanel's own entry in the Windows volume mixer was muted or at zero.</summary>
    RepairOutcome RestoreOwnVolume(out string? what);

    /// <summary>Windows has the playback device muted or turned down. Reported, never changed.</summary>
    RepairOutcome ReportOutputMuted(out string? what);

    /// <summary>Windows has the microphone muted or turned down. Reported, never changed.</summary>
    RepairOutcome ReportInputMuted(out string? what);

    /// <summary>The call is playing into a device that is not the one in use.</summary>
    RepairOutcome MoveCallToDefaultOutput(out string? what);

    RepairOutcome ReopenRender(out string? what);

    RepairOutcome ReopenCapture(out string? what);
}

/// <summary>
/// Watches a call and says, in plain words, which half of it is broken - then tries the
/// things that are usually wrong, in the order they are usually wrong.
///
/// This exists because of a specific failure that two people cannot diagnose between
/// themselves: one person talks, their own meter moves, their packets leave, and the other
/// person hears nothing. From either end alone it is invisible. With each end reporting what
/// it can hear and what it is actually playing, it becomes a question with an answer.
///
/// The repairs are deliberately limited to things CommPanel has the standing to change: its
/// own mixer entry, its own streams, the endpoint the call is using. It never touches the
/// other person's machine - their copy runs the same checks and fixes its own end.
/// </summary>
internal sealed class VoiceHealth
{
    /// <summary>A fault has to persist this long before it is believed.</summary>
    private const int FaultSeconds = 3;

    /// <summary>A report older than this says nothing about now.</summary>
    private static readonly TimeSpan ReportFreshness = TimeSpan.FromSeconds(4);

    private readonly IVoiceRepair _repair;
    private readonly Func<DateTime> _now;

    private int _outputFaultSeconds;
    private int _captureStallSeconds;
    private int _micMutedSeconds;
    private int _notReceivingSeconds;
    private int _notHearingSeconds;

    private int _repairStep;
    private bool _waitingOnUser;
    private DateTime _lastRepairAt = DateTime.MinValue;
    private string? _lastRepairWhat;

    /// <summary>
    /// The clock is a parameter so the repair pacing can be exercised without a test having
    /// to spend the seconds it paces by.
    /// </summary>
    public VoiceHealth(IVoiceRepair repair, Func<DateTime>? clock = null)
    {
        _repair = repair;
        _now = clock ?? (() => DateTime.UtcNow);
    }

    public VoiceHealthCode Code { get; private set; } = VoiceHealthCode.Idle;

    /// <summary>One line for the panel, already in the panel's voice.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>True while a fault is being worked on, so the far end can be told.</summary>
    public bool Trouble => Code is not (VoiceHealthCode.Idle or VoiceHealthCode.Healthy);

    /// <summary>What this end should put on the wire for the other end to read.</summary>
    public LinkReport BuildReport(VoiceVitals vitals) => new(
        micMuted: vitals.Muted,
        incomingSignal: vitals.IncomingPeak > VoiceFormat.SignalFloor,
        outputActive: vitals.RenderPeak > VoiceFormat.SilenceFloor,
        trouble: Trouble,
        micPeak: vitals.CapturePeak,
        playPeak: vitals.RenderPeak);

    public void Evaluate(VoiceVitals vitals)
    {
        if (!vitals.Connected)
        {
            Reset();
            Code = VoiceHealthCode.Idle;
            Message = string.Empty;
            return;
        }

        var peer = vitals.Peer;
        bool talking = vitals.CapturePeak > VoiceFormat.SignalFloor;
        bool theirVoiceArriving = vitals.IncomingPeak > VoiceFormat.SignalFloor;
        bool ourSpeakersWorking = vitals.RenderPeak > VoiceFormat.SilenceFloor;

        // ---- our own output -------------------------------------------------
        // Their voice is arriving with real signal in it and nothing is reaching the
        // speakers. This is the one fault that is certain rather than inferred.
        // Silence from the speakers only counts against them when the buffer had audio to
        // give. While the jitter buffer refills after a late packet the renderer is handed
        // silence deliberately, and on a real link that happens constantly - so a second
        // with any starved read in it is evidence about the network, not the speakers.
        if (theirVoiceArriving && !ourSpeakersWorking && vitals.HasRender && vitals.StarvedReads == 0)
            _outputFaultSeconds++;
        else
            _outputFaultSeconds = 0;

        // ---- our own microphone ---------------------------------------------
        if (vitals.HasCapture && vitals.CaptureFrames == 0)
            _captureStallSeconds++;
        else
            _captureStallSeconds = 0;

        bool peerFresh = peer is not null;
        bool theyHearUs = peerFresh && peer!.Value.IncomingSignal;
        bool theirOutputWorks = peerFresh && peer!.Value.OutputActive;

        // Deliberately not inferred from a silent microphone: people stop talking, and a
        // monitor that calls that a fault is worse than no monitor. Only the endpoint's own
        // mute state is evidence.
        if (!vitals.Muted && vitals.InputMutedInWindows)
            _micMutedSeconds++;
        else
            _micMutedSeconds = 0;

        // We are talking, we are sending, and it is not arriving.
        if (talking && peerFresh && !theyHearUs)
            _notReceivingSeconds++;
        else
            _notReceivingSeconds = 0;

        // They are getting us but their speakers are producing nothing. Counted like every
        // other fault: their playback level legitimately drops to nothing between words, and
        // acting on a single reading of it announced a fault on every pause.
        if (talking && theyHearUs && !theirOutputWorks)
            _notHearingSeconds++;
        else
            _notHearingSeconds = 0;

        // ---- act, most certain fault first ----------------------------------
        if (_captureStallSeconds >= FaultSeconds)
        {
            Fault(VoiceHealthCode.MicStalled, "MICROPHONE STOPPED — REOPENING IT", RepairCapture);
            return;
        }

        if (_outputFaultSeconds >= FaultSeconds)
        {
            Fault(VoiceHealthCode.OutputStalled, "THEIR VOICE IS ARRIVING BUT NOT PLAYING", RepairOutput);
            return;
        }

        if (_micMutedSeconds >= 2)
        {
            Fault(VoiceHealthCode.MicMutedInWindows, "WINDOWS HAS YOUR MICROPHONE MUTED", RepairInput);
            return;
        }

        if (_notReceivingSeconds >= FaultSeconds)
        {
            // Nothing local to repair: the audio is leaving and not arriving.
            Settle(VoiceHealthCode.TheyAreNotReceiving,
                   "YOUR AUDIO IS NOT REACHING THEM — THE LINK IS ONE-WAY");
            return;
        }

        if (_notHearingSeconds >= FaultSeconds)
        {
            Settle(VoiceHealthCode.TheyAreNotHearing,
                   "THEY ARE RECEIVING YOU BUT THEIR PLAYBACK IS SILENT — THEIR END IS CHECKING");
            return;
        }

        if (peerFresh && peer!.Value.MicMuted)
        {
            Settle(VoiceHealthCode.Healthy, "THEIR MICROPHONE IS MUTED");
            return;
        }

        ResetRepairs();
        Settle(VoiceHealthCode.Healthy, peerFresh ? "BOTH ENDS HEALTHY" : "CONNECTED");
    }

    private void Settle(VoiceHealthCode code, string message)
    {
        Code = code;
        Message = message;
    }

    /// <summary>
    /// Reports a fault and takes the next repair step, at most one every few seconds. Stepping
    /// rather than retrying means each possible cause is tried once and the result is visible,
    /// instead of the same failing fix being hammered.
    ///
    /// A step that finds something only the person can put right stops the ladder: nothing
    /// further down would help, and the message becomes an instruction rather than a progress
    /// report.
    /// </summary>
    private void Fault(VoiceHealthCode code, string message,
                       Func<int, (RepairOutcome Outcome, string? What)> ladder)
    {
        Code = code;

        if (_waitingOnUser)
        {
            Message = _lastRepairWhat ?? message;
            return;
        }

        if (_now() - _lastRepairAt < TimeSpan.FromSeconds(FaultSeconds))
        {
            Message = _lastRepairWhat is null ? message : message + " — " + _lastRepairWhat;
            return;
        }

        _lastRepairAt = _now();
        var (outcome, what) = ladder(_repairStep++);

        switch (outcome)
        {
            case RepairOutcome.Fixed when what is not null:
                _lastRepairWhat = what;
                Message = what;
                Code = VoiceHealthCode.Repairing;
                break;

            case RepairOutcome.NeedsYou when what is not null:
                _waitingOnUser = true;
                _lastRepairWhat = what;
                Message = what;
                break;

            case RepairOutcome.Exhausted:
                Message = message + " — NOTHING LEFT TO TRY AUTOMATICALLY";
                break;

            default:
                Message = message;
                break;
        }
    }

    /// <summary>
    /// The output ladder, in the order these things are actually wrong. CommPanel's own mixer
    /// entry first, because it is the most common cause and is ours to put right. Windows'
    /// own mute is only ever reported: it belongs to the person, not to this program.
    /// </summary>
    private (RepairOutcome, string?) RepairOutput(int step) => step switch
    {
        0 => Try(_repair.RestoreOwnVolume),
        1 => Try(_repair.ReportOutputMuted),
        2 => Try(_repair.MoveCallToDefaultOutput),
        3 => Try(_repair.ReopenRender),
        _ => (RepairOutcome.Exhausted, null)
    };

    private (RepairOutcome, string?) RepairCapture(int step) => step switch
    {
        0 => Try(_repair.ReopenCapture),
        1 => Try(_repair.ReportInputMuted),
        _ => (RepairOutcome.Exhausted, null)
    };

    /// <summary>
    /// A microphone muted in Windows is almost always muted on purpose - often with a button
    /// on the headset itself. Unmuting it would start sending audio somebody believes is
    /// private, so this says so and stops.
    /// </summary>
    private (RepairOutcome, string?) RepairInput(int step) => step switch
    {
        0 => Try(_repair.ReportInputMuted),
        _ => (RepairOutcome.Exhausted, null)
    };

    private delegate RepairOutcome RepairAction(out string? what);

    private static (RepairOutcome, string?) Try(RepairAction action)
    {
        try
        {
            RepairOutcome outcome = action(out string? what);
            return (outcome, what);
        }
        catch
        {
            return (RepairOutcome.NothingToDo, null);
        }
    }


    /// <summary>Everything, for when there is no longer a call to judge.</summary>
    private void Reset()
    {
        _outputFaultSeconds = 0;
        _captureStallSeconds = 0;
        _micMutedSeconds = 0;
        _notReceivingSeconds = 0;
        _notHearingSeconds = 0;
        ResetRepairs();
    }

    /// <summary>
    /// Just the repair ladder, so the next fault episode starts at the first rung. The fault
    /// counters are deliberately left alone: they are zeroed by their own conditions, and
    /// clearing them on every healthy tick stopped any fault reaching its threshold.
    /// </summary>
    private void ResetRepairs()
    {
        _repairStep = 0;
        _lastRepairWhat = null;
        _waitingOnUser = false;
    }

    /// <summary>Only a report that arrived recently says anything about now.</summary>
    public LinkReport? FreshPeer(LinkReport? report, DateTime at) =>
        report is not null && _now() - at <= ReportFreshness ? report : null;
}
