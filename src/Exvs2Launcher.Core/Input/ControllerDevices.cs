using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.Input;

/// <summary>A connected pad or fight stick visible through HID, XInput, or the WinMM joystick API.</summary>
public sealed record ConnectedController(
    string Name,
    string Source,
    int? XInputIndex,
    int? WinmmId,
    int ButtonCount,
    string? StoredName = null,
    string? DevicePath = null,
    int? VendorId = null,
    int? ProductId = null)
{
    /// <summary>Value written to the game's <c>Name=</c> field. Falls back to <see cref="Name"/>.</summary>
    public string IniName => string.IsNullOrWhiteSpace(StoredName) ? Name : StoredName;

    public string Display
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Name) ? "Controller" : Name;
            if (!string.IsNullOrWhiteSpace(StoredName)
                && !StoredName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                return label + "  (INI name: " + StoredName + ")";
            return string.IsNullOrWhiteSpace(Source) ? label : label + "  (" + Source + ")";
        }
    }
}

public interface IControllerDeviceSource
{
    IReadOnlyList<ConnectedController> List();
    /// <summary>1-based button numbers currently held down on this device, or an empty list.</summary>
    IReadOnlyList<int> PressedButtons(ConnectedController device);
}

/// <summary>
/// Device list: product names from HID / SetupAPI (<c>HidD_GetProductString</c>, friendly name,
/// bus-reported description) and Raw Input, XInput slots 0–3 via <c>xinput1_4.dll</c>, and
/// DirectInput-style devices (including many fight sticks such as a Razer Panthera in PS4 mode)
/// via <c>winmm.dll</c>. This is not a new input driver — it only lists devices and reads button
/// numbers so the launcher can write the game's existing INI keys. Returns nothing on non-Windows systems.
/// </summary>
public sealed class WindowsControllerDevices : IControllerDeviceSource, IControllerButtonCapture
{
    private readonly RawInputButtonCapture _raw = new();

    public bool StartListening(IntPtr hwnd) => _raw.StartListening(hwnd);
    public void StopListening() => _raw.StopListening();
    public bool ProcessRawInput(IntPtr lParam) => _raw.ProcessRawInput(lParam);
    public IReadOnlyList<int> CachedButtons(ConnectedController device) => _raw.CachedButtons(device);
    public int? LastGameButton => _raw.LastGameButton;
    public string? LastButtonLabel => _raw.LastButtonLabel;

