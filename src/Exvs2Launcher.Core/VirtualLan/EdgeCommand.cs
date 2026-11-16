using System.Text;

namespace Exvs2Launcher.Core.VirtualLan;

/// <summary>One edge option, for example ("-c", community). Value is null for a bare flag such as -E.</summary>
public sealed record EdgeOption(string Flag, string? Value, bool Secret = false);

/// <summary>
/// Builds the edge invocation. Options go into a short-lived config file (edge reads it when its only argument
/// is a file path), so the key is never on the command line where any process listing could read it.
/// The command line that is logged is just <c>"edge.exe" "edge.conf"</c>.
/// </summary>
public sealed class EdgeCommand
{
    public const string Redacted = "(hidden)";

    private EdgeCommand(string exePath, IReadOnlyList<EdgeOption> options, string configPath)
    {
        ExePath = exePath;
        Options = options;
        ConfigPath = configPath;
    }

    public string ExePath { get; }
    public string ConfigPath { get; }
    public IReadOnlyList<EdgeOption> Options { get; }

    /// <summary>The arguments passed to edge.exe: only the config file path.</summary>
    public string Arguments => Quote(ConfigPath);

    /// <summary>Safe to log: no community, no key.</summary>
    public string DisplayCommandLine => Quote(ExePath) + " " + Arguments;

    /// <summary>The config file text. Contains the key: write it only to a private file and delete it soon.</summary>
    public string ConfigFileText()
    {
        var sb = new StringBuilder();
        foreach (var o in Options) sb.Append(Line(o, redact: false)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The config file with the key and community replaced by (hidden). Safe to log.</summary>
    public string RedactedConfigText()
    {
        var sb = new StringBuilder();
        foreach (var o in Options) sb.Append(Line(o, redact: true)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The same options as a single argument string (redacted). Used only for display and tests.</summary>
    public string RedactedArgumentString() =>
        string.Join(" ", Options.Select(o => o.Value is null ? o.Flag : o.Flag + " " + Quote(o.Secret ? Redacted : o.Value)));

    public static EdgeCommand Build(VirtualLanSettings s, string key, string configPath)
    {
        if (!VirtualLanConfig.TryParseSupernode(s.Supernode, out var sn, out var snErr))
            throw new ArgumentException(snErr);
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("The encryption key is empty.");

        var o = new List<EdgeOption>
        {
            new("-c", s.Community.Trim(), Secret: true),
            new("-k", key, Secret: true),
            new("-l", sn!.ToString()),
        };

        switch (s.IpMode)
        {
            case VirtualIpMode.Static:
                if (!VirtualLanConfig.TryParseStatic(s.StaticIp, out var ip, out var ipErr)) throw new ArgumentException(ipErr);
                o.Add(new("-a", ip!.ToString()));
                break;
            case VirtualIpMode.Dhcp:
                o.Add(new("-r", null));
                o.Add(new("-a", "dhcp:0.0.0.0"));
                break;
            case VirtualIpMode.SupernodeAssigned:
                break;
        }

        if (!string.IsNullOrWhiteSpace(s.AdapterName)) o.Add(new("-d", s.AdapterName.Trim()));
        if (!string.IsNullOrWhiteSpace(s.Description)) o.Add(new("-I", s.Description.Trim()));
        o.Add(new("-M", s.Mtu.ToString()));
        o.Add(new("-x", s.Metric.ToString()));
        if (s.Multicast) o.Add(new("-E", null));
        if (s.ForceRelay) o.Add(new("-S1", null));
        if (s.HeaderEncryption) o.Add(new("-H", null));
        o.Add(new("-t", s.ManagementPort.ToString()));

        return new EdgeCommand(s.EdgePath.Trim().Trim('"'), o, configPath);
    }

    /// <summary>Replaces every occurrence of the given secrets (key, community) with (hidden).</summary>
    public static string Redact(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var s in secrets.Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x!.Length))
            text = text.Replace(s!, Redacted, StringComparison.Ordinal);
        return text;
    }

    private static string Line(EdgeOption o, bool redact) =>
        o.Value is null ? o.Flag : o.Flag + "=" + (redact && o.Secret ? Redacted : o.Value);

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";
}
