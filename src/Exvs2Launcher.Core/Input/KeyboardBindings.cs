namespace Exvs2Launcher.Core.Input;

/// <summary>
/// One keyboard map as edited in the launcher. Face buttons and combos are stored the
/// way SnowLauncher does (<c>ButtonA</c>, <c>ButtonAB</c>, …). When writing
/// <c>config.ini</c> / <c>lmnfig.ini</c> they are expanded into the <c>A</c>/<c>B</c>/<c>C</c>
/// lists the hook actually reads.
/// </summary>
public sealed class KeyboardMap
{
    public bool Enabled { get; set; } = true;
    public string? Up { get; set; }
    public string? Down { get; set; }
    public string? Left { get; set; }
    public string? Right { get; set; }
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
}

/// <summary>Client and LM keyboard maps currently in a game folder (no secrets).</summary>
public sealed class KeyboardFileState
{
    public KeyboardMap Client { get; set; } = new();
    public KeyboardMap Lm { get; set; } = new();
}

/// <summary>
/// Pack-observed Infinite Boost defaults (from the live <c>config.ini</c>,
/// <c>lmnfig.ini</c>, and <c>SnowLauncher.ini</c> keyboard sections). These are
/// not the hook's compiled fallbacks, which differ (for example Start=1, Coin=M).
/// </summary>
public static class KeyboardDefaults
{
    public static KeyboardMap Client() => new()
    {
        Enabled = true,
        Up = "W",
        Down = "S",
        Left = "A",
        Right = "D",
        ButtonA = "J",
        ButtonB = "I",
        ButtonC = "K",
        ButtonD = "L",
        ButtonAB = "U",
        ButtonBC = "O",
        ButtonAC = "Y",
        ButtonABC = "T",
        Start = KeyboardKeyNames.ReturnName,
        Coin = "5",
        Card = "4",
        Test = "F3",
        Service = "F2",
        Kill = "F4",
    };

    public static KeyboardMap Lm() => new()
    {
        Enabled = true,
        Up = "UpArr",
        Down = "DownArr",
        Left = "LeftArr",
        Right = "RightArr",
        ButtonA = "Z",
        ButtonB = "X",
        ButtonC = "C",
        ButtonD = "V",
        Start = "Q",
        Coin = "5",
        Card = "P",
        Test = "F3",
        Kill = "F4",
    };

    public static string? Value(KeyboardMap map, string action) => action switch
    {
        "Up" => map.Up,
        "Down" => map.Down,
        "Left" => map.Left,
        "Right" => map.Right,
        "A" => map.ButtonA,
        "B" => map.ButtonB,
        "C" => map.ButtonC,
        "D" => map.ButtonD,
        "AB" => map.ButtonAB,
        "BC" => map.ButtonBC,
        "AC" => map.ButtonAC,
        "ABC" => map.ButtonABC,
        "Start" => map.Start,
        "Coin" => map.Coin,
        "Card" => map.Card,
        "Test" => map.Test,
        "Service" => map.Service,
        "Kill" => map.Kill,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown keyboard action."),
    };

    public static void Set(KeyboardMap map, string action, string? value)
    {
        switch (action)
        {
            case "Up": map.Up = value; break;
            case "Down": map.Down = value; break;
            case "Left": map.Left = value; break;
            case "Right": map.Right = value; break;
            case "A": map.ButtonA = value; break;
            case "B": map.ButtonB = value; break;
            case "C": map.ButtonC = value; break;
            case "D": map.ButtonD = value; break;
            case "AB": map.ButtonAB = value; break;
            case "BC": map.ButtonBC = value; break;
            case "AC": map.ButtonAC = value; break;
            case "ABC": map.ButtonABC = value; break;
            case "Start": map.Start = value; break;
            case "Coin": map.Coin = value; break;
            case "Card": map.Card = value; break;
            case "Test": map.Test = value; break;
            case "Service": map.Service = value; break;
            case "Kill": map.Kill = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown keyboard action.");
        }
    }
}

/// <summary>
/// Converts between SnowLauncher-style primary + combo keys and the expanded
/// <c>A=</c>/<c>B=</c>/<c>C=</c> lists the hook reads in the game INI.
/// Order matches the observed pack: primary, then AB, AC, ABC for A; primary, AB, BC, ABC for B;
/// primary, BC, AC, ABC for C (this C order differs from the controller expander).
/// </summary>
public static class KeyboardCombos
{
    public static string ExpandA(KeyboardMap map) =>
        KeyboardKeyNames.FormatList(Concat(map.ButtonA, map.ButtonAB, map.ButtonAC, map.ButtonABC));

    public static string ExpandB(KeyboardMap map) =>
        KeyboardKeyNames.FormatList(Concat(map.ButtonB, map.ButtonAB, map.ButtonBC, map.ButtonABC));

    public static string ExpandC(KeyboardMap map) =>
        KeyboardKeyNames.FormatList(Concat(map.ButtonC, map.ButtonBC, map.ButtonAC, map.ButtonABC));

    public static string ExpandD(KeyboardMap map) =>
        KeyboardKeyNames.FormatList(KeyboardKeyNames.ParseList(map.ButtonD));

