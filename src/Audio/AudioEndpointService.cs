using System.Runtime.InteropServices;

namespace CommPanel.Audio;

/// <summary>
/// Thin wrapper over the Core Audio MMDevice API: enumerates endpoints, reports which are
/// default, and switches the default endpoint via IPolicyConfig.
///
/// Everything here is demand-driven - there is no polling loop and no background thread.
/// Device changes arrive as COM callbacks from the Windows audio service, which is why an
/// idle CommPanel costs no CPU at all.
/// </summary>
internal sealed class AudioEndpointService : IDisposable
{
    private const int StgmRead = 0x0;

    private IMMDeviceEnumerator? _enumerator;
    private EndpointNotificationClient? _notificationClient;
    private bool _disposed;

    /// <summary>Raised on an arbitrary thread when Windows reports an endpoint change.</summary>
    public event Action? EndpointsChanged;

    public AudioEndpointService()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _notificationClient = new EndpointNotificationClient(() => EndpointsChanged?.Invoke());
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    /// <summary>Enumerates active endpoints for one direction and flags the current defaults.</summary>
    public List<AudioDevice> GetDevices(EDataFlow flow)
    {
        var result = new List<AudioDevice>();
        var enumerator = _enumerator;
        if (enumerator is null) return result;

        string? defaultId = GetDefaultId(flow, ERole.Console);
        string? commsId = GetDefaultId(flow, ERole.Communications);

        if (enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out var collection) != 0 || collection is null)
            return result;

        try
        {
            if (collection.GetCount(out int count) != 0) return result;

            for (int i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device is null) continue;
                try
                {
                    var info = Describe(device, flow);
                    if (info is null) continue;
                    info.IsDefault = string.Equals(info.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                    info.IsDefaultCommunications = string.Equals(info.Id, commsId, StringComparison.OrdinalIgnoreCase);
                    result.Add(info);
                }
                finally
                {
                    Release(device);
                }
            }
        }
        finally
        {
            Release(collection);
        }

