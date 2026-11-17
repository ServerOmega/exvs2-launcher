using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.Diagnostics;

/// <summary>
/// Masks secrets before anything leaves the PC (diagnostics zips, saved health events, shared profiles).
/// Card keys (access code / chip id), tag secrets, boot tokens, passwords, card ids, 20-digit and 32-hex card values,
/// public IPv4 addresses and the Windows user name are replaced. LAN / loopback addresses and matchmaker URLs stay.
/// </summary>
public static partial class Redactor
{
    public const string Masked = "***";

    /// <summary>Key names whose values are always masked (case-insensitive, also as substrings: "tagSecret", "authToken").</summary>
    private static readonly string[] SensitiveFragments =
    {
        "secret", "token", "password", "passwd", "accesscode", "access_code", "chipid", "chip_id", "n2n_community",
        "apikey", "api_key", "privatekey", "sessionid", "session_id", "cardid", "card_id", "pilotid", "am_id",
    };

    /// <summary>Keys that look sensitive by substring but are safe settings (kept).</summary>
    private static readonly HashSet<string> SafeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "TokenRefreshSeconds", "SecretFile",
    };

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        if (SafeKeys.Contains(k)) return false;
        var lower = k.ToLowerInvariant();
        if (lower is "card" or "amid" or "amids") return true;
        return SensitiveFragments.Any(f => lower.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>Masks the value of sensitive <c>key=value</c> lines; comments, spacing and other keys are kept.</summary>
    public static string MaskIni(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var (line, eol) in Lines(text))
        {
            var trimmed = line.TrimStart();
            int eq = line.IndexOf('=');
            if (eq > 0 && !trimmed.StartsWith(';') && !trimmed.StartsWith('#') && !trimmed.StartsWith('['))
            {
                var key = line[..eq].Trim();
                if (IsSensitiveKey(key))
                {
                    var value = line[(eq + 1)..].Trim();
                    sb.Append(line[..(eq + 1)]).Append(value.Length == 0 ? "" : Masked + " (" + value.Length + " chars)").Append(eol);
                    continue;
                }
            }
            sb.Append(MaskText(line)).Append(eol);
        }
        return sb.ToString();
    }

    /// <summary>Masks sensitive properties anywhere in a JSON document. Returns masked free text when it is not JSON.</summary>
    public static string MaskJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException) { return MaskText(json); }
        if (node is null) return MaskText(json);
        MaskNode(node);
        return MaskText(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void MaskNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (IsSensitiveKey(key) && child is JsonValue v)
                    {
                        var s = v.ToString();
                        obj[key] = string.IsNullOrEmpty(s) ? "" : Masked;
                    }
                    else if (child is not null) MaskNode(child);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr) if (child is not null) MaskNode(child);
                break;
        }
    }

    // key=value, key: value, "key": "value", key value with quotes
    [GeneratedRegex(@"(?<key>""?\b[A-Za-z_][A-Za-z0-9_.]*""?)(?<sep>\s*[=:]\s*)(?<q>""?)(?<val>[^\s"",;&\]\}\)]+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{20}(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex TwentyDigits();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex ThirtyTwoHex();

    [GeneratedRegex(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?i)(?<pre>[A-Z]:\\Users\\|/home/|/Users/)(?<user>[^\\/\s""]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UserPath();

    /// <summary>Masks secrets in free text (log lines).</summary>
    public static string MaskText(string text, bool maskPublicIps = true)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = KeyValue().Replace(text, m =>
        {
            var key = m.Groups["key"].Value.Trim('"');
            return IsSensitiveKey(key) ? m.Groups["key"].Value + m.Groups["sep"].Value + m.Groups["q"].Value + Masked : m.Value;
        });
        s = TwentyDigits().Replace(s, "[20-char card value masked]");
        s = ThirtyTwoHex().Replace(s, "[32-hex value masked]");
        if (maskPublicIps) s = Ipv4().Replace(s, m => IsPublicIpv4(m.Value) ? "[public IP masked]" : m.Value);
        s = UserPath().Replace(s, m => m.Groups["pre"].Value + "%USERNAME%");
        return s;
    }

    public static bool IsPublicIpv4(string text)
    {
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] == 0) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT / VPN overlays
        if (b[0] >= 224) return false; // multicast, broadcast, reserved
        return true;
    }

    private static IEnumerable<(string Line, string Eol)> Lines(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int n = text.IndexOf('\n', i);
            if (n < 0) { yield return (text[i..], ""); yield break; }
            int end = n > i && text[n - 1] == '\r' ? n - 1 : n;
            yield return (text[i..end], text[end..(n + 1)]);
            i = n + 1;
        }
    }
}
