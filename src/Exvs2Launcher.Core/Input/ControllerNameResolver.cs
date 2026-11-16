namespace Exvs2Launcher.Core.Input;

/// <summary>Raw name strings collected from HID, SetupAPI, WinMM, or XInput for one device.</summary>
public sealed class ControllerNameSources
{
    public string? HidProduct { get; init; }
    public string? Manufacturer { get; init; }
    public string? BusReportedDescription { get; init; }
    public string? FriendlyName { get; init; }
    public string? DeviceDescription { get; init; }
    public string? WinmmName { get; init; }
}

/// <summary>A display name for the UI and the INI-compatible <c>Name=</c> value the game/hook expects.</summary>
public readonly record struct ControllerNames(string DisplayName, string StoredName);

/// <summary>
/// Picks a human-readable product name and a game-compatible stored name.
/// SnowLauncher and the hook store DirectInput-style names such as
/// <c>Razer Panthera PS4</c> or <c>Controller (Xbox One For Windows)</c>.
/// The UI can show a shorter HID product string such as <c>Razer Panthera</c>
/// while still writing that stored name.
/// </summary>
public static class ControllerNameResolver
{
    public static bool IsGenericName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var t = name.Trim();
        if (t.StartsWith("XInput pad", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("XInput Controller", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("Microsoft PC-joystick driver", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("USB Input Device", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("USB Composite Device", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("Generic USB Joystick", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("HID Controller", StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikeNumberedJoystick(t)) return true;
        return false;
    }

    public static ControllerNames Resolve(ControllerNameSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var hid = Clean(sources.HidProduct);
        var bus = Clean(sources.BusReportedDescription);
        var friendly = Clean(sources.FriendlyName);
        var winmm = Clean(sources.WinmmName);
        var desc = Clean(sources.DeviceDescription);

        var display = FirstSpecific(hid, bus, friendly, winmm, desc)
                      ?? FirstAny(hid, bus, friendly, winmm, desc)
                      ?? "Controller";

        // The hook matches the DirectInput / WinMM product name when one exists
        // (observed: "Razer Panthera PS4", "Controller (Xbox One For Windows)").
        var stored = FirstSpecific(winmm, hid, bus, friendly, desc) ?? display;
        return new ControllerNames(display, stored);
    }

    public static bool NamesLikelySame(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return na == nb || na.Contains(nb) || nb.Contains(na);
    }

    private static string? FirstSpecific(params string?[] names)
    {
        foreach (var n in names)
            if (!IsGenericName(n)) return n!.Trim();
        return null;
    }

    private static string? FirstAny(params string?[] names)
    {
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
        return null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim().Trim('\0');
        return t.Length == 0 ? null : t;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Trim().ToLowerInvariant().Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private static bool LooksLikeNumberedJoystick(string t)
    {
        if (!t.StartsWith("Joystick ", StringComparison.OrdinalIgnoreCase)) return false;
        return t.Length > 9 && t[9..].All(char.IsDigit);
    }
}

/// <summary>A HID / SetupAPI game controller discovered on this PC.</summary>
public sealed class HidControllerInfo
{
    public string? Product { get; init; }
    public string? Manufacturer { get; init; }
    public string? BusReportedDescription { get; init; }
    public string? FriendlyName { get; init; }
    public string? DeviceDescription { get; init; }
    public string Path { get; init; } = "";
    public int? VendorId { get; init; }
    public int? ProductId { get; init; }
    public bool IsXInputFilter { get; init; }
    public int? XInputInterfaceIndex { get; init; }
    public int ButtonCount { get; init; }
}

/// <summary>A device visible through the WinMM joystick API (DirectInput-style).</summary>
public sealed class WinmmControllerInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int ButtonCount { get; init; }
}

/// <summary>An occupied XInput user slot (0–3).</summary>
public sealed class XInputSlotInfo
{
    public int Slot { get; init; }
    public int ButtonCount { get; init; }
}

/// <summary>
/// Merges HID / SetupAPI, WinMM, and XInput enumerations into one list, attaching
/// product names and the identifiers needed to read buttons and write <c>Name=</c>.
/// </summary>
public static class ControllerInventory
{
    public static IReadOnlyList<ConnectedController> Merge(
        IEnumerable<HidControllerInfo>? hidDevices,
        IEnumerable<WinmmControllerInfo>? winmmDevices,
        IEnumerable<XInputSlotInfo>? xinputSlots)
    {
        var hid = (hidDevices ?? Array.Empty<HidControllerInfo>()).ToList();
        var winmm = (winmmDevices ?? Array.Empty<WinmmControllerInfo>()).ToList();
        var xinput = (xinputSlots ?? Array.Empty<XInputSlotInfo>()).ToList();

        var usedWinmm = new HashSet<int>();
        var usedXinput = new HashSet<int>();
        var usedHid = new HashSet<int>();
        var result = new List<ConnectedController>();

        for (int i = 0; i < hid.Count; i++)
        {
            var h = hid[i];
            var wm = FindWinmm(h, winmm, usedWinmm);
            if (wm is not null) usedWinmm.Add(wm.Id);
            usedHid.Add(i);
            result.Add(Build(h, wm, xinputSlot: null));
        }

        foreach (var wm in winmm)
        {
            if (usedWinmm.Contains(wm.Id)) continue;
            usedWinmm.Add(wm.Id);
            result.Add(Build(hid: null, wm, xinputSlot: null));
        }

        AssignXInputSlots(result, hid, xinput, usedXinput);

        foreach (var slot in xinput)
        {
            if (usedXinput.Contains(slot.Slot)) continue;
            usedXinput.Add(slot.Slot);
            result.Add(Build(hid: null, wm: null, slot));
        }

        return result;
    }

    private static void AssignXInputSlots(
        List<ConnectedController> result,
        List<HidControllerInfo> hid,
        List<XInputSlotInfo> xinput,
        HashSet<int> usedXinput)
    {
        if (xinput.Count == 0) return;

        // Prefer an IG_xx index that matches the XInput slot when it is unique.
        foreach (var slot in xinput.OrderBy(s => s.Slot))
        {
            int index = result.FindIndex(c =>
                c.XInputIndex is null
                && hid.FirstOrDefault(h => PathsEqual(h.Path, c.DevicePath)) is { IsXInputFilter: true } h
                && h.XInputInterfaceIndex == slot.Slot);
            if (index < 0) continue;
            result[index] = WithXInput(result[index], slot);
            usedXinput.Add(slot.Slot);
        }

        var unmatchedIg = result
            .Select((c, i) => (c, i))
            .Where(t => t.c.XInputIndex is null && hid.Any(h => PathsEqual(h.Path, t.c.DevicePath) && h.IsXInputFilter))
            .Select(t => t.i)
            .ToList();
        var unmatchedSlots = xinput.Where(s => !usedXinput.Contains(s.Slot)).OrderBy(s => s.Slot).ToList();
        int pair = Math.Min(unmatchedIg.Count, unmatchedSlots.Count);
        for (int n = 0; n < pair; n++)
        {
            result[unmatchedIg[n]] = WithXInput(result[unmatchedIg[n]], unmatchedSlots[n]);
            usedXinput.Add(unmatchedSlots[n].Slot);
        }

        // A leftover XInput slot may already be represented as a WinMM Xbox pad with no HID path.
        foreach (var slot in xinput.Where(s => !usedXinput.Contains(s.Slot)))
        {
            int index = result.FindIndex(c =>
                c.XInputIndex is null
                && c.WinmmId is not null
                && LooksLikeXboxName(c.Name, c.StoredName));
            if (index < 0) continue;
            result[index] = WithXInput(result[index], slot);
            usedXinput.Add(slot.Slot);
        }
    }

    private static ConnectedController WithXInput(ConnectedController c, XInputSlotInfo slot) =>
        c with
        {
            XInputIndex = slot.Slot,
            ButtonCount = c.ButtonCount > 0 ? c.ButtonCount : (slot.ButtonCount == 0 ? 10 : slot.ButtonCount),
            Source = CombineSource(c.Source, "XInput slot " + slot.Slot),
        };

    private static ConnectedController Build(HidControllerInfo? hid, WinmmControllerInfo? wm, XInputSlotInfo? xinputSlot)
    {
        var names = ControllerNameResolver.Resolve(new ControllerNameSources
        {
            HidProduct = hid?.Product,
            Manufacturer = hid?.Manufacturer,
            BusReportedDescription = hid?.BusReportedDescription,
            FriendlyName = hid?.FriendlyName,
            DeviceDescription = hid?.DeviceDescription,
            WinmmName = wm?.Name,
        });

        if (xinputSlot is not null && ControllerNameResolver.IsGenericName(names.DisplayName))
            names = new ControllerNames("Xbox-style pad", names.StoredName);

        var sources = new List<string>();
        if (hid is not null) sources.Add(hid.IsXInputFilter ? "HID (XInput)" : "HID");
        if (wm is not null) sources.Add("DirectInput / WinMM id " + wm.Id);
        if (xinputSlot is not null) sources.Add("XInput slot " + xinputSlot.Slot);
        if (sources.Count == 0) sources.Add("unknown");

        int buttons = hid?.ButtonCount ?? 0;
        if (wm is not null && wm.ButtonCount > buttons) buttons = wm.ButtonCount;
        if (xinputSlot is not null && (buttons == 0 || xinputSlot.ButtonCount > buttons))
            buttons = xinputSlot.ButtonCount == 0 ? 10 : xinputSlot.ButtonCount;

        return new ConnectedController(
            Name: names.DisplayName,
            Source: string.Join(", ", sources),
            XInputIndex: xinputSlot?.Slot,
            WinmmId: wm?.Id,
            ButtonCount: buttons,
            StoredName: names.StoredName,
            DevicePath: hid?.Path,
            VendorId: hid?.VendorId,
            ProductId: hid?.ProductId);
    }

    private static WinmmControllerInfo? FindWinmm(HidControllerInfo hid, List<WinmmControllerInfo> winmm, HashSet<int> used)
    {
        var hidNames = new[] { hid.Product, hid.BusReportedDescription, hid.FriendlyName, hid.DeviceDescription };
        var named = winmm.Where(w => !used.Contains(w.Id) && hidNames.Any(n => ControllerNameResolver.NamesLikelySame(n, w.Name))).ToList();
        if (named.Count == 1) return named[0];

        var unused = winmm.Where(w => !used.Contains(w.Id)).ToList();
        if (unused.Count == 1 && winmm.Count == 1) return unused[0];
        return null;
    }

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeXboxName(string? a, string? b)
    {
        static bool One(string? s) =>
            !string.IsNullOrWhiteSpace(s)
            && (s.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
                || s.Contains("XBOX", StringComparison.OrdinalIgnoreCase)
                || s.Contains("XInput", StringComparison.OrdinalIgnoreCase));
        return One(a) || One(b);
    }

    private static string CombineSource(string existing, string extra) =>
        string.IsNullOrWhiteSpace(existing) ? extra
        : existing.Contains(extra, StringComparison.OrdinalIgnoreCase) ? existing
        : existing + ", " + extra;
}
