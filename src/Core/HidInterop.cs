using System.Runtime.InteropServices;
using System.Text;

namespace CommPanel.Core;

/// <summary>One HID interface exposed by a device.</summary>
internal sealed class HidDeviceInfo
{
    public required string Path { get; init; }
    public required ushort VendorId { get; init; }
    public required ushort ProductId { get; init; }
    public required ushort UsagePage { get; init; }
    public required ushort Usage { get; init; }
    public required int InputReportLength { get; init; }

    /// <summary>The device's own product string, when it reports one.</summary>
    public string? ProductName { get; init; }

    /// <summary>Vendor-defined pages are where status like headset power lives.</summary>
    public bool IsVendorDefined => UsagePage >= 0xFF00;

    public override string ToString() =>
        string.Format("{0:X4}:{1:X4} page {2:X4}  {3}", VendorId, ProductId, UsagePage,
                      ProductName ?? "(unnamed)");
}

internal static class HidDevices
{
    /// <summary>
    /// Lists every HID interface currently present. Each is opened with no access rights,
    /// which is enough to read its attributes and never blocks an application that already
    /// has the device open.
    /// </summary>
    public static List<HidDeviceInfo> Enumerate()
    {
        var found = new List<HidDeviceInfo>();

        NativeHid.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = NativeHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            NativeHid.DigcfPresent | NativeHid.DigcfDeviceInterface);
        if (set == NativeHid.InvalidHandle) return found;

        try
        {
            var data = new NativeHid.SpDeviceInterfaceData
            {
                cbSize = Marshal.SizeOf<NativeHid.SpDeviceInterfaceData>()
            };

            for (uint index = 0;
                 NativeHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data);
                 index++)
            {
                NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrEmpty(path)) continue;

                    var info = Describe(path);
                    if (info is not null) found.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            NativeHid.SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static HidDeviceInfo? Describe(string path)
    {
        IntPtr handle = NativeHid.CreateFile(path, 0, NativeHid.FileShareRead | NativeHid.FileShareWrite,
            IntPtr.Zero, NativeHid.OpenExisting, 0, IntPtr.Zero);
        if (handle == NativeHid.InvalidHandle) return null;

        try
        {
            var attributes = new NativeHid.HiddAttributes { Size = Marshal.SizeOf<NativeHid.HiddAttributes>() };
            if (!NativeHid.HidD_GetAttributes(handle, ref attributes)) return null;

            if (!NativeHid.HidD_GetPreparsedData(handle, out IntPtr preparsed)) return null;
            try
            {
                if (NativeHid.HidP_GetCaps(preparsed, out NativeHid.HidpCaps caps) != NativeHid.HidpStatusSuccess)
                    return null;

                var product = new StringBuilder(128);
                string? productName = NativeHid.HidD_GetProductString(handle, product, product.Capacity * 2)
                    ? product.ToString()
                    : null;

                return new HidDeviceInfo
                {
                    Path = path,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InputReportLength = caps.InputReportByteLength,
                    ProductName = string.IsNullOrWhiteSpace(productName) ? null : productName.Trim()
                };
            }
            finally
            {
                NativeHid.HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            NativeHid.CloseHandle(handle);
        }
    }
}

/// <summary>
/// Reads input reports from one HID interface on a dedicated thread.
///
/// The thread blocks on an overlapped read, so it consumes nothing at all until the device
/// has something to say - there is no polling here. Reading only: nothing is ever written
/// to the device, so this cannot alter a hardware setting.
/// </summary>
internal sealed class HidReportReader : IDisposable
{
    private readonly Action<HidDeviceInfo, byte[], int> _onReport;

    private IntPtr _device = NativeHid.InvalidHandle;
    private IntPtr _readEvent = IntPtr.Zero;
    private IntPtr _stopEvent = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>
    /// Set once a read is actually outstanding on the device.
    ///
    /// Starting the thread is not the same as listening: until the first ReadFile has been
    /// issued, a report the device sends is simply lost. That matters for the status query,
    /// which is written immediately after the reader is created and would otherwise race it.
    /// </summary>
    private readonly ManualResetEventSlim _listening = new(false);

    public HidReportReader(HidDeviceInfo device, Action<HidDeviceInfo, byte[], int> onReport)
    {
        Device = device;
        _onReport = onReport;
    }

