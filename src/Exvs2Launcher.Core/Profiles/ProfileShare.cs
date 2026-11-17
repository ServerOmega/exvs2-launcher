using System.Text.Json;
using System.Text.Json.Serialization;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Matchmaking;

namespace Exvs2Launcher.Core.Profiles;

/// <summary>The shareable form of one cab profile. It never holds card values, the tag secret, IPs or window titles.</summary>
public sealed class SharedProfile
{
    public const string FormatName = "exvs2-launcher-profile";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public string? ExportedBy { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }

    public string Name { get; set; } = "";
    /// <summary>Where the game was on the exporting PC; the importer is asked to confirm or change it.</summary>
    public string? GameFolder { get; set; }
    public string? CardServerPath { get; set; }
    public string? CardServerArguments { get; set; }
    public bool StartCardServer { get; set; } = true;
    public bool StartLm { get; set; }
    public bool StartClient { get; set; } = true;
    public string? CardServerHost { get; set; }
    public int CardServerPort { get; set; } = 28080;
    public int PortTimeoutSeconds { get; set; } = 90;
    public int LmToClientDelaySeconds { get; set; } = 3;
    public bool AutoDetectIp { get; set; } = true;
    public bool SyncSnowLauncherIni { get; set; } = true;
    public string? AccessCodePrefix { get; set; }
    /// <summary>Only when the exporter ticked "include serial". Importing a serial another cab uses is warned about.</summary>
    public string? Serial { get; set; }
    /// <summary>A matchmaking server to add to the importer's list (name, host, port only).</summary>
    public MatchmakingServer? MatchmakingServer { get; set; }
}

public sealed record ImportReviewLine(string Field, string Value, string? Note = null);

public sealed class ImportReview
{
    public required LauncherProfile Profile { get; init; }
    public MatchmakingServer? MatchmakingServer { get; init; }
    public List<ImportReviewLine> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>An existing profile with the same name (overwriting it needs the user's confirmation).</summary>
    public LauncherProfile? Conflict { get; init; }
    public string SuggestedNewName { get; init; } = "";
}

/// <summary>Export / import of cab profiles as small JSON files that are safe to post in Discord.</summary>
public static class ProfileShare
{
    public const long MaxFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SharedProfile ToShared(LauncherProfile p, bool includeSerial, MatchmakingServer? server = null, DateTimeOffset? now = null) => new()
    {
        ExportedBy = "EXVS2 Launcher",
        ExportedAt = now ?? DateTimeOffset.Now,
        Name = p.Name,
        GameFolder = Blank(p.GameFolder),
        CardServerPath = Blank(p.CardServerPath),
        CardServerArguments = Blank(p.CardServerArguments),
        StartCardServer = p.StartCardServer,
        StartLm = p.StartLm,
        StartClient = p.StartClient,
        CardServerHost = Blank(p.CardServerHost),
        CardServerPort = p.CardServerPort,
        PortTimeoutSeconds = p.PortTimeoutSeconds,
        LmToClientDelaySeconds = p.LmToClientDelaySeconds,
        AutoDetectIp = p.AutoDetectIp,
        SyncSnowLauncherIni = p.SyncSnowLauncherIni,
        AccessCodePrefix = Blank(p.AccessCodePrefix),
        Serial = includeSerial ? Blank(p.Serial) : null,
        MatchmakingServer = server is { HasAddress: true } ? server.Clone() : null,
    };

    public static string Export(LauncherProfile p, bool includeSerial, MatchmakingServer? server = null, DateTimeOffset? now = null) =>
        JsonSerializer.Serialize(ToShared(p, includeSerial, server, now), Json);

