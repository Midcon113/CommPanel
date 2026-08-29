namespace CommPanel.Core;

/// <summary>
/// Describes how one base station reports whether its headset is powered on.
///
/// Every value here is model-specific and cannot be generalised: SteelSeries alone uses a
/// different product id for each Arctis model, and a firmware update has been known to
/// change the product id of a model that previously worked. So rather than shipping a table
/// that silently rots, profiles are data - one built in, and any number learned from the
/// user's own hardware and stored in the settings file, where they can also be shared.
/// </summary>
internal sealed class HeadsetProfile
{
    /// <summary>Display name, e.g. "Arctis Nova Pro Wireless".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Matched against an endpoint's adapter name to find which audio devices belong to
    /// this headset, e.g. "Arctis Nova Pro Wireless".
    /// </summary>
    public string AdapterMatch { get; set; } = string.Empty;

    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public int UsagePage { get; set; }

    /// <summary>Report id, i.e. byte 0 of the input report.</summary>
    public int ReportId { get; set; }

    /// <summary>Byte 1 of the report, which these devices use to tag the message kind.</summary>
    public int ReportTag { get; set; }

    /// <summary>Which byte of the report carries the power state.</summary>
    public int StatusOffset { get; set; }

    public int PoweredOnValue { get; set; }
    public int PoweredOffValue { get; set; }

    /// <summary>True when this came from the built-in table rather than being learned.</summary>
    public bool IsBuiltIn { get; set; }

    // ---- Optional status query -------------------------------------------
    //
    // These base stations report only when something changes, so a headset already switched
    // off when CommPanel starts would go unnoticed. Some models will answer a direct question,
    // which is the only way to know the state at launch. Doing so means *writing* one command
    // to the device, so it is opt-in, described here as data rather than hardcoded, and absent
    // (QueryUsagePage = 0) for any profile that has not been shown to support it.

    /// <summary>Usage page of the interface that accepts the query. 0 means unsupported.</summary>
    public int QueryUsagePage { get; set; }

    /// <summary>Report id the query is sent as, which the device's own descriptor must declare.</summary>
    public int QueryReportId { get; set; }

    /// <summary>Command byte placed after the report id.</summary>
    public int QueryCommand { get; set; }

    /// <summary>Byte 1 of the reply, identifying it as an answer to this query.</summary>
    public int QueryReplyTag { get; set; }

    /// <summary>Which byte of the reply carries the state.</summary>
    public int QueryStatusOffset { get; set; }

    /// <summary>Value meaning "powered off".</summary>
    public int QueryOffValue { get; set; }