    public static void FillFromExpanded(
        KeyboardMap map,
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
            map.ButtonA = FormatOrNull(PreferExplicit(explicitA, Minus(KeyboardKeyNames.ParseList(expandedA), Concat(explicitAB, explicitAC, explicitABC))));
            map.ButtonB = FormatOrNull(PreferExplicit(explicitB, Minus(KeyboardKeyNames.ParseList(expandedB), Concat(explicitAB, explicitBC, explicitABC))));
            map.ButtonC = FormatOrNull(PreferExplicit(explicitC, Minus(KeyboardKeyNames.ParseList(expandedC), Concat(explicitAC, explicitBC, explicitABC))));
            map.ButtonD = KeyboardKeyNames.NullIfEmpty(explicitD) ?? KeyboardKeyNames.NullIfEmpty(expandedD);
            map.ButtonAB = KeyboardKeyNames.NullIfEmpty(explicitAB);
            map.ButtonBC = KeyboardKeyNames.NullIfEmpty(explicitBC);
            map.ButtonAC = KeyboardKeyNames.NullIfEmpty(explicitAC);
            map.ButtonABC = KeyboardKeyNames.NullIfEmpty(explicitABC);
            return;
        }

        var a = KeyboardKeyNames.ParseList(expandedA);
        var b = KeyboardKeyNames.ParseList(expandedB);
        var c = KeyboardKeyNames.ParseList(expandedC);
        var abc = Intersect(a, b, c);
        var ab = Minus(Intersect(a, b), abc);
        var bc = Minus(Intersect(b, c), abc);
        var ac = Minus(Intersect(a, c), abc);
        var primaryA = PreferExplicit(explicitA, Minus(a, ConcatLists(ab, ac, abc)));
        var primaryB = PreferExplicit(explicitB, Minus(b, ConcatLists(ab, bc, abc)));
        var primaryC = PreferExplicit(explicitC, Minus(c, ConcatLists(ac, bc, abc)));

        map.ButtonA = FormatOrNull(primaryA);
        map.ButtonB = FormatOrNull(primaryB);
        map.ButtonC = FormatOrNull(primaryC);
        map.ButtonD = KeyboardKeyNames.NullIfEmpty(explicitD) ?? FormatOrNull(KeyboardKeyNames.ParseList(expandedD));
        map.ButtonAB = FormatOrNull(ab);
        map.ButtonBC = FormatOrNull(bc);
        map.ButtonAC = FormatOrNull(ac);
        map.ButtonABC = FormatOrNull(abc);
    }

    /// <summary>
    /// Movement, face buttons, Start, Coin, Card, Test, and Kill must be set to a known hook name
    /// (an empty value would be written as a blank INI key, which the hook can refuse to parse).
    /// Combos and Service may be empty.
    /// </summary>
    public static string? ProblemWith(KeyboardMap map, string side)
    {
        foreach (var (label, value, required) in new[]
                 {
                     ("Up", map.Up, true), ("Down", map.Down, true), ("Left", map.Left, true), ("Right", map.Right, true),
                     ("A", map.ButtonA, true), ("B", map.ButtonB, true), ("C", map.ButtonC, true), ("D", map.ButtonD, true),
                     ("AB", map.ButtonAB, false), ("BC", map.ButtonBC, false), ("AC", map.ButtonAC, false), ("ABC", map.ButtonABC, false),
                     ("Start", map.Start, true), ("Coin", map.Coin, true), ("Card", map.Card, true),
                     ("Test", map.Test, true), ("Service", map.Service, false), ("Kill", map.Kill, true),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) return side + " " + label + " needs a key. Use Rebind or Reset to default.";
                continue;
            }
            if (!KeyboardKeyNames.TryParseList(value, out _, out var error))
                return side + " " + label + ": " + error;
        }
        return null;
    }

    private static IEnumerable<string> Concat(params string?[] lists)
    {
        foreach (var list in lists)
        {
            foreach (var n in KeyboardKeyNames.ParseList(list))
                yield return n;
        }
    }

    private static IReadOnlyList<string> ConcatLists(params IEnumerable<string>[] lists)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            foreach (var n in list)
                if (seen.Add(n)) result.Add(n);
        }
        return result;
    }

    private static IReadOnlyList<string> Intersect(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var set = right.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return left.Where(set.Contains).ToList();
    }

    private static IReadOnlyList<string> Intersect(IReadOnlyList<string> a, IReadOnlyList<string> b, IReadOnlyList<string> c) =>
        Intersect(Intersect(a, b), c);

    private static IReadOnlyList<string> Minus(IEnumerable<string> left, IEnumerable<string> right)
    {
        var remove = right.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return left.Where(n => !remove.Contains(n)).ToList();
    }

    private static IReadOnlyList<string> PreferExplicit(string? explicitValue, IReadOnlyList<string> inferred)
    {
        if (explicitValue is null) return inferred;
        var listed = KeyboardKeyNames.ParseList(explicitValue);
        return ConcatLists(listed, Minus(inferred, listed));
    }

    private static string? FormatOrNull(IReadOnlyList<string> names) =>
        KeyboardKeyNames.FormatOrNull(names);
}
