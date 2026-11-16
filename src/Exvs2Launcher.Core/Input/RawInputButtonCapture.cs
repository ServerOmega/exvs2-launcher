using System.Runtime.InteropServices;

namespace Exvs2Launcher.Core.Input;

/// <summary>Optional Raw Input listener used to capture HID button presses (for example a PS4-mode fight stick).</summary>
public interface IControllerButtonCapture
{
    bool StartListening(IntPtr hwnd);
    void StopListening();
    bool ProcessRawInput(IntPtr lParam);
    IReadOnlyList<int> CachedButtons(ConnectedController device);
    int? LastGameButton { get; }
    string? LastButtonLabel { get; }
}

/// <summary>
/// Decodes WM_INPUT HID reports with <c>HidP_GetUsages</c> and maps button usages to the
/// 1-based ids stored in the game's INI. Used when XInput or WinMM do not report presses
/// (common for a Razer Panthera in PS4 / DirectInput mode).
/// </summary>
public sealed class RawInputButtonCapture : IControllerButtonCapture, IDisposable
{
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsagePageButton = 0x09;
    private const ushort UsageJoystick = 0x04;
    private const ushort UsageGamepad = 0x05;
    private const uint RimTypeHid = 2;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceDecode> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, string> _pathByHandle = new();
    private IntPtr _hwnd;
    private bool _registered;

    public int? LastGameButton { get; private set; }
    public string? LastButtonLabel { get; private set; }

