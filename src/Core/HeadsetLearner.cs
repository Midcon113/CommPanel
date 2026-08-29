namespace CommPanel.Core;

internal enum LearnPhase
{
    Idle,
    PoweredOffFirst,
    PoweredOn,
    PoweredOffSecond
}

/// <summary>A single input report, copied out of the reader's reused buffer.</summary>
internal sealed class CapturedReport
{
    public required HidDeviceInfo Device { get; init; }
    public required byte[] Data { get; init; }
    public required LearnPhase Phase { get; init; }
}

internal sealed class LearnOutcome
{
    public HeadsetProfile? Profile { get; init; }
    public required string Explanation { get; init; }
    public bool Succeeded => Profile is not null;
}

/// <summary>
/// Works out how an unknown base station signals headset power, by watching what its HID
/// reports do across a known sequence of power states.
///
/// The user is walked through off, on, then off again. Two separate "off" captures matter:
/// a byte that genuinely encodes power state holds the same value in both, while counters
/// and other incidental traffic do not, which is what keeps unrelated devices and stray
/// mouse or keyboard reports from being mistaken for a signal.
/// </summary>
internal static class HeadsetLearner
{
    /// <summary>Reports below this length cannot carry a status byte worth reading.</summary>
    private const int MinimumReportLength = 3;

    /// <summary>Bytes 0 and 1 are the report id and tag, so a status byte starts at 2.</summary>
    private const int FirstStatusOffset = 2;

    /// <summary>
    /// Derives a profile from captured reports.
    /// </summary>
    /// <param name="captures">Everything recorded across all three phases.</param>
    /// <param name="adapterName">The audio adapter the user identified as their headset.</param>
    public static LearnOutcome Analyse(IReadOnlyList<CapturedReport> captures, string adapterName)
    {
        var poweredOn = captures.Where(c => c.Phase == LearnPhase.PoweredOn).ToList();
        var poweredOff = captures.Where(c =>
            c.Phase is LearnPhase.PoweredOffFirst or LearnPhase.PoweredOffSecond).ToList();

        if (poweredOn.Count == 0 || poweredOff.Count == 0)
        {
            return new LearnOutcome
            {
                Explanation = poweredOn.Count == 0 && poweredOff.Count == 0
                    ? "No device reported anything while the headset was switched. Its base station may not "
                      + "expose power state over HID, in which case CommPanel cannot detect it."
                    : "Reports were seen in only one power state, so there is nothing to compare. "
                      + "Make sure the headset fully powered down and back up during the steps."
            };
        }

        var candidates = new List<Candidate>();

        // Group by the interface and the report's identifying bytes: different report kinds
        // from the same device are different messages and must not be compared with each other.
        foreach (var group in captures.GroupBy(c => new ReportKey(c.Device.Path, c.Data[0], KeyTag(c.Data))))
        {
            var onSamples = group.Where(c => c.Phase == LearnPhase.PoweredOn).ToList();
            var offSamples = group.Where(c =>
                c.Phase is LearnPhase.PoweredOffFirst or LearnPhase.PoweredOffSecond).ToList();

            if (onSamples.Count == 0 || offSamples.Count == 0) continue;

            int shortest = group.Min(c => c.Data.Length);

            for (int offset = FirstStatusOffset; offset < shortest; offset++)
            {
                var onValues = onSamples.Select(c => c.Data[offset]).Distinct().ToList();
                var offValues = offSamples.Select(c => c.Data[offset]).Distinct().ToList();

                // The byte has to be steady in each state and different between them.
                if (onValues.Count != 1 || offValues.Count != 1) continue;
                if (onValues[0] == offValues[0]) continue;

                // Require both "off" captures to agree, when we have both.
                bool sawFirstOff = offSamples.Any(c => c.Phase == LearnPhase.PoweredOffFirst);
                bool sawSecondOff = offSamples.Any(c => c.Phase == LearnPhase.PoweredOffSecond);
                bool corroborated = sawFirstOff && sawSecondOff;

                candidates.Add(new Candidate
                {
                    Device = group.First().Device,
                    ReportId = group.Key.ReportId,
                    ReportTag = group.Key.Tag,
                    Offset = offset,
                    OnValue = onValues[0],
                    OffValue = offValues[0],
                    SampleCount = onSamples.Count + offSamples.Count,
                    Corroborated = corroborated
                });
            }
        }

        if (candidates.Count == 0)
        {
            return new LearnOutcome
            {
                Explanation = "Reports were captured, but no byte changed consistently between the headset "
                            + "being on and off. The device may encode power state in a way CommPanel "
                            + "cannot recognise."
            };
        }

        var best = Rank(candidates, adapterName).First();

        var profile = new HeadsetProfile
        {
            Name = best.Device.ProductName ?? adapterName,
            AdapterMatch = adapterName,
            VendorId = best.Device.VendorId,
            ProductId = best.Device.ProductId,
            UsagePage = best.Device.UsagePage,
            ReportId = best.ReportId,
            ReportTag = best.ReportTag,
            StatusOffset = best.Offset,
            PoweredOnValue = best.OnValue,
            PoweredOffValue = best.OffValue,
            IsBuiltIn = false
        };

        string caveat = best.Corroborated
            ? string.Empty
            : "\r\n\r\nNote: only one of the two power-off steps produced a report, so this was derived "
              + "from a single observation. Re-run the wizard if it does not behave correctly.";

        return new LearnOutcome
        {
            Profile = profile,
            Explanation = "Learned from " + (best.Device.ProductName ?? "the device") + "." + caveat
        };
    }

