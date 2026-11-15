namespace Exvs2Launcher.Core.Input;

/// <summary>
/// One controller slot as the game hook understands it in <c>config.ini</c>
/// (<c>[controller]</c> or <c>[controller-1]</c>). Button values are 1-based numeric ids,
/// comma-separated when a key has more than one button.
/// </summary>
public sealed class ControllerSlot
{
    public string Section { get; set; } = "controller";
    public bool Enabled { get; set; }
    public string? Name { get; set; }
    /// <summary>Hook mode, for example <c>xinput-native</c>. Empty means the default (DirectInput-style) path.</summary>
    public string? Mode { get; set; }
    /// <summary>0–15 is a device index; 16 means all devices (observed <c>[xinput]</c> / <c>[controller]</c> convention).</summary>
    public int? DeviceId { get; set; }

    public string? ButtonA { get; set; }
    public string? ButtonB { get; set; }
    public string? ButtonC { get; set; }
    public string? ButtonD { get; set; }
    public string? ButtonAB { get; set; }
    public string? ButtonBC { get; set; }
    public string? ButtonAC { get; set; }
    public string? ButtonABC { get; set; }
    public string? Start { get; set; }
    public string? Coin { get; set; }
    public string? Card { get; set; }
    public string? Test { get; set; }
    public string? Service { get; set; }
    public string? Kill { get; set; }

    public string Summary
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "(no device name)" : Name.Trim();
            var on = Enabled ? "enabled" : "disabled";
            var mode = string.IsNullOrWhiteSpace(Mode) ? "" : ", " + Mode.Trim();
            return name + " (" + on + mode + ")";
        }
    }
}

/// <summary>The optional <c>[xinput]</c> section in <c>config.ini</c>.</summary>
public sealed class XInputSettings
{
    public bool? EmulatedXInputEnabled { get; set; }
    public string? Mode { get; set; }
    public int? DeviceId { get; set; }
}

/// <summary>Controller-related keys currently in a game folder (no secrets).</summary>
public sealed class ControllerFileState
{
    public ControllerSlot Controller { get; set; } = new() { Section = "controller" };
    public ControllerSlot Controller1 { get; set; } = new() { Section = "controller-1" };
    public XInputSettings XInput { get; set; } = new();
}

/// <summary>
/// Converts between SnowLauncher-style primary + combo keys and the expanded
/// <c>A=</c>/<c>B=</c>/<c>C=</c> lists the game reads in <c>config.ini</c>.
/// </summary>
public static class ControllerCombos
{
    public const int MinDeviceId = 0;
    public const int MaxDeviceId = 16;
    public const int AllDevicesId = 16;

