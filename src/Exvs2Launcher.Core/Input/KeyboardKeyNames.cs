namespace Exvs2Launcher.Core.Input;

/// <summary>
/// Key names the Infinite Boost hook accepts in <c>config.ini</c> / <c>lmnfig.ini</c>
/// <c>[keyboard]</c> values. Confirmed from the hook's name table (same names as the
/// public VirtualKeyNames list the hook comments point at) plus the exact string
/// <c>Enter</c> in the IB hook, which is treated as an alias of <c>Return</c>.
/// Names are matched case-insensitively. Values in the INI are comma-separated
/// when one action has more than one key (observed with no space after the comma).
/// </summary>
public static class KeyboardKeyNames
{
    /// <summary>Canonical hook name for the Enter key (VK_RETURN).</summary>
    public const string ReturnName = "Return";

    private static readonly Dictionary<int, string> NamesByVk = new()
    {
        { 0x08, "Backspace" },
        { 0x09, "Tab" },
        { 0x0d, ReturnName },
        { 0x13, "Pause" },
        { 0x1b, "Esc" },
        { 0x20, "Space" },
        { 0x21, "PgUp" },
        { 0x22, "PgDown" },
        { 0x23, "End" },
        { 0x24, "Home" },
        { 0x25, "LeftArr" },
        { 0x26, "UpArr" },
        { 0x27, "RightArr" },
        { 0x28, "DownArr" },
        { 0x2d, "Insert" },
        { 0x2e, "Delete" },
        { 0x30, "0" }, { 0x31, "1" }, { 0x32, "2" }, { 0x33, "3" }, { 0x34, "4" },
        { 0x35, "5" }, { 0x36, "6" }, { 0x37, "7" }, { 0x38, "8" }, { 0x39, "9" },
        { 0x41, "A" }, { 0x42, "B" }, { 0x43, "C" }, { 0x44, "D" }, { 0x45, "E" },
        { 0x46, "F" }, { 0x47, "G" }, { 0x48, "H" }, { 0x49, "I" }, { 0x4a, "J" },
        { 0x4b, "K" }, { 0x4c, "L" }, { 0x4d, "M" }, { 0x4e, "N" }, { 0x4f, "O" },
        { 0x50, "P" }, { 0x51, "Q" }, { 0x52, "R" }, { 0x53, "S" }, { 0x54, "T" },
        { 0x55, "U" }, { 0x56, "V" }, { 0x57, "W" }, { 0x58, "X" }, { 0x59, "Y" },
        { 0x5a, "Z" },
        { 0x60, "0 (NumPad)" }, { 0x61, "1 (NumPad)" }, { 0x62, "2 (NumPad)" },
        { 0x63, "3 (NumPad)" }, { 0x64, "4 (NumPad)" }, { 0x65, "5 (NumPad)" },
        { 0x66, "6 (NumPad)" }, { 0x67, "7 (NumPad)" }, { 0x68, "8 (NumPad)" },
        { 0x69, "9 (NumPad)" }, { 0x6a, "* (NumPad)" }, { 0x6b, "+ (NumPad)" },
        { 0x6d, "- (NumPad)" }, { 0x6e, ". (NumPad)" }, { 0x6f, "/ (NumPad)" },
        { 0x70, "F1" }, { 0x71, "F2" }, { 0x72, "F3" }, { 0x73, "F4" },
        { 0x74, "F5" }, { 0x75, "F6" }, { 0x76, "F7" }, { 0x77, "F8" },
        { 0x78, "F9" }, { 0x79, "F10" }, { 0x7a, "F11" }, { 0x7b, "F12" },
        { 0x7c, "F13" }, { 0x7d, "F14" }, { 0x7e, "F15" }, { 0x7f, "F16" },
        { 0x80, "F17" }, { 0x81, "F18" }, { 0x82, "F19" }, { 0x83, "F20" },
        { 0x84, "F21" }, { 0x85, "F22" }, { 0x86, "F23" }, { 0x87, "F24" },
    };

    private static readonly Dictionary<string, int> VkByName =
        BuildVkByName();

    private static Dictionary<string, int> BuildVkByName()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in NamesByVk)
            map[name] = vk;
        // IB hook binaries contain the exact string "Enter" in addition to "Return".
        // The observed pack writes Return; we accept Enter when reading and write Return.
        map["Enter"] = 0x0d;
        return map;
    }

    /// <summary>Canonical names the hook table uses when writing a binding (Enter is not in this list).</summary>
    public static IReadOnlyCollection<string> CanonicalNames => NamesByVk.Values;

    public static bool TryGetName(int virtualKey, out string name) =>
        NamesByVk.TryGetValue(virtualKey, out name!);

    public static bool TryGetVirtualKey(string? name, out int virtualKey)
    {
        virtualKey = 0;
        var canonical = CanonicalName(name);
        if (canonical is null) return false;
        return VkByName.TryGetValue(canonical, out virtualKey);
    }

    /// <summary>
    /// Returns the hook spelling for a single token, or null if it is blank.
    /// Known names (including the <c>Enter</c> alias) are canonicalized;
    /// unknown tokens are returned trimmed so an existing INI value is not discarded.
    /// </summary>
    public static string? CanonicalName(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim();
        if (!VkByName.TryGetValue(t, out int vk)) return t;
        return NamesByVk[vk];
    }

    public static bool IsKnownName(string? token) =>
        !string.IsNullOrWhiteSpace(token) && VkByName.ContainsKey(token.Trim());

    /// <summary>Splits a comma-separated binding, trims each part, and canonicalizes known names, keeping first-seen order.</summary>
    public static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(','))
        {
            var name = CanonicalName(part);
            if (name is null) continue;
            if (seen.Add(name)) list.Add(name);
        }
        return list;
    }

    public static bool TryParseList(string? value, out IReadOnlyList<string> names, out string? error)
    {
        names = Array.Empty<string>();
        error = null;
        try
        {
            names = ParseList(value);
            var unknown = names.Where(n => !IsKnownName(n)).ToList();
            if (unknown.Count > 0)
            {
                error = "Unrecognized key name '" + unknown[0] +
                        "'. The hook only accepts the documented key names (for example Return, UpArr, F3, or 1 (NumPad)).";
                return false;
            }
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string FormatList(IEnumerable<string> names)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = CanonicalName(raw);
            if (name is null) continue;
            if (seen.Add(name)) list.Add(name);
        }
        return string.Join(",", list);
    }

    public static string? FormatOrNull(IEnumerable<string> names)
    {
        var text = FormatList(names);
        return text.Length == 0 ? null : text;
    }

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : FormatList(ParseList(value));
}