    public HidDeviceInfo Device { get; }

    public bool IsAlive => _thread is { IsAlive: true };

    public bool Start()
    {
        _device = NativeHid.CreateFile(Device.Path, NativeHid.GenericRead,
            NativeHid.FileShareRead | NativeHid.FileShareWrite,
            IntPtr.Zero, NativeHid.OpenExisting, NativeHid.FileFlagOverlapped, IntPtr.Zero);
        if (_device == NativeHid.InvalidHandle) return false;

        _readEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, null);
        _stopEvent = NativeHid.CreateEvent(IntPtr.Zero, true, false, null);
        if (_readEvent == IntPtr.Zero || _stopEvent == IntPtr.Zero) return false;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CommPanel HID reader",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();

        // Do not report success until a read is genuinely outstanding, so a caller that
        // writes to the device straight afterwards cannot beat the listener to it.
        _listening.Wait(500);
        return true;
    }

    private void Run()
    {
        int length = Math.Max(2, Device.InputReportLength);
        var buffer = new byte[length];

        while (!_stopping)
        {
            var overlapped = new NativeOverlapped { EventHandle = _readEvent };
            NativeHid.ResetEvent(_readEvent);

            GCHandle pin = GCHandle.Alloc(overlapped, GCHandleType.Pinned);
            try
            {
                if (!NativeHid.ReadFile(_device, buffer, (uint)buffer.Length, out uint read, pin.AddrOfPinnedObject()))
                {
                    if (Marshal.GetLastWin32Error() != NativeHid.ErrorIoPending) return;

                    _listening.Set();

                    var handles = new[] { _readEvent, _stopEvent };
                    uint signalled = NativeHid.WaitForMultipleObjects(2, handles, false, NativeHid.Infinite);
                    if (signalled != 0) return;

                    if (!NativeHid.GetOverlappedResult(_device, pin.AddrOfPinnedObject(), out read, false))
                        continue;
                }

                if (read > 0 && !_stopping) _onReport(Device, buffer, (int)read);
            }
            catch
            {
                return; // the device went away; a rescan will pick it up again
            }
            finally
            {
                pin.Free();
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;

        if (_stopEvent != IntPtr.Zero) NativeHid.SetEvent(_stopEvent);
        if (_device != NativeHid.InvalidHandle) NativeHid.CancelIoEx(_device, IntPtr.Zero);

        try { _thread?.Join(500); } catch { /* shutting down */ }

        if (_device != NativeHid.InvalidHandle) { NativeHid.CloseHandle(_device); _device = NativeHid.InvalidHandle; }
        if (_readEvent != IntPtr.Zero) { NativeHid.CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
        _listening.Dispose();
        if (_stopEvent != IntPtr.Zero) { NativeHid.CloseHandle(_stopEvent); _stopEvent = IntPtr.Zero; }
    }
}

internal static class NativeHid
{
    public const int DigcfPresent = 0x02;
    public const int DigcfDeviceInterface = 0x10;
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 1, FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;
    public const int ErrorIoPending = 997;
    public const int HidpStatusSuccess = 0x00110000;
    public const uint Infinite = 0xFFFFFFFF;
    public static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr device, ref HiddAttributes attributes);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    public static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(IntPtr device, byte[] report, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll")]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid,
        uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data,
        IntPtr detail, int detailSize, out int required, IntPtr devInfoData);

    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr file, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll")] public static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")] public static extern bool ResetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
}

internal static class HidOutput
{
    /// <summary>
    /// Sends one output report to a HID interface. Opened, written and closed immediately -
    /// CommPanel holds no writable handle on the device between queries.
    /// </summary>
    public static bool Send(string path, byte[] report)
    {
        const uint genericReadWrite = 0x80000000 | 0x40000000;

        IntPtr handle = NativeHid.CreateFile(path, genericReadWrite,
            NativeHid.FileShareRead | NativeHid.FileShareWrite,
            IntPtr.Zero, NativeHid.OpenExisting, 0, IntPtr.Zero);
        if (handle == NativeHid.InvalidHandle) return false;

        try { return NativeHid.HidD_SetOutputReport(handle, report, report.Length); }
        catch { return false; }
        finally { NativeHid.CloseHandle(handle); }
    }
}
