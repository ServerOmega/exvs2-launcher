namespace Exvs2Launcher.Core.Ini;

/// <summary>
/// A line-preserving INI editor. Comments, blank lines, key order, the spacing around '=' and unknown keys are
/// kept exactly as they were; only the values that are set change. Section and key names are matched
/// case-insensitively (the game's own files mix <c>serial =</c>, <c>Server =</c> and <c>accessCode=</c> styles).
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;

    public TextFormat Format { get; }

    private IniDocument(List<string> lines, TextFormat format)
    {
        _lines = lines;
        Format = format;
    }

    public static IniDocument Empty() => new(new List<string>(), TextFormat.Default);

    public static IniDocument Parse(string text, TextFormat? format = null)
    {
        format ??= TextFormat.Default;
        var lines = new List<string>();
        if (text.Length > 0)
        {
            string body = text;
            if (body.EndsWith('\n'))
            {
                body = body[..^1];
                if (body.EndsWith('\r')) body = body[..^1];
            }
            foreach (var raw in body.Split('\n'))
                lines.Add(raw.EndsWith('\r') ? raw[..^1] : raw);
        }
        return new IniDocument(lines, format);
    }

    public static IniDocument FromBytes(byte[] bytes)
    {
        var (text, format) = TextFileCodec.Decode(bytes);
        return Parse(text, format);
    }

    public static IniDocument Load(string path) => FromBytes(File.ReadAllBytes(path));

    public IReadOnlyList<string> Lines => _lines;

    public string ToText()
    {
        if (_lines.Count == 0) return string.Empty;
        var text = string.Join(Format.NewLine, _lines);
        return Format.EndsWithNewLine ? text + Format.NewLine : text;
    }

    public byte[] ToBytes() => TextFileCodec.Encode(ToText(), Format);

    public void Save(string path)
    {
        var bytes = ToBytes();
        var tmp = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public IEnumerable<string> Sections =>
        _lines.Select(TryParseSection).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase);

    public bool HasSection(string section) =>
        _lines.Any(l => string.Equals(TryParseSection(l), section, StringComparison.OrdinalIgnoreCase));

    public string? Get(string section, string key)
    {
        int idx = FindKey(section, key);
        if (idx < 0) return null;
        var line = _lines[idx];
        return line[(line.IndexOf('=') + 1)..].Trim();
    }

    /// <summary>Returns the key exactly as it is spelled in the file, or null.</summary>
    public string? GetKeyName(string section, string key)
    {
        int idx = FindKey(section, key);
        return idx < 0 ? null : _lines[idx][.._lines[idx].IndexOf('=')].Trim();
    }

    /// <summary>
    /// Sets a value. An existing key keeps its spelling and spacing; a missing key is appended to the end of the
    /// section (after its last non-blank line) as <c>key=value</c>; a missing section is appended to the file.
    /// Returns true when the document changed.
    /// </summary>
    public bool Set(string section, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException("INI values cannot contain line breaks.", nameof(value));

        int idx = FindKey(section, key);
        if (idx >= 0)
        {
            var line = _lines[idx];
            int eq = line.IndexOf('=');
            string after = line[(eq + 1)..];
            int lead = after.Length - after.TrimStart(' ', '\t').Length;
            string updated = line[..(eq + 1)] + after[..lead] + value;
            if (updated == line) return false;
            _lines[idx] = updated;
            return true;
        }

        int sectionStart = FindSection(section);
        if (sectionStart < 0)
        {
            _lines.Add("[" + section + "]");
            _lines.Add(key + "=" + value);
            return true;
        }

        int end = sectionStart + 1;
        while (end < _lines.Count && TryParseSection(_lines[end]) is null) end++;
        int insertAt = end;
        while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(_lines[insertAt - 1])) insertAt--;
        _lines.Insert(insertAt, key + "=" + value);
        return true;
    }

    private int FindSection(string section)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (string.Equals(TryParseSection(_lines[i]), section, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private int FindKey(string section, string key)
    {
        string? current = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var s = TryParseSection(line);
            if (s is not null) { current = s; continue; }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            var k = TryParseKey(line);
            if (k is not null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    internal static string? TryParseSection(string line)
    {
        var t = line.Trim();
        if (t.Length < 2 || t[0] != '[') return null;
        int close = t.IndexOf(']');
        return close > 1 ? t[1..close].Trim() : null;
    }

    internal static string? TryParseKey(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] == '#' || t[0] == ';' || t[0] == '[') return null;
        int eq = t.IndexOf('=');
        if (eq <= 0) return null;
        var key = t[..eq].Trim();
        return key.Length == 0 ? null : key;
    }
}