    /// <summary>
    /// Highest value that still means "powered on". The observed byte is a battery level, so
    /// the rule is a range rather than a single value - but it is still a closed range, and
    /// anything outside it is treated as unknown rather than guessed at.
    /// </summary>
    public int QueryOnMaxValue { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsQuery => QueryUsagePage != 0 && QueryOnMaxValue > QueryOffValue;

    public bool MatchesQueryInterface(HidDeviceInfo device) =>
        SupportsQuery &&
        device.VendorId == VendorId &&
        device.ProductId == ProductId &&
        device.UsagePage == QueryUsagePage;

    /// <summary>Builds the report to send. The device descriptor must declare this report id.</summary>
    public byte[] BuildQuery(int reportLength)
    {
        var report = new byte[Math.Max(2, reportLength)];
        report[0] = (byte)QueryReportId;
        report[1] = (byte)QueryCommand;
        return report;
    }

    /// <summary>Reads the power state out of a query reply, or null if it says nothing.</summary>
    public bool? ReadQueryState(byte[] reply, int length)
    {
        if (!SupportsQuery || length <= QueryStatusOffset || QueryStatusOffset < 0) return null;
        if (reply[0] != QueryReportId) return null;
        if (QueryReplyTag >= 0 && (length < 2 || reply[1] != QueryReplyTag)) return null;

        byte value = reply[QueryStatusOffset];
        if (value == QueryOffValue) return false;
        if (value > QueryOffValue && value <= QueryOnMaxValue) return true;
        return null;
    }

    public bool Matches(HidDeviceInfo device) =>
        device.VendorId == VendorId &&
        device.ProductId == ProductId &&
        device.UsagePage == UsagePage;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable =>
        VendorId > 0 && ProductId > 0 && StatusOffset >= 0 &&
        PoweredOnValue != PoweredOffValue &&
        !string.IsNullOrWhiteSpace(AdapterMatch);

    /// <summary>Reads the power state out of a report, or null if the report says nothing.</summary>
    public bool? ReadState(byte[] report, int length)
    {
        if (length <= StatusOffset || StatusOffset < 0) return null;
        if (report[0] != ReportId) return null;
        if (ReportTag >= 0 && (length < 2 || report[1] != ReportTag)) return null;

        byte status = report[StatusOffset];
        if (status == PoweredOnValue) return true;
        if (status == PoweredOffValue) return false;

        // An unrecognised value says nothing. Guessing would move the user's audio for no
        // reason, which is worse than not reacting.
        return null;
    }

    public string Describe() =>
        string.Format("{0}  [{1:X4}:{2:X4} page {3:X4}, report {4:X2}/{5:X2}, byte {6}: on={7:X2} off={8:X2}]",
            Name, VendorId, ProductId, UsagePage, ReportId, ReportTag,
            StatusOffset, PoweredOnValue, PoweredOffValue);

    /// <summary>
    /// Profiles verified against real hardware. A learned profile for the same interface
    /// takes precedence, so a firmware change that alters the report format can be fixed by
    /// re-learning rather than waiting for a new build.
    /// </summary>
    public static IEnumerable<HeadsetProfile> BuiltIn()
    {
        // Verified on an Arctis Nova Pro Wireless base station: with the headset on it
        // reports 07-B7-07-08-08, and with it off 07-B7-00-08-01.
        yield return new HeadsetProfile
        {
            Name = "SteelSeries Arctis Nova Pro Wireless",
            AdapterMatch = "Arctis Nova Pro Wireless",
            VendorId = 0x1038,
            ProductId = 0x12E0,
            UsagePage = 0xFF00,
            ReportId = 0x07,
            ReportTag = 0xB7,
            StatusOffset = 4,
            PoweredOnValue = 0x08,
            PoweredOffValue = 0x01,

            // Verified by asking the base station directly: with the headset off the reply
            // was 06-B0-01-00-01-00-00-08 and with it on 06-B0-01-00-01-00-06-08. Byte 6 is a
            // battery level, which reads zero only when the headset is not there.
            QueryUsagePage = 0xFFC0,
            QueryReportId = 0x06,
            QueryCommand = 0xB0,
            QueryReplyTag = 0xB0,
            QueryStatusOffset = 6,
            QueryOffValue = 0x00,
            QueryOnMaxValue = 0x08,

            IsBuiltIn = true
        };
    }

    /// <summary>
    /// Learned profiles first, so a user's own capture overrides a stale built-in for the
    /// same interface.
    /// </summary>
    public static List<HeadsetProfile> Resolve(IEnumerable<HeadsetProfile> learned)
    {
        var result = learned.Where(p => p.IsUsable).ToList();

        foreach (var builtIn in BuiltIn())
        {
            int index = result.FindIndex(p =>
                p.VendorId == builtIn.VendorId &&
                p.ProductId == builtIn.ProductId &&
                p.UsagePage == builtIn.UsagePage);

            if (index < 0)
            {
                result.Add(builtIn);
                continue;
            }

            // A learned profile overrides how status reports are *read*, which is a separate
            // matter from whether the model will answer a direct question. Overriding one
            // must not silently discard the other - a profile learned before the query
            // existed would otherwise permanently lose it.
            if (!result[index].SupportsQuery && builtIn.SupportsQuery)
                result[index] = result[index].WithQueryFrom(builtIn);
        }

        return result;
    }

    /// <summary>
    /// A copy of this profile carrying another's query capability. Returns a copy rather than
    /// mutating, because these objects belong to the user's saved settings.
    /// </summary>
    public HeadsetProfile WithQueryFrom(HeadsetProfile source) => new()
    {
        Name = Name,
        AdapterMatch = AdapterMatch,
        VendorId = VendorId,
        ProductId = ProductId,
        UsagePage = UsagePage,
        ReportId = ReportId,
        ReportTag = ReportTag,
        StatusOffset = StatusOffset,
        PoweredOnValue = PoweredOnValue,
        PoweredOffValue = PoweredOffValue,
        IsBuiltIn = IsBuiltIn,

        QueryUsagePage = source.QueryUsagePage,
        QueryReportId = source.QueryReportId,
        QueryCommand = source.QueryCommand,
        QueryReplyTag = source.QueryReplyTag,
        QueryStatusOffset = source.QueryStatusOffset,
        QueryOffValue = source.QueryOffValue,
        QueryOnMaxValue = source.QueryOnMaxValue
    };
}
