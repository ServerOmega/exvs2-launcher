using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Exvs2Launcher.Core.Identity;

/// <summary>An emulated card: 20 decimal digits plus a 32-character uppercase hex chip id.</summary>
public sealed record CardIdentity(string AccessCode, string ChipId)
{
    /// <summary>Never prints the values. Use <see cref="Secrets.Mask"/> if a hint is needed.</summary>
    public override string ToString() => "CardIdentity { AccessCode = " + Secrets.Mask(AccessCode) + ", ChipId = " + Secrets.Mask(ChipId) + " }";
}

public static partial class IdentityFormat
{
    // Official Banapass-style codes are 20 decimal digits. Some working pack cards use 20 hex characters
    // instead; both are accepted when reading an existing card.ini. New cards are still 20 digits.
    [GeneratedRegex("^[0-9A-Fa-f]{20}$")] private static partial Regex AccessCodeRx();
    [GeneratedRegex("^[0-9A-F]{32}$")] private static partial Regex ChipIdRx();
    [GeneratedRegex("^[A-Za-z0-9]{4}$")] private static partial Regex SerialRx();

    public static bool IsValidAccessCode(string? s) => s is not null && AccessCodeRx().IsMatch(NormalizeIniValue(s));
    public static bool IsValidChipId(string? s) => s is not null && ChipIdRx().IsMatch(NormalizeIniValue(s));
    public static bool IsValidSerial(string? s) => s is not null && SerialRx().IsMatch(s);

    /// <summary>Trim whitespace and a single pair of wrapping quotes from an INI value.</summary>
    public static string NormalizeIniValue(string? s)
    {
        if (s is null) return "";
        var t = s.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t[1..^1].Trim();
        else if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            t = t[1..^1].Trim();
        return t;
    }
}

/// <summary>
/// Generates cabinet serials and cards with a cryptographic RNG.
/// </summary>
public sealed class IdentityGenerator
{
    /// <summary>Letters and digits without look-alikes (0/O, 1/l/I).</summary>
    public const string SerialAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private readonly Func<int, int> _nextInt;

    public IdentityGenerator() : this(RandomNumberGenerator.GetInt32) { }

    /// <summary>For tests: <paramref name="nextInt"/> returns a value in [0, max).</summary>
    public IdentityGenerator(Func<int, int> nextInt) => _nextInt = nextInt;

    /// <summary>
    /// A random 4-character serial that is not in <paramref name="taken"/> (compared case-insensitively, so two
    /// cabs never differ only by case).
    /// </summary>
    public string NewSerial(IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(taken ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            var chars = new char[4];
            for (int i = 0; i < chars.Length; i++) chars[i] = SerialAlphabet[_nextInt(SerialAlphabet.Length)];
            var serial = new string(chars);
            if (!used.Contains(serial)) return serial;
        }
        throw new InvalidOperationException("Could not find an unused serial.");
    }

    /// <summary>
    /// A new card. <paramref name="accessCodePrefix"/> (digits only, optional) is kept at the start of the
    /// access code, for example "99" for a second-player cab. Without a prefix the first digit is never 0.
    /// </summary>
    public CardIdentity NewCard(string? accessCodePrefix = null)
    {
        accessCodePrefix ??= string.Empty;
        if (accessCodePrefix.Length > 18 || accessCodePrefix.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("The access code prefix must be 0-18 digits.", nameof(accessCodePrefix));

        var digits = new char[20];
        accessCodePrefix.CopyTo(0, digits, 0, accessCodePrefix.Length);
        for (int i = accessCodePrefix.Length; i < 20; i++)
        {
            int d = (i == 0) ? 1 + _nextInt(9) : _nextInt(10);
            digits[i] = (char)('0' + d);
        }

        const string hex = "0123456789ABCDEF";
        var chip = new char[32];
        for (int i = 0; i < 32; i++) chip[i] = hex[_nextInt(16)];

        return new CardIdentity(new string(digits), new string(chip));
    }
}
