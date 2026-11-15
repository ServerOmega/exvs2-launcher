using System.Net;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// Helps a multi-cab PC add a second IPv4 on the same adapter as the PCB IP, so the LM can bind its own
/// address. The launcher never runs the command; it only shows it.
/// </summary>
public sealed record SecondaryAddressAdvice(
    bool HasSecondAddressOnAdapter,
    IReadOnlyList<NicAddress> SameAdapterAddresses,
    IReadOnlyList<NicAddress> LmCandidates,
    string? SuggestedIp,
    string? InterfaceAlias,
    int PrefixLength,
    string? Command,
    string Message);

public static class SecondaryAddress
{
    public const int DefaultPrefixLength = 24;

    /// <summary>
    /// One-line elevated PowerShell that adds a secondary IPv4 without making it the default source address.
    /// </summary>
    public static string NewNetIpAddressCommand(string interfaceAlias, string ip, int prefixLength = DefaultPrefixLength)
    {
        if (string.IsNullOrWhiteSpace(interfaceAlias)) throw new ArgumentException("An interface alias is required.", nameof(interfaceAlias));
        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("An IP address is required.", nameof(ip));
        if (prefixLength is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var alias = interfaceAlias.Replace("'", "''");
        return $"New-NetIPAddress -InterfaceAlias '{alias}' -IPAddress '{ip.Trim()}' -PrefixLength {prefixLength} -SkipAsSource $true";
    }

    public static string? Suggest(string pcbIp, IEnumerable<string> alreadyUsed)
    {
        if (!IPAddress.TryParse(pcbIp, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;
        var b = addr.GetAddressBytes();
        var used = new HashSet<string>(alreadyUsed.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        used.Add(pcbIp.Trim());

        var lastOctets = new List<int> { 245 };
        for (int i = 240; i <= 254; i++) lastOctets.Add(i);
        for (int i = 2; i <= 239; i++) lastOctets.Add(i);

        foreach (var last in lastOctets.Distinct())
        {
            if (last == b[3]) continue;
            var cand = $"{b[0]}.{b[1]}.{b[2]}.{last}";
            if (!used.Contains(cand)) return cand;
        }
        return null;
    }

    /// <summary>
    /// For a chosen PCB IP, lists other IPv4s on the same adapter (LM candidates) and, if there are none,
    /// a suggested address plus the admin command to add it.
    /// </summary>
    public static SecondaryAddressAdvice Advise(string? pcbIp, IReadOnlyList<NicAddress> nics, int prefixLength = DefaultPrefixLength)
    {
        var local = nics.Where(n => !LoopbackAddress.IsLoopback(n.Address)).ToList();
        if (string.IsNullOrWhiteSpace(pcbIp))
            return new SecondaryAddressAdvice(false, Array.Empty<NicAddress>(), local, null, null, prefixLength, null,
                "Pick a PCB IP first.");

        var pcb = local.FirstOrDefault(n => NetworkBinding.SameIp(n.Address, pcbIp));
        var alias = pcb?.InterfaceName;
        IReadOnlyList<NicAddress> same = alias is null
            ? Array.Empty<NicAddress>()
            : local.Where(n => string.Equals(n.InterfaceName, alias, StringComparison.OrdinalIgnoreCase)).ToList();
        var othersOnAdapter = same.Where(n => !NetworkBinding.SameIp(n.Address, pcbIp)).ToList();
        var lmCandidates = local.Where(n => !NetworkBinding.SameIp(n.Address, pcbIp)).ToList();

        if (othersOnAdapter.Count > 0)
        {
            return new SecondaryAddressAdvice(true, same, lmCandidates, othersOnAdapter[0].Address, alias, prefixLength, null,
                "This adapter already has another IPv4 (" + string.Join(", ", othersOnAdapter.Select(n => n.Address)) +
                "). Pick one of those for the LM.");
        }

        var used = local.Select(n => n.Address);
        var suggested = Suggest(pcbIp.Trim(), used);
        string? command = suggested is not null && alias is not null
            ? NewNetIpAddressCommand(alias, suggested, prefixLength)
            : suggested is not null
                ? NewNetIpAddressCommand("Ethernet", suggested, prefixLength)
                : null;
        var msg = "This adapter has only the PCB IP. Add a second IPv4 on the same adapter (elevated PowerShell), then pick it for the LM.";
        if (command is not null) msg += " Command: " + command;
        return new SecondaryAddressAdvice(false, same, lmCandidates, suggested, alias, prefixLength, command, msg);
    }

    /// <summary>
    /// Network page decision: a single-cab setup may share the PCB IP; a multi-cab setup should use a second address.
    /// Loopback is never offered for the LM.
    /// </summary>
    public static (string? LmIp, IReadOnlyList<string> Messages) ChooseLm(bool onlyCabOnThisPc, string? pcbIp, string? chosenLmIp, IReadOnlyList<NicAddress> nics)
    {
        var messages = new List<string>();
        if (LoopbackAddress.IsLoopback(chosenLmIp))
        {
            messages.Add(LoopbackAddress.LmRejected);
            return (null, messages);
        }

        if (onlyCabOnThisPc)
        {
            var lm = string.IsNullOrWhiteSpace(chosenLmIp) ? pcbIp : chosenLmIp;
            if (LoopbackAddress.IsLoopback(lm))
            {
                messages.Add(LoopbackAddress.LmRejected);
                return (null, messages);
            }
            if (NetworkBinding.SameIp(pcbIp, lm))
                messages.Add("Single cab: the LM may share the PCB IP. That is fine on this PC.");
            return (lm, messages);
        }

        var advice = Advise(pcbIp, nics);
        messages.Add(advice.Message);
        if (LoopbackAddress.IsLoopback(chosenLmIp))
            return (null, messages);

        if (!string.IsNullOrWhiteSpace(chosenLmIp))
        {
            if (NetworkBinding.SameIp(pcbIp, chosenLmIp))
                messages.Add("You said this PC has more than one cab, but the LM IP is still the same as the PCB IP. Pick a second address.");
            return (chosenLmIp.Trim(), messages);
        }

        if (advice.SuggestedIp is not null && advice.HasSecondAddressOnAdapter)
            return (advice.SuggestedIp, messages);
        if (advice.HasSecondAddressOnAdapter && advice.SuggestedIp is not null)
            return (advice.SuggestedIp, messages);
        if (advice.LmCandidates.Count > 0 && advice.HasSecondAddressOnAdapter)
            return (advice.LmCandidates[0].Address, messages);
        return (advice.HasSecondAddressOnAdapter ? advice.SuggestedIp : null, messages);
    }
}