    /// <summary>Splits a comma-separated button list into unique positive integers, keeping first-seen order.</summary>
    public static IReadOnlyList<int> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
        var list = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in value.Split(','))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            if (!int.TryParse(t, out int n) || n <= 0)
                throw new FormatException("Controller button ids must be positive integers (got '" + t + "').");
            if (seen.Add(n)) list.Add(n);
        }
        return list;
    }

    public static bool TryParseList(string? value, out IReadOnlyList<int> ids, out string? error)
    {
        ids = Array.Empty<int>();
        error = null;
        try
        {
            ids = ParseList(value);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string FormatList(IEnumerable<int> ids)
    {
        var list = new List<int>();
        var seen = new HashSet<int>();
        foreach (var n in ids)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(ids), "Button ids must be positive integers.");
            if (seen.Add(n)) list.Add(n);
        }
        return string.Join(",", list);
    }

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool TryParseDeviceId(string? value, out int? id, out string? error)
    {
        id = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!int.TryParse(value.Trim(), out int n) || n < MinDeviceId || n > MaxDeviceId)
        {
            error = "Device id must be 0-15 (one pad or stick) or 16 (all devices).";
            return false;
        }
        id = n;
        return true;
    }

    /// <summary>
    /// Rebuilds the expanded <c>A</c>/<c>B</c>/<c>C</c> lists from primary buttons and combos.
    /// Order matches observed packs: primary, then AB, AC, ABC for A; primary, AB, BC, ABC for B;
    /// primary, AC, BC, ABC for C.
    /// </summary>
    public static string ExpandA(ControllerSlot slot) =>
        FormatList(Concat(slot.ButtonA, slot.ButtonAB, slot.ButtonAC, slot.ButtonABC));

    public static string ExpandB(ControllerSlot slot) =>
        FormatList(Concat(slot.ButtonB, slot.ButtonAB, slot.ButtonBC, slot.ButtonABC));

    public static string ExpandC(ControllerSlot slot) =>
        FormatList(Concat(slot.ButtonC, slot.ButtonAC, slot.ButtonBC, slot.ButtonABC));

    public static string ExpandD(ControllerSlot slot) =>
        FormatList(ParseList(slot.ButtonD));

    /// <summary>
    /// Fills primary and combo fields from expanded <c>A</c>/<c>B</c>/<c>C</c> lists.
    /// Numbers in A∩B∩C become ABC; A∩B become AB; B∩C become BC; A∩C become AC;
    /// the rest stay on the face button. Explicit <c>ButtonA</c> (etc.) keys, when present,
    /// are used as the primary list and are removed from the combo leftovers.
    /// </summary>
    public static void FillFromExpanded(
        ControllerSlot slot,
        string? expandedA,
        string? expandedB,
        string? expandedC,
        string? expandedD,
        string? explicitA = null,
        string? explicitB = null,
        string? explicitC = null,
        string? explicitD = null,
        string? explicitAB = null,
        string? explicitBC = null,
        string? explicitAC = null,
        string? explicitABC = null)
    {
        if (explicitAB is not null || explicitBC is not null || explicitAC is not null || explicitABC is not null)
        {
            slot.ButtonA = FormatOrNull(PreferExplicit(explicitA, Minus(ParseList(expandedA), Concat(explicitAB, explicitAC, explicitABC))));
            slot.ButtonB = FormatOrNull(PreferExplicit(explicitB, Minus(ParseList(expandedB), Concat(explicitAB, explicitBC, explicitABC))));
            slot.ButtonC = FormatOrNull(PreferExplicit(explicitC, Minus(ParseList(expandedC), Concat(explicitAC, explicitBC, explicitABC))));
            slot.ButtonD = NullIfEmpty(explicitD) ?? NullIfEmpty(expandedD);
            slot.ButtonAB = NullIfEmpty(explicitAB);
            slot.ButtonBC = NullIfEmpty(explicitBC);
            slot.ButtonAC = NullIfEmpty(explicitAC);
            slot.ButtonABC = NullIfEmpty(explicitABC);
            return;
        }

        var a = ParseList(expandedA);
        var b = ParseList(expandedB);
        var c = ParseList(expandedC);
        var abc = Intersect(a, b, c);
        var ab = Minus(Intersect(a, b), abc);
        var bc = Minus(Intersect(b, c), abc);
        var ac = Minus(Intersect(a, c), abc);
        var primaryA = PreferExplicit(explicitA, Minus(a, ConcatLists(ab, ac, abc)));
        var primaryB = PreferExplicit(explicitB, Minus(b, ConcatLists(ab, bc, abc)));
        var primaryC = PreferExplicit(explicitC, Minus(c, ConcatLists(ac, bc, abc)));

        slot.ButtonA = FormatOrNull(primaryA);
        slot.ButtonB = FormatOrNull(primaryB);
        slot.ButtonC = FormatOrNull(primaryC);
        slot.ButtonD = NullIfEmpty(explicitD) ?? FormatOrNull(ParseList(expandedD));
        slot.ButtonAB = FormatOrNull(ab);
        slot.ButtonBC = FormatOrNull(bc);
        slot.ButtonAC = FormatOrNull(ac);
        slot.ButtonABC = FormatOrNull(abc);
    }

    public static string? ProblemWith(ControllerSlot slot)
    {
        foreach (var (label, value) in new[]
                 {
                     ("A", slot.ButtonA), ("B", slot.ButtonB), ("C", slot.ButtonC), ("D", slot.ButtonD),
                     ("AB", slot.ButtonAB), ("BC", slot.ButtonBC), ("AC", slot.ButtonAC), ("ABC", slot.ButtonABC),
                     ("Start", slot.Start), ("Coin", slot.Coin), ("Card", slot.Card),
                     ("Test", slot.Test), ("Service", slot.Service), ("Kill", slot.Kill),
                 })
        {
            if (!TryParseList(value, out _, out var error))
                return slot.Section + " " + label + ": " + error;
        }
        if (slot.DeviceId is int id && (id < MinDeviceId || id > MaxDeviceId))
            return slot.Section + " DeviceId must be 0-15 or 16 (all devices).";
        return null;
    }

    private static IEnumerable<int> Concat(params string?[] lists)
    {
        foreach (var list in lists)
        {
            foreach (var n in ParseList(list))
                yield return n;
        }
    }

    private static IReadOnlyList<int> ConcatLists(params IEnumerable<int>[] lists)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var list in lists)
        {
            foreach (var n in list)
                if (seen.Add(n)) result.Add(n);
        }
        return result;
    }

    private static IReadOnlyList<int> Intersect(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var set = right.ToHashSet();
        return left.Where(set.Contains).ToList();
    }

    private static IReadOnlyList<int> Intersect(IReadOnlyList<int> a, IReadOnlyList<int> b, IReadOnlyList<int> c) =>
        Intersect(Intersect(a, b), c);

    private static IReadOnlyList<int> Minus(IEnumerable<int> left, IEnumerable<int> right)
    {
        var remove = right.ToHashSet();
        return left.Where(n => !remove.Contains(n)).ToList();
    }

    private static string? Minus(string? left, IEnumerable<int> right) =>
        FormatOrNull(Minus(ParseList(left), right));

    private static IReadOnlyList<int> PreferExplicit(string? explicitValue, IReadOnlyList<int> inferred)
    {
        if (explicitValue is null) return inferred;
        var listed = ParseList(explicitValue);
        return ConcatLists(listed, Minus(inferred, listed));
    }

    private static string? FormatOrNull(IReadOnlyList<int> ids) =>
        ids.Count == 0 ? null : FormatList(ids);
}
