namespace Exvs2Launcher.Core.Identity;

public static class Secrets
{
    /// <summary>INI keys whose values must never be shown or logged.</summary>
    public static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "accessCode", "chipId", "BootToken", "TagSecret", "N2N_Community",
    };

    /// <summary>"(empty)" or "(set, N chars)". Never reveals any character of the value.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "(empty)" : $"(set, {value.Length} chars)";
}
