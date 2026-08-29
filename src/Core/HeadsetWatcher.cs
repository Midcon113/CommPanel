namespace CommPanel.Core;

/// <summary>
/// Notices a wireless headset being powered on or off.
///
/// This exists because some wireless headsets cannot be detected any other way. Their base
/// station is what is plugged into USB, so the audio endpoint stays Active whether or not
/// the headset attached to it is switched on - Windows keeps rendering audio into a headset
/// that is sitting on the desk, turned off, and reports nothing. Verified on an Arctis Nova
/// Pro Wireless: a full off/on cycle produced zero Core Audio notifications, while the base
/// station's vendor HID interface reported the change immediately.
///
/// Which devices it understands comes from <see cref="HeadsetProfile"/> - one built in, plus
/// anything the user has taught it. The watcher blocks on overlapped reads, so it costs
/// nothing while nothing is happening, and with no matching device present it opens no
/// handles and starts no threads at all.
/// </summary>
internal sealed class HeadsetWatcher : IDisposable
{
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    private List<HeadsetProfile> _profiles = HeadsetProfile.Resolve(Array.Empty<HeadsetProfile>());
    private bool _disposed;

    /// <summary>
    /// Raised on a background thread with the adapter name of the headset that just powered
    /// down, e.g. "Arctis Nova Pro Wireless".
    /// </summary>
    public event Action<string>? HeadsetPoweredOff;

    /// <summary>Raised on a background thread when the headset comes back.</summary>
    public event Action<string>? HeadsetPoweredOn;

    public bool IsWatching
    {
        get { lock (_gate) return _entries.Count > 0; }
    }

    /// <summary>Names of the headsets currently being watched, for display in settings.</summary>
    public List<string> WatchedHeadsets
    {
        get { lock (_gate) return _entries.Select(e => e.Profile.Name).Distinct().ToList(); }
    }

    /// <summary>
    /// Adapter names known to be powered down right now.
    ///
    /// Only headsets we have actually seen switch off appear here. A headset whose state has
    /// never been observed - because it was already off when CommPanel started, and these
    /// base stations report only on change - is deliberately absent rather than guessed at,
    /// since marking a working device "offline" is worse than saying nothing.
    /// </summary>
    public HashSet<string> PoweredOffAdapters()
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.LastKnownState == false)
                .Select(e => e.Profile.AdapterMatch)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Replaces the profile set, e.g. after the user learns a new headset.</summary>
    public void SetProfiles(IEnumerable<HeadsetProfile> learned)
    {
        lock (_gate) _profiles = HeadsetProfile.Resolve(learned);
    }

    /// <summary>
    /// Opens any device a profile matches. Safe to call repeatedly - devices already open are
    /// left alone - so it can be driven off endpoint change notifications rather than a timer.
    /// </summary>
    public void Rescan()
    {
        if (_disposed) return;

        lock (_gate)
        {
            _entries.RemoveAll(entry =>
            {
                if (entry.Reader.IsAlive) return false;
                entry.Reader.Dispose();
                entry.QueryReader?.Dispose();
                return true;
            });

            if (_profiles.Count == 0) return;

            foreach (var device in HidDevices.Enumerate())
            {
                var profile = _profiles.FirstOrDefault(p => p.Matches(device));
                if (profile is null) continue;

                if (_entries.Any(e => string.Equals(e.Reader.Device.Path, device.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var entry = new Entry(profile);
                entry.Reader = new HidReportReader(device, entry.Handle);
                entry.Report = OnStatus;

                if (entry.Reader.Start()) _entries.Add(entry);
                else entry.Reader.Dispose();
            }

            // A second listener on the query interface, where the answer to a status request
            // arrives. Opened only for profiles that support being asked.
            foreach (var device in HidDevices.Enumerate())
            {
                var profile = _profiles.FirstOrDefault(p => p.MatchesQueryInterface(device));
                if (profile is null) continue;

                var entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Profile, profile));
                if (entry is null || entry.QueryReader is not null) continue;

                entry.QueryPath = device.Path;
                entry.QueryLength = device.InputReportLength;
                entry.QueryReader = new HidReportReader(device, entry.HandleQueryReply);

                if (!entry.QueryReader.Start())
                {
                    entry.QueryReader.Dispose();
                    entry.QueryReader = null;
                }
            }
        }
    }

    /// <summary>
    /// Asks every supported base station for its current state.
    ///
    /// This is the only thing CommPanel writes to the device, and it exists because these base
    /// stations report only on change: without asking, a headset already switched off when
    /// CommPanel started would go unnoticed. The command is the one the device's own
    /// descriptor declares, sent on a handle that is opened and closed around the call.
    /// </summary>
    public void Query()
    {
        if (_disposed) return;

        List<Entry> entries;
        lock (_gate) entries = _entries.Where(e => e.QueryPath is not null).ToList();

        foreach (var entry in entries)
        {
            try { HidOutput.Send(entry.QueryPath!, entry.Profile.BuildQuery(entry.QueryLength)); }
            catch { /* the device may have gone; the next rescan will sort it out */ }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                entry.Reader.Dispose();
                entry.QueryReader?.Dispose();
            }
            _entries.Clear();
        }
    }

    private void OnStatus(HeadsetProfile profile, bool poweredOn)
    {
        if (poweredOn) HeadsetPoweredOn?.Invoke(profile.AdapterMatch);
        else HeadsetPoweredOff?.Invoke(profile.AdapterMatch);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>One open interface, its profile, and the last state it reported.</summary>
    private sealed class Entry
    {
        public Entry(HeadsetProfile profile) => Profile = profile;

        public HeadsetProfile Profile { get; }
        public HidReportReader Reader { get; set; } = null!;
        public Action<HeadsetProfile, bool>? Report { get; set; }

        /// <summary>Interface that accepts a status query, when the profile supports one.</summary>
        public HidReportReader? QueryReader { get; set; }
        public string? QueryPath { get; set; }
        public int QueryLength { get; set; }

        /// <summary>Handles the reply to a status query, which uses its own report format.</summary>
        public void HandleQueryReply(HidDeviceInfo device, byte[] buffer, int length)
        {
            bool? state = Profile.ReadQueryState(buffer, length);
            if (state is null) return;

            if (LastKnownState == state) return;
            StateBox = state.Value;

            Report?.Invoke(Profile, state.Value);
        }

        /// <summary>Null until a report has actually been seen for this device.</summary>
        public volatile object? StateBox;

        public bool? LastKnownState => StateBox as bool?;

        public void Handle(HidDeviceInfo device, byte[] buffer, int length)
        {
            bool? state = Profile.ReadState(buffer, length);
            if (state is null) return;

            // These base stations repeat their status; only transitions are worth reporting.
            if (LastKnownState == state) return;
            StateBox = state.Value;

            Report?.Invoke(Profile, state.Value);
        }
    }
}