    public IReadOnlyList<ConnectedController> List()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ConnectedController>();
        IEnumerable<HidControllerInfo> hid = Array.Empty<HidControllerInfo>();
        IEnumerable<WinmmControllerInfo> winmm = Array.Empty<WinmmControllerInfo>();
        IEnumerable<XInputSlotInfo> xinput = Array.Empty<XInputSlotInfo>();
        try { hid = ListHid(); }
        catch (Exception) { }
        try { winmm = ListWinmm(); }
        catch (Exception) { }
        try { xinput = ListXInput(); }
        catch (Exception) { }
        try { return ControllerInventory.Merge(hid, winmm, xinput); }
        catch (Exception) { return Array.Empty<ConnectedController>(); }
    }

    public IReadOnlyList<int> PressedButtons(ConnectedController device)
    {
        if (!OperatingSystem.IsWindows() || device is null) return Array.Empty<int>();
        try
        {
            // XInput pads must keep the XInput button numbers (1=A … 10=RS), not HID usages.
            if (device.XInputIndex is int slot)
                return PressedXInput(slot);

            // A PS4-mode fight stick such as a Razer Panthera is often visible as HID / Raw Input
            // even when WinMM lists it but reports no buttons. Prefer a fresh Raw Input cache.
            var raw = _raw.CachedButtons(device);
            if (raw.Count > 0) return raw;

            if (device.WinmmId is int joy)
            {
                var winmm = PressedWinmm(joy);
                if (winmm.Count > 0) return winmm;
            }

            return raw;
        }
        catch (Exception) { }
        return Array.Empty<int>();
    }

    private static IEnumerable<XInputSlotInfo> ListXInput()
    {
        for (int i = 0; i < 4; i++)
        {
            if (XInputGetCapabilities(i, 0, out var caps) != 0) continue;
            int buttons = CountBits(caps.Gamepad.wButtons);
            yield return new XInputSlotInfo { Slot = i, ButtonCount = buttons == 0 ? 10 : buttons };
        }
    }

    private static IEnumerable<WinmmControllerInfo> ListWinmm()
    {
        int max = joyGetNumDevs();
        if (max <= 0) yield break;
        for (int i = 0; i < max; i++)
        {
            var caps = new JoyCaps();
            if (joyGetDevCaps(i, ref caps, Marshal.SizeOf<JoyCaps>()) != 0) continue;
            var info = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(), dwFlags = JoyAll };
            if (joyGetPosEx(i, ref info) != 0) continue;
            var name = string.IsNullOrWhiteSpace(caps.szPname) ? "Joystick " + i : caps.szPname.Trim();
            yield return new WinmmControllerInfo { Id = i, Name = name, ButtonCount = (int)caps.wNumButtons };
        }
    }

    /// <summary>
    /// XInput face/menu buttons as 1-based ids: 1=A, 2=B, 3=X, 4=Y, 5=LB, 6=RB, 7=Back, 8=Start, 9=LS, 10=RS.
    /// These are only used when binding an XInput device; a DirectInput fight stick uses WinMM button numbers.
    /// </summary>
    private static IReadOnlyList<int> PressedXInput(int slot)
    {
        if (XInputGetState(slot, out var state) != 0) return Array.Empty<int>();
        ushort w = state.Gamepad.wButtons;
        var ids = new List<int>();
        void Add(ushort mask, int id) { if ((w & mask) != 0) ids.Add(id); }
        Add(0x1000, 1); // A
        Add(0x2000, 2); // B
        Add(0x4000, 3); // X
        Add(0x8000, 4); // Y
        Add(0x0100, 5); // LB
        Add(0x0200, 6); // RB
        Add(0x0020, 7); // Back
        Add(0x0010, 8); // Start
        Add(0x0040, 9); // LS
        Add(0x0080, 10); // RS
        return ids;
    }

    private static IReadOnlyList<int> PressedWinmm(int joy)
    {
        var info = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(), dwFlags = JoyAll };
        if (joyGetPosEx(joy, ref info) != 0) return Array.Empty<int>();
        var ids = new List<int>();
        uint bits = info.dwButtons;
        for (int i = 0; i < 32; i++)
        {
            if ((bits & (1u << i)) != 0) ids.Add(i + 1);
        }
        return ids;
    }

    private static int CountBits(ushort value)
    {
        int n = 0;
        while (value != 0) { n += value & 1; value >>= 1; }
        return n;
    }

    // ---- HID / SetupAPI / Raw Input ----

    private static IEnumerable<HidControllerInfo> ListHid()
    {
        var byPath = new Dictionary<string, HidControllerInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in EnumerateSetupApiHid())
            AddHid(byPath, d);
        foreach (var d in EnumerateRawInputHid())
            AddHid(byPath, d);
        return byPath.Values;
    }

    private static void AddHid(Dictionary<string, HidControllerInfo> byPath, HidControllerInfo? device)
    {
        if (device is null || string.IsNullOrWhiteSpace(device.Path)) return;
        if (byPath.TryGetValue(device.Path, out var existing))
        {
            byPath[device.Path] = new HidControllerInfo
            {
                Product = First(existing.Product, device.Product),
                Manufacturer = First(existing.Manufacturer, device.Manufacturer),
                BusReportedDescription = First(existing.BusReportedDescription, device.BusReportedDescription),
                FriendlyName = First(existing.FriendlyName, device.FriendlyName),
                DeviceDescription = First(existing.DeviceDescription, device.DeviceDescription),
                Path = existing.Path,
                VendorId = existing.VendorId ?? device.VendorId,
                ProductId = existing.ProductId ?? device.ProductId,
                IsXInputFilter = existing.IsXInputFilter || device.IsXInputFilter,
                XInputInterfaceIndex = existing.XInputInterfaceIndex ?? device.XInputInterfaceIndex,
                ButtonCount = Math.Max(existing.ButtonCount, device.ButtonCount),
            };
            return;
        }
        byPath[device.Path] = device;
    }

    private static string? First(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static IEnumerable<HidControllerInfo> EnumerateSetupApiHid()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
        try
        {
            for (int index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface)) break;
                var info = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };
                string? path = GetInterfacePath(set, ref iface, ref info);
                if (string.IsNullOrWhiteSpace(path)) continue;

                var detail = ReadHidDetail(path);
                if (detail is null) continue;
                if (!detail.Value.Keep) continue;

                var ig = ParseIgIndex(path);
                yield return new HidControllerInfo
                {
                    Product = detail.Value.Product,
                    Manufacturer = detail.Value.Manufacturer,
                    BusReportedDescription = GetDevicePropertyString(set, ref info, DevpkeyBusReported),
                    FriendlyName = GetRegistryProperty(set, ref info, SpdrpFriendlyName),
                    DeviceDescription = GetRegistryProperty(set, ref info, SpdrpDeviceDesc),
                    Path = path,
                    VendorId = detail.Value.VendorId,
                    ProductId = detail.Value.ProductId,
                    IsXInputFilter = IsXInputPath(path),
                    XInputInterfaceIndex = ig,
                    ButtonCount = detail.Value.ButtonCount,
                };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static IEnumerable<HidControllerInfo> EnumerateRawInputHid()
    {
        uint count = 0;
        int size = Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, (uint)size) != 0 && count == 0) yield break;
        var list = new RawInputDeviceList[count];
        uint got = GetRawInputDeviceList(list, ref count, (uint)size);
        if (got == uint.MaxValue) yield break;
        for (int i = 0; i < got && i < list.Length; i++)
        {
            if (list[i].Type != RimTypeHid) continue;
            string? path = GetRawDeviceName(list[i].HDevice);
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!GetRawHidInfo(list[i].HDevice, out ushort usagePage, out ushort usage, out int vid, out int pid))
                continue;
            if (!IsGamepadUsage(usagePage, usage) && !IsXInputPath(path)) continue;

            var detail = ReadHidDetail(path);
            var ig = ParseIgIndex(path);
            yield return new HidControllerInfo
            {
                Product = detail?.Product,
                Manufacturer = detail?.Manufacturer,
                Path = path,
                VendorId = detail?.VendorId ?? (vid == 0 ? null : vid),
                ProductId = detail?.ProductId ?? (pid == 0 ? null : pid),
                IsXInputFilter = IsXInputPath(path),
                XInputInterfaceIndex = ig,
                ButtonCount = detail?.ButtonCount ?? 0,
            };
        }
    }

    private static (string? Product, string? Manufacturer, int? VendorId, int? ProductId, int ButtonCount, bool Keep)? ReadHidDetail(string path)
    {
        var handle = CreateFile(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        }
        try
        {
            string? product = HidString(handle, HidD_GetProductString);
            string? manufacturer = HidString(handle, HidD_GetManufacturerString);
            int? vid = null, pid = null;
            if (HidD_GetAttributes(handle, out var attrs))
            {
                vid = attrs.VendorID;
                pid = attrs.ProductID;
            }

            int buttons = 0;
            bool keep = IsXInputPath(path);
            if (HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
            {
                try
                {
                    if (HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess)
                    {
                        keep = keep || IsGamepadUsage(caps.UsagePage, caps.Usage);
                        buttons = caps.NumberInputButtonCaps;
                    }
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }

            if (!keep) return null;
            return (product, manufacturer, vid, pid, buttons, keep);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? HidString(IntPtr handle, HidStringFn fn)
    {
        var buffer = new byte[256];
        if (!fn(handle, buffer, buffer.Length)) return null;
        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private delegate bool HidStringFn(IntPtr handle, byte[] buffer, int length);

    private static string? GetInterfacePath(IntPtr set, ref SpDeviceInterfaceData iface, ref SpDevinfoData info)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
        if (needed <= 0) return null;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buf, needed, out _, ref info))
                return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? GetRegistryProperty(IntPtr set, ref SpDevinfoData info, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out int needed);
        if (needed <= 0) return null;
        var buf = new byte[needed];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buf, buf.Length, out _))
            return null;
        var text = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text.Split('\0')[0];
    }

    private static string? GetDevicePropertyString(IntPtr set, ref SpDevinfoData info, DevpropKey key)
    {
        var k = key;
        SetupDiGetDevicePropertyW(set, ref info, ref k, out _, null, 0, out int needed, 0);
        if (needed <= 0) return null;
        var buf = new byte[needed];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref k, out uint type, buf, buf.Length, out _, 0))
            return null;
        if (type != DevpropTypeString) return null;
        var text = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? GetRawDeviceName(IntPtr handle)
    {
        uint size = 0;
        GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            var written = GetRawInputDeviceInfo(handle, RidiDeviceName, buf, ref size);
            if (written == uint.MaxValue) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool GetRawHidInfo(IntPtr handle, out ushort usagePage, out ushort usage, out int vid, out int pid)
    {
        usagePage = 0; usage = 0; vid = 0; pid = 0;
        uint size = 0;
        GetRawInputDeviceInfo(handle, RidiDeviceInfo, IntPtr.Zero, ref size);
        if (size == 0) return false;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(buf, (int)size);
            if (GetRawInputDeviceInfo(handle, RidiDeviceInfo, buf, ref size) == uint.MaxValue) return false;
            // RID_DEVICE_INFO: cbSize (4), dwType (4), then RID_DEVICE_INFO_HID
            int type = Marshal.ReadInt32(buf, 4);
            if (type != RimTypeHid) return false;
            int offset = 8;
            vid = Marshal.ReadInt32(buf, offset);
            pid = Marshal.ReadInt32(buf, offset + 4);
            // skip version (4)
            usagePage = (ushort)Marshal.ReadInt16(buf, offset + 12);
            usage = (ushort)Marshal.ReadInt16(buf, offset + 14);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool IsGamepadUsage(ushort usagePage, ushort usage) =>
        usagePage == 0x01 && (usage is 0x04 or 0x05);

    private static bool IsXInputPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0;

    private static int? ParseIgIndex(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var m = Regex.Match(path, @"IG_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        int value = Convert.ToInt32(m.Groups[1].Value, 16);
        return value is >= 0 and <= 3 ? value : null;
    }

    // ---- XInput ----

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern int XInputGetCapabilities(int dwUserIndex, int dwFlags, out XInputCapabilities pCapabilities);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort wLeftMotorSpeed, wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type, SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    // ---- WinMM ----

    private const uint JoyAll = 0xFF;

    [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCaps(int uJoyID, ref JoyCaps pjc, int cbjc);

    [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")]
    private static extern int joyGetPosEx(int uJoyID, ref JoyInfoEx pji);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint dwSize, dwFlags;
        public uint dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV;
        public uint dwReserved1, dwReserved2;
    }

    // ---- SetupAPI / HID / Raw Input p/invoke ----

    private const int DigcfPresent = 0x2;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint SpdrpDeviceDesc = 0;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint DevpropTypeString = 18;
    private const uint RimTypeHid = 2;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const int HidpStatusSuccess = 0x00110000;

    private static readonly DevpropKey DevpkeyBusReported = new()
    {
        Fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
        Pid = 4,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DevpropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public ushort Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7, Reserved8;
        public ushort Reserved9, Reserved10, Reserved11, Reserved12, Reserved13, Reserved14, Reserved15, Reserved16;
        public ushort Reserved17;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public IntPtr HDevice;
        public uint Type;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, out HidAttributes attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property, out uint propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, ref DevpropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