    /// <summary>
    /// Orders candidates best-first.
    ///
    /// The important case this handles: a battery-level byte also changes when the headset
    /// powers down, and on its own looks just as good as the real state flag. A genuine
    /// state flag tends to show the same "on" value at the same offset across several of the
    /// device's report kinds, whereas a battery reading does not - so agreement across
    /// reports is weighted above everything else.
    /// </summary>
    private static List<Candidate> Rank(List<Candidate> candidates, string adapterName)
    {
        string[] hintWords = adapterName
            .Split(new[] { ' ', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .ToArray();

        foreach (var candidate in candidates)
        {
            candidate.Agreement = candidates.Count(other =>
                other != candidate &&
                other.Offset == candidate.Offset &&
                other.OnValue == candidate.OnValue &&
                (other.ReportId != candidate.ReportId || other.ReportTag != candidate.ReportTag));

            string product = candidate.Device.ProductName ?? string.Empty;
            candidate.NameMatches = hintWords.Any(w => product.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .OrderByDescending(c => c.Corroborated)
            .ThenByDescending(c => c.NameMatches)
            .ThenByDescending(c => c.Agreement)
            .ThenByDescending(c => c.SampleCount)
            .ThenBy(c => c.Offset)
            .ThenBy(c => c.ReportTag)
            .ToList();
    }

    /// <summary>Byte 1 identifies the message kind on these devices; -1 for reports too short to have one.</summary>
    private static int KeyTag(byte[] data) => data.Length >= 2 ? data[1] : -1;

    /// <summary>Devices worth listening to while learning: vendor-defined pages carry status.</summary>
    public static bool IsCandidateInterface(HidDeviceInfo device) =>
        device.IsVendorDefined && device.InputReportLength >= MinimumReportLength;

    private readonly record struct ReportKey(string Path, byte ReportId, int Tag);

    private sealed class Candidate
    {
        public required HidDeviceInfo Device { get; init; }
        public required byte ReportId { get; init; }
        public required int ReportTag { get; init; }
        public required int Offset { get; init; }
        public required byte OnValue { get; init; }
        public required byte OffValue { get; init; }
        public required int SampleCount { get; init; }
        public required bool Corroborated { get; init; }

        public int Agreement { get; set; }
        public bool NameMatches { get; set; }
    }
}

/// <summary>
/// Holds open every candidate HID interface while the wizard walks the user through the
/// power states, tagging each report with the phase it arrived in.
/// </summary>
internal sealed class HeadsetLearnSession : IDisposable
{
    /// <summary>Enough for any plausible machine, and a guard against opening hundreds of handles.</summary>
    private const int MaxInterfaces = 48;

    private readonly List<HidReportReader> _readers = new();
    private readonly List<CapturedReport> _captures = new();
    private readonly object _gate = new();

    private LearnPhase _phase = LearnPhase.Idle;

    public int InterfaceCount => _readers.Count;

    public int CaptureCount
    {
        get { lock (_gate) return _captures.Count; }
    }

    /// <summary>Opens every vendor-defined HID interface present. Returns how many were opened.</summary>
    public int Start()
    {
        foreach (var device in HidDevices.Enumerate().Where(HeadsetLearner.IsCandidateInterface))
        {
            if (_readers.Count >= MaxInterfaces) break;

            var reader = new HidReportReader(device, OnReport);
            if (reader.Start()) _readers.Add(reader);
            else reader.Dispose();
        }

        return _readers.Count;
    }

    public void BeginPhase(LearnPhase phase)
    {
        lock (_gate) _phase = phase;
    }

    public int CountFor(LearnPhase phase)
    {
        lock (_gate) return _captures.Count(c => c.Phase == phase);
    }

    public List<CapturedReport> Snapshot()
    {
        lock (_gate) return _captures.ToList();
    }

    private void OnReport(HidDeviceInfo device, byte[] buffer, int length)
    {
        lock (_gate)
        {
            if (_phase == LearnPhase.Idle) return;

            // The reader reuses its buffer, so the bytes have to be copied out here.
            var data = new byte[length];
            Array.Copy(buffer, data, length);

            // A stuck device could otherwise fill memory during a long wizard step.
            if (_captures.Count > 5000) return;

            _captures.Add(new CapturedReport { Device = device, Data = data, Phase = _phase });
        }
    }

    public void Dispose()
    {
        foreach (var reader in _readers) reader.Dispose();
        _readers.Clear();
    }
}
