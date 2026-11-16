namespace Exvs2Launcher.Core.Input;

/// <summary>One HID button capability: a single usage or a usage range, with the starting data index.</summary>
public readonly record struct HidButtonCap(ushort UsageMin, ushort UsageMax, ushort DataIndexMin, bool IsRange);

/// <summary>
/// Maps HID button usages (Usage Page 0x09) to the 1-based button ids the game stores in
/// <c>config.ini</c>. Those ids match the Windows “Set up USB game controllers” panel and
/// DirectInput (button 1 is the first usage in data-index order).
/// </summary>
/// <remarks>
/// A Razer Panthera in PS4 mode uses the standard DualShock layout, which is also what
/// SnowLauncher wrote on this PC: 1 Square, 2 Cross, 3 Circle, 4 Triangle, 5 L1, 6 R1,
/// 7 L2, 8 R2, 9 Share, 10 Options, 11 L3, 12 R3, 13 Home, 14 Trackpad. Observed keys
/// were <c>ButtonA=1</c>, <c>ButtonB=4</c>, <c>ButtonC=2</c>, <c>ButtonD=3</c>,
/// <c>ButtonAB=6</c>, <c>ButtonAC=7</c>, <c>ButtonBC=8</c>, <c>ButtonABC=12</c>,
/// <c>Start=10,11</c>, <c>Test=9</c>, <c>Coin=14</c>, <c>Card=14</c>, <c>Kill=13</c>,
/// <c>Name=Razer Panthera PS4</c>.
/// </remarks>
public sealed class HidButtonMap
{
    public IReadOnlyList<ushort> UsagesInGameOrder { get; }

    public HidButtonMap(IEnumerable<ushort> usagesInGameOrder)
    {
        var list = new List<ushort>();
        var seen = new HashSet<ushort>();
        foreach (var usage in usagesInGameOrder)
        {
            if (seen.Add(usage)) list.Add(usage);
        }
        UsagesInGameOrder = list;
    }

    /// <summary>Build a map from HID button caps, expanding ranges and sorting by data index (DirectInput order).</summary>
    public static HidButtonMap FromCaps(IEnumerable<HidButtonCap> caps)
    {
        var items = new List<(ushort Usage, int DataIndex)>();
        foreach (var cap in caps)
        {
            if (cap.IsRange && cap.UsageMax >= cap.UsageMin)
            {
                int count = cap.UsageMax - cap.UsageMin + 1;
                for (int i = 0; i < count; i++)
                    items.Add(((ushort)(cap.UsageMin + i), cap.DataIndexMin + i));
            }
            else
            {
                items.Add((cap.UsageMin, cap.DataIndexMin));
            }
        }
        return new HidButtonMap(items.OrderBy(i => i.DataIndex).ThenBy(i => i.Usage).Select(i => i.Usage));
    }

    /// <summary>
    /// When no capability list is available, use the HID usage number itself as the game id.
    /// That is correct for a contiguous 1..N pad such as a PS4-mode Panthera.
    /// </summary>
    public static IReadOnlyList<int> FallbackGameIds(IEnumerable<ushort> pressedUsages) =>
        pressedUsages
            .Where(u => u > 0)
            .Select(u => (int)u)
            .Distinct()
            .OrderBy(u => u)
            .ToArray();

    public int? ToGameId(ushort usage)
    {
        int index = -1;
        for (int i = 0; i < UsagesInGameOrder.Count; i++)
        {
            if (UsagesInGameOrder[i] == usage) { index = i; break; }
        }
        return index < 0 ? null : index + 1;
    }

    public IReadOnlyList<int> ToGameIds(IEnumerable<ushort> pressedUsages)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var usage in pressedUsages)
        {
            var id = ToGameId(usage);
            if (id is int gameId && seen.Add(gameId)) ids.Add(gameId);
        }
        ids.Sort();
        return ids;
    }
}

/// <summary>Normalizes Windows device interface paths so Raw Input and SetupAPI names can be compared.</summary>
public static class DevicePathComparer
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var t = path.Trim().Replace('/', '\\');
        if (t.StartsWith(@"\\?\", StringComparison.Ordinal)) t = t[4..];
        else if (t.StartsWith(@"\??\", StringComparison.Ordinal)) t = t[4..];
        else if (t.StartsWith(@"\\.\", StringComparison.Ordinal)) t = t[4..];
        int guid = t.IndexOf("#{");
        if (guid > 0) t = t[..guid];
        return t.TrimEnd('\\').ToUpperInvariant();
    }

    public static bool Equals(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.Length > 0 && nb.Length > 0 && na == nb;
    }
}