    public bool StartListening(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        StopListening();
        _hwnd = hwnd;
        var devices = new RawInputDevice[]
        {
            new() { UsagePage = UsagePageGenericDesktop, Usage = UsageJoystick, Flags = RidevInputSink, Target = hwnd },
            new() { UsagePage = UsagePageGenericDesktop, Usage = UsageGamepad, Flags = RidevInputSink, Target = hwnd },
        };
        _registered = RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RawInputDevice>());
        return _registered;
    }

    public void StopListening()
    {
        if (_registered && OperatingSystem.IsWindows())
        {
            var devices = new RawInputDevice[]
            {
                new() { UsagePage = UsagePageGenericDesktop, Usage = UsageJoystick, Flags = RidevRemove, Target = IntPtr.Zero },
                new() { UsagePage = UsagePageGenericDesktop, Usage = UsageGamepad, Flags = RidevRemove, Target = IntPtr.Zero },
            };
            try { RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RawInputDevice>()); }
            catch (Exception) { }
        }
        _registered = false;
        _hwnd = IntPtr.Zero;
        lock (_gate)
        {
            foreach (var d in _byPath.Values) d.Dispose();
            _byPath.Clear();
            _pathByHandle.Clear();
        }
    }

    public void Dispose() => StopListening();

    public IReadOnlyList<int> CachedButtons(ConnectedController device)
    {
        if (device is null) return Array.Empty<int>();
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(device.DevicePath))
            {
                foreach (var kv in _byPath)
                {
                    if (DevicePathComparer.Equals(kv.Key, device.DevicePath))
                        return kv.Value.LastButtons;
                }
            }
        }
        return Array.Empty<int>();
    }

    public bool ProcessRawInput(IntPtr lParam)
    {
        if (!OperatingSystem.IsWindows() || lParam == IntPtr.Zero) return false;
        try
        {
            if (!TryReadRawHid(lParam, out var headerDevice, out var report)) return false;
            var path = PathFor(headerDevice);
            if (string.IsNullOrWhiteSpace(path) || report.Length == 0) return false;

            DeviceDecode? decode;
            lock (_gate)
            {
                if (!_byPath.TryGetValue(path, out decode))
                {
                    decode = DeviceDecode.Open(headerDevice, path);
                    if (decode is null) return false;
                    _byPath[path] = decode;
                }
            }

            var buttons = decode.Decode(report);
            lock (_gate) decode.LastButtons = buttons;
            if (buttons.Count > 0)
            {
                LastGameButton = buttons[0];
                LastButtonLabel = buttons.Count == 1
                    ? "button " + buttons[0]
                    : "buttons " + string.Join(", ", buttons);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string? PathFor(IntPtr handle)
    {
        lock (_gate)
        {
            if (_pathByHandle.TryGetValue(handle, out var cached)) return cached;
        }
        var path = GetRawDeviceName(handle);
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_gate) _pathByHandle[handle] = path;
        return path;
    }

    private static bool TryReadRawHid(IntPtr lParam, out IntPtr device, out byte[] report)
    {
        device = IntPtr.Zero;
        report = Array.Empty<byte>();
        uint size = 0;
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, HeaderSize) != 0 || size == 0)
            return false;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            var written = GetRawInputData(lParam, RidInput, buf, ref size, HeaderSize);
            if (written == uint.MaxValue || written == 0) return false;
            int type = Marshal.ReadInt32(buf, 0);
            if (type != RimTypeHid) return false;
            device = Marshal.ReadIntPtr(buf, 8);
            int hidOffset = (int)HeaderSize;
            int sizeHid = Marshal.ReadInt32(buf, hidOffset);
            int count = Marshal.ReadInt32(buf, hidOffset + 4);
            if (sizeHid <= 0 || count <= 0) return false;
            report = new byte[sizeHid];
            Marshal.Copy(buf + hidOffset + 8, report, 0, sizeHid);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? GetRawDeviceName(IntPtr handle)
    {
        uint size = 0;
        GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            if (GetRawInputDeviceInfo(handle, RidiDeviceName, buf, ref size) == uint.MaxValue) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static uint HeaderSize => (uint)(IntPtr.Size == 8 ? 24 : 16);

    private sealed class DeviceDecode : IDisposable
    {
        private readonly IntPtr _preparsed;
        private readonly HidButtonMap _map;
        public IReadOnlyList<int> LastButtons { get; set; } = Array.Empty<int>();

        private DeviceDecode(IntPtr preparsed, HidButtonMap map)
        {
            _preparsed = preparsed;
            _map = map;
        }

        public static DeviceDecode? Open(IntPtr rawHandle, string path)
        {
            var preparsed = CopyPreparsed(rawHandle);
            if (preparsed == IntPtr.Zero) return null;
            try
            {
                var map = ReadMap(preparsed) ?? new HidButtonMap(Array.Empty<ushort>());
                return new DeviceDecode(preparsed, map);
            }
            catch
            {
                Marshal.FreeHGlobal(preparsed);
                throw;
            }
        }

        public IReadOnlyList<int> Decode(byte[] report)
        {
            if (_preparsed == IntPtr.Zero || report.Length == 0) return Array.Empty<int>();
            uint max = HidP_MaxUsageListLength(HidpInput, UsagePageButton, _preparsed);
            if (max == 0) max = 64;
            var usages = new short[max];
            uint length = max;
            int status = HidP_GetUsages(HidpInput, UsagePageButton, 0, usages, ref length, _preparsed, report, (uint)report.Length);
            if (status != HidpStatusSuccess) return Array.Empty<int>();
            var pressed = new List<ushort>();
            for (int i = 0; i < length && i < usages.Length; i++)
            {
                if (usages[i] > 0) pressed.Add((ushort)usages[i]);
            }
            if (_map.UsagesInGameOrder.Count > 0) return _map.ToGameIds(pressed);
            return HidButtonMap.FallbackGameIds(pressed);
        }

        public void Dispose()
        {
            if (_preparsed != IntPtr.Zero) Marshal.FreeHGlobal(_preparsed);
        }

        private static HidButtonMap? ReadMap(IntPtr preparsed)
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess) return null;
            ushort count = caps.NumberInputButtonCaps;
            if (count == 0) return null;
            var buttonCaps = new HidpButtonCaps[count];
            if (HidP_GetButtonCaps(HidpInput, buttonCaps, ref count, preparsed) != HidpStatusSuccess) return null;
            var list = new List<HidButtonCap>();
            for (int i = 0; i < count && i < buttonCaps.Length; i++)
            {
                var c = buttonCaps[i];
                if (c.UsagePage != UsagePageButton) continue;
                list.Add(new HidButtonCap(c.UsageMin, c.UsageMax, c.DataIndexMin, c.IsRange != 0));
            }
            return list.Count == 0 ? null : HidButtonMap.FromCaps(list);
        }

        private static IntPtr CopyPreparsed(IntPtr rawHandle)
        {
            uint size = 0;
            GetRawInputDeviceInfo(rawHandle, RidiPreparsedData, IntPtr.Zero, ref size);
            if (size == 0) return IntPtr.Zero;
            var buf = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(rawHandle, RidiPreparsedData, buf, ref size) == uint.MaxValue)
            {
                Marshal.FreeHGlobal(buf);
                return IntPtr.Zero;
            }
            return buf;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage, UsagePage;
            public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            public ushort R0, R1, R2, R3, R4, R5, R6, R7, R8, R9, R10, R11, R12, R13, R14, R15, R16;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Explicit, Size = 72)]
        private struct HidpButtonCaps
        {
            [FieldOffset(0)] public ushort UsagePage;
            [FieldOffset(2)] public byte ReportID;
            [FieldOffset(3)] public byte IsAlias;
            [FieldOffset(4)] public ushort BitField;
            [FieldOffset(6)] public ushort LinkCollection;
            [FieldOffset(8)] public ushort LinkUsage;
            [FieldOffset(10)] public ushort LinkUsagePage;
            [FieldOffset(12)] public byte IsRange;
            [FieldOffset(13)] public byte IsStringRange;
            [FieldOffset(14)] public byte IsDesignatorRange;
            [FieldOffset(15)] public byte IsAbsolute;
            [FieldOffset(56)] public ushort UsageMin;
            [FieldOffset(58)] public ushort UsageMax;
            [FieldOffset(68)] public ushort DataIndexMin;
            [FieldOffset(70)] public ushort DataIndexMax;
        }

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("hid.dll")]
        private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] short[] usageList, ref uint usageLength, IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetButtonCaps(int reportType, [In, Out] HidpButtonCaps[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] pRawInputDevices, int uiNumDevices, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