    /// <summary>Parses and checks an exported profile. Throws <see cref="InvalidDataException"/> with a readable message.</summary>
    public static ImportReview Review(string json, ProfileFile existing)
    {
        if (json.Length > MaxFileBytes) throw new InvalidDataException("That file is too large to be a launcher profile.");
        SharedProfile? s;
        try { s = JsonSerializer.Deserialize<SharedProfile>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException("That file is not a launcher profile (" + ex.Message + ")."); }
        if (s is null || !HasFormatMarker(json) || !string.Equals(s.Format, SharedProfile.FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("That file is not an EXVS2 launcher profile export.");
        if (s.Version > SharedProfile.CurrentVersion)
            throw new InvalidDataException($"That profile was exported by a newer launcher (format {s.Version}). Update the launcher first.");

        var warnings = new List<string>();
        var name = Clean(s.Name, 60);
        if (string.IsNullOrWhiteSpace(name)) name = "Imported cab";

        var serverPath = Clean(s.CardServerPath, 400);
        if (serverPath is not null && string.Equals(Path.GetFileName(serverPath.Replace('\\', '/').Split('/').Last()), "Server.exe", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("The card server in this profile is the pack's stock Server.exe. It was cleared; pick the patched Server-Infinite.exe after importing.");
            serverPath = null;
        }
        var host = Clean(s.CardServerHost, 100) ?? "127.0.0.1";
        if (!System.Net.IPAddress.TryParse(host, out _) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Card server address \"{host}\" is not an IP address; 127.0.0.1 is used instead.");
            host = "127.0.0.1";
        }
        int port = s.CardServerPort is >= 1 and <= 65535 ? s.CardServerPort : 28080;
        string? serial = Clean(s.Serial, 8);
        if (serial is not null && !IdentityFormat.IsValidSerial(serial))
        {
            warnings.Add("The serial in the file is not a 4-character serial; it was dropped.");
            serial = null;
        }
        if (serial is not null && existing.Profiles.Any(p => string.Equals(p.Serial, serial, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"Serial {serial} is already used by another cab here; it was dropped so two cabs do not share one serial. Use New serial after importing.");
            serial = null;
        }
        var prefix = Clean(s.AccessCodePrefix, 18);
        if (prefix is not null && prefix.Any(c => c is < '0' or > '9')) { warnings.Add("The access code prefix was not digits only; it was dropped."); prefix = null; }

        var folder = Clean(s.GameFolder, 400);
        if (folder is not null && !Directory.Exists(folder))
            warnings.Add("The game folder from the other PC does not exist here. Pick this PC's folder before starting.");

        var profile = new LauncherProfile
        {
            Name = name,
            GameFolder = folder ?? "",
            CardServerPath = serverPath,
            CardServerArguments = Clean(s.CardServerArguments, 400) ?? "",
            StartCardServer = s.StartCardServer,
            StartLm = s.StartLm,
            StartClient = s.StartClient,
            CardServerHost = host,
            CardServerPort = port,
            PortTimeoutSeconds = Math.Clamp(s.PortTimeoutSeconds, 5, 600),
            LmToClientDelaySeconds = Math.Clamp(s.LmToClientDelaySeconds, 0, 120),
            AutoDetectIp = s.AutoDetectIp,
            SyncSnowLauncherIni = s.SyncSnowLauncherIni,
            AccessCodePrefix = prefix ?? "",
            Serial = serial,
        };

        MatchmakingServer? mm = null;
        if (s.MatchmakingServer is { } m)
        {
            var err = MatchmakingServers.TryCreate(m.Name, m.Host, m.Port.ToString(), Array.Empty<MatchmakingServer>(), out var clean);
            if (err is null && clean.HasAddress) mm = clean;
            else warnings.Add("The matchmaking server in the file was not valid and was skipped.");
        }

        var conflict = existing.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var review = new ImportReview
        {
            Profile = profile,
            MatchmakingServer = mm,
            Conflict = conflict,
            SuggestedNewName = conflict is null ? name : UniqueName(name + " (imported)", existing),
        };
        review.Warnings.AddRange(warnings);
        review.Lines.Add(new("Name", name, conflict is null ? null : "A profile with this name already exists."));
        review.Lines.Add(new("Game folder", profile.GameFolder.Length == 0 ? "(not set)" : profile.GameFolder));
        review.Lines.Add(new("Card server", profile.CardServerPath ?? "(not set)"));
        review.Lines.Add(new("Card server address", $"{host}:{port}"));
        review.Lines.Add(new("Starts", string.Join(", ", new[] { profile.StartCardServer ? "card server" : null, profile.StartLm ? "LM" : null, profile.StartClient ? "client" : null }.Where(x => x is not null))));
        review.Lines.Add(new("Serial", serial ?? "(none; a unique one is assigned when you pick the game folder)"));
        if (mm is not null) review.Lines.Add(new("Matchmaking server", $"{mm.Name} ({mm.Address})", "Added to your server list if it is not there yet."));
        review.Lines.Add(new("Not included", "card values, tag secret, PCB / LM IPs (detected on this PC)"));
        return review;
    }

    /// <summary>
    /// Adds the reviewed profile. When the name exists, <paramref name="overwrite"/> must be true to replace it
    /// (the caller asks the user first); otherwise it is added under <see cref="ImportReview.SuggestedNewName"/>.
    /// </summary>
    public static LauncherProfile Apply(ImportReview review, ProfileFile file, bool overwrite)
    {
        var p = review.Profile;
        if (review.Conflict is not null)
        {
            if (overwrite)
            {
                int i = file.Profiles.IndexOf(review.Conflict);
                // Keep the replaced cab's serial unless the import carries a (checked) one.
                p.Serial ??= review.Conflict.Serial;
                if (i >= 0) file.Profiles[i] = p; else file.Profiles.Add(p);
            }
            else
            {
                if (string.Equals(p.Name.Trim(), review.Conflict.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    p.Name = review.SuggestedNewName;
                file.Profiles.Add(p);
            }
        }
        else file.Profiles.Add(p);

        if (review.MatchmakingServer is { } mm)
        {
            file.MatchmakingServers ??= MatchmakingServers.Defaults();
            bool exists = file.MatchmakingServers.Any(x =>
                string.Equals(x.Host.Trim(), mm.Host.Trim(), StringComparison.OrdinalIgnoreCase) && x.Port == mm.Port);
            if (!exists)
            {
                var n = mm.Name;
                for (int k = 2; file.MatchmakingServers.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)); k++) n = mm.Name + " " + k;
                file.MatchmakingServers.Add(new MatchmakingServer { Name = n, Host = mm.Host, Port = mm.Port });
            }
        }
        return p;
    }

    private static bool HasFormatMarker(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("format", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String &&
                    p.Value.GetString() == SharedProfile.FormatName) return true;
            return false;
        }
        catch (JsonException) { return false; }
    }

    private static string UniqueName(string name, ProfileFile file)
    {
        var n = name;
        for (int i = 2; file.Profiles.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} {i}";
        return n;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Trim().Where(c => !char.IsControl(c)).ToArray());
        return t.Length == 0 ? null : (t.Length > max ? t[..max] : t);
    }
}
