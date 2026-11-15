using System.Runtime.InteropServices;

namespace Exvs2Launcher.Core.Input;

/// <summary>A connected pad or fight stick visible through XInput or the WinMM joystick API.</summary>
public sealed record ConnectedController(
    string Name,
    string Source,
    int? XInputIndex,
    int? WinmmId,
    int ButtonCount)
{
    public string Display =>
        string.IsNullOrWhiteSpace(Name) ? Source : Name + "  (" + Source + ")";
}

public interface IControllerDeviceSource
{
    IReadOnlyList<ConnectedController> List();
    /// <summary>1-based button numbers currently held down on this device, or an empty list.</summary>
    IReadOnlyList<int> PressedButtons(ConnectedController device);
}

/// <summary>
/// Dependency-free device list: XInput slots 0–3 via <c>xinput1_4.dll</c>, and DirectInput-style
/// devices (including many fight sticks such as a Razer Panthera in PS4 mode) via <c>winmm.dll</c>.
/// This is not a new input driver — it only lists devices and reads button numbers so the launcher
/// can write the game's existing INI keys. Returns nothing on non-Windows systems.
/// </summary>
public sealed class WindowsControllerDevices : IControllerDeviceSource
{
    public IReadOnlyList<ConnectedController> List()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ConnectedController>();
        var list = new List<ConnectedController>();
        try { list.AddRange(ListXInput()); }
        catch (Exception) { }
        try { list.AddRange(ListWinmm()); }
        catch (Exception) { }
        return list;
    }

    public IReadOnlyList<int> PressedButtons(ConnectedController device)
    {
        if (!OperatingSystem.IsWindows() || device is null) return Array.Empty<int>();
        try
        {
            if (device.XInputIndex is int slot)
                return PressedXInput(slot);
            if (device.WinmmId is int joy)
                return PressedWinmm(joy);
        }
        catch (Exception) { }
        return Array.Empty<int>();
    }

    private static IEnumerable<ConnectedController> ListXInput()
    {
        for (int i = 0; i < 4; i++)
        {
            if (XInputGetCapabilities(i, 0, out var caps) != 0) continue;
            int buttons = CountBits(caps.Gamepad.wButtons);
            yield return new ConnectedController(
                "XInput pad " + i,
                "XInput slot " + i,
                XInputIndex: i,
                WinmmId: null,
                ButtonCount: buttons == 0 ? 10 : buttons);
        }
    }

    private static IEnumerable<ConnectedController> ListWinmm()
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
            yield return new ConnectedController(
                name,
                "DirectInput / WinMM id " + i,
                XInputIndex: null,
                WinmmId: i,
                ButtonCount: (int)caps.wNumButtons);
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
}