        result.Sort(static (a, b) => string.Compare(a.ShortName, b.ShortName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public string? GetDefaultId(EDataFlow flow, ERole role)
    {
        var enumerator = _enumerator;
        if (enumerator is null) return null;

        // A failure here is normal: a machine can legitimately have no capture device.
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null)
            return null;

        try
        {
            return device.GetId(out string? id) == 0 ? id : null;
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>
    /// Makes <paramref name="deviceId"/> the default endpoint. Console and Multimedia are
    /// always set together, since Windows presents them as a single choice. Communications
    /// follows only when <paramref name="includeCommunications"/> is set, so a user who
    /// deliberately keeps voice chat on a headset does not get it yanked to the speakers.
    /// </summary>
    public bool SetDefault(string deviceId, EDataFlow flow, bool includeCommunications, out string? error)
    {
        error = null;
        object? policyObject = null;
        try
        {
            policyObject = new PolicyConfigComObject();

            int hr = SetRole(policyObject, deviceId, ERole.Console, ref error);
            if (hr == 0) hr = SetRole(policyObject, deviceId, ERole.Multimedia, ref error);
            if (hr == 0 && includeCommunications)
                hr = SetRole(policyObject, deviceId, ERole.Communications, ref error);

            if (hr != 0)
            {
                error ??= string.Format("Windows refused the change (HRESULT 0x{0:X8}).", hr);
                return false;
            }

            // Trust the endpoint, not the return code: this interface is undocumented, and a
            // success code from an unexpected vtable slot would otherwise read as a switch.
            string? actual = GetDefaultId(flow, ERole.Console);
            if (!string.Equals(actual, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                error = "Windows reported success but the default device did not change.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(policyObject);
        }
    }

    /// <summary>
    /// Calls SetDefaultEndpoint through whichever PolicyConfig IID this Windows build
    /// answers. The interface is chosen by QueryInterface alone; a call that fails is never
    /// retried against the other declaration, because a mismatched vtable would put the
    /// call on SetEndpointVisibility and hide the device rather than select it.
    /// </summary>
    private static int SetRole(object policyObject, string deviceId, ERole role, ref string? error)
    {
        if (policyObject is IPolicyConfig policy)
            return policy.SetDefaultEndpoint(deviceId, role);

        if (policyObject is IPolicyConfigLegacy legacy)
            return legacy.SetDefaultEndpoint(deviceId, role);

        error = "The Windows audio policy interface is unavailable on this system.";
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    /// <summary>
    /// Opens the meter and volume interfaces for an endpoint. Returns null when the device
    /// has gone, which is routine while devices are being switched.
    /// </summary>
    public EndpointControls? OpenControls(string deviceId)
    {
        var enumerator = _enumerator;
        if (enumerator is null) return null;

        try { return EndpointControls.Open(enumerator, deviceId); }
        catch { return null; }
    }

    /// <summary>
    /// Opens a capture stream on a microphone so its level can be metered. Returns null with
    /// a reason when the device cannot be opened - most often because Windows privacy
    /// settings block microphone access.
    /// </summary>
    public CaptureMeter? OpenCaptureMeter(string deviceId, out string? error)
    {
        error = null;
        var enumerator = _enumerator;
        if (enumerator is null) return null;

        try { return CaptureMeter.Open(enumerator, deviceId, out error); }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Opens the per-application session mixer for an endpoint - what the Windows volume
    /// mixer lists. Returns null when the device has gone.
    /// </summary>
    public SessionMixer? OpenSessionMixer(string deviceId)
    {
        var enumerator = _enumerator;
        if (enumerator is null) return null;

        try { return SessionMixer.Open(enumerator, deviceId); }
        catch { return null; }
    }

    /// <summary>Sets only the Communications role, leaving Console/Multimedia untouched.</summary>
    public bool SetDefaultCommunications(string deviceId, out string? error)
    {
        error = null;
        object? policyObject = null;
        try
        {
            policyObject = new PolicyConfigComObject();
            int hr = SetRole(policyObject, deviceId, ERole.Communications, ref error);

            if (hr != 0)
            {
                error ??= string.Format("Windows refused the change (HRESULT 0x{0:X8}).", hr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Release(policyObject);
        }
    }

    private static AudioDevice? Describe(IMMDevice device, EDataFlow flow)
    {
        if (device.GetId(out string? id) != 0 || string.IsNullOrEmpty(id)) return null;
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null) return null;

        try
        {
            string full = ReadString(store, PropertyKeys.DeviceFriendlyName) ?? "Unknown device";
            string shortName = ReadString(store, PropertyKeys.DeviceDescription) ?? full;
            string adapter = ReadString(store, PropertyKeys.DeviceInterfaceFriendlyName) ?? string.Empty;
            var formFactor = (FormFactor?)ReadInt(store, PropertyKeys.AudioEndpointFormFactor)
                             ?? FormFactor.UnknownFormFactor;

            return new AudioDevice
            {
                Id = id!,
                ShortName = shortName,
                Adapter = adapter,
                FullName = full,
                Flow = flow,
                FormFactor = formFactor
            };
        }
        finally
        {
            Release(store);
        }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            string? text = value.AsString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static int? ReadInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.AsInt32();
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); }
            catch { /* shutdown races here are harmless */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_enumerator is not null && _notificationClient is not null)
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        }
        catch { /* the audio service may already be gone during shutdown */ }

        _notificationClient = null;
        Release(_enumerator);
        _enumerator = null;
    }

    /// <summary>
    /// Receives endpoint change callbacks from the Windows audio service. Calls arrive on an
    /// MTA pool thread, so this handler never touches UI - it only raises the signal.
    /// </summary>
    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action _changed;

        public EndpointNotificationClient(Action changed) => _changed = changed;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string deviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) => _changed();

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // Volume and format churn fires constantly; only a name change matters to the panel.
            if (key.PropertyId == PropertyKeys.DeviceFriendlyName.PropertyId &&
                key.FormatId == PropertyKeys.DeviceFriendlyName.FormatId)
            {
                _changed();
            }
        }
    }
}
