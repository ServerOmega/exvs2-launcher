namespace Exvs2Launcher.Core.Net;

/// <summary>
/// <c>DisableSocketHook</c> in <c>lmnfig.ini</c> controls whether the LM binds <c>IpAddress</c>.
/// When it is True, the hook is off, the LM ignores its IP and listens on 0.0.0.0, and other LMs
/// on this PC fail with error 21-32. SnowLauncher writes True for the LM; this launcher never does.
/// </summary>
public static class SocketHookSetting
{
    public const string Key = "DisableSocketHook";
    public const string EnabledValue = "False";

    public const string GrabEveryAddress =
        "the LM will grab every address and other LMs fail with 21-32";

    public static bool IsDisabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        return t.Equals("True", StringComparison.OrdinalIgnoreCase)
               || t.Equals("1", StringComparison.Ordinal)
               || t.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Warning when the LM hook is off in a multi-cab or split-IP setup, or when SnowLauncher.ini
    /// would turn it off later. Null if there is nothing dangerous to report.
    /// </summary>
    public static string? WarnIfDangerous(string? lmHook, string? pcbIp, string? lmIp, bool multipleCabs, string? snowHook = null)
    {
        bool splitIps = !string.IsNullOrWhiteSpace(pcbIp) && !string.IsNullOrWhiteSpace(lmIp)
                        && !NetworkBinding.SameIp(pcbIp, lmIp);
        bool riskyLayout = splitIps || multipleCabs;

        if (IsDisabled(lmHook) && riskyLayout)
            return "lmnfig.ini DisableSocketHook=True. With the hook off, the LM ignores IpAddress and binds every address (0.0.0.0), so "
                   + GrabEveryAddress + ". Set DisableSocketHook=False so the LM stays on its own IP.";

        if (IsDisabled(snowHook))
            return "SnowLauncher.ini has DisableSocketHook=True; it was not written to lmnfig.ini. Opening SnowLauncher would turn the LM hook off, so "
                   + GrabEveryAddress + ". Set it to False if you use SnowLauncher.ini sync.";

        return null;
    }

    public static string Display(string? lmHook) =>
        "lmnfig.ini DisableSocketHook=" + (string.IsNullOrWhiteSpace(lmHook) ? "(not set)" : lmHook.Trim());

    public static string OfferFix =>
        "Set DisableSocketHook=False in lmnfig.ini (and SnowLauncher.ini if sync is on)? A backup is made if a file changes. "
        + "With the hook off, " + GrabEveryAddress + ". This launcher never turns the hook off by itself.";
}
