using System.Net;

namespace Exvs2Launcher.Core.Net;

/// <summary>
/// Decides which PCB (client) and LM IPs to write. The two are independent: the LM binds UDP 48000/49000
/// on <c>lmnfig.ini</c> <c>IpAddress</c>, and the client binds a nearby port on <c>config.ini</c> <c>IpAddress</c>.
/// A shared address is fine for a single cab; a separate LM address is needed when more than one cab or LM
/// runs on the same PC. <c>lmnfig.ini</c> is authoritative for the LM; auto-detect never overwrites it.
/// </summary>
public sealed record NetworkDecision(
    string? PcbIp,
    string? LmIp,
    IReadOnlyList<string> Messages,
    bool LmEqualsPcb,
    bool KeptExistingLmIp);

public static class NetworkBinding
{
    public const string SameAddressNote =
        "PCB IP and LM IP are the same. That is fine for a single cab. If you run more than one cab or more than one LM on this PC, give the LM its own IPv4 on the same adapter (a secondary address) so each process can bind its ports. A separate LM address is recommended in that case.";

    public static bool SameIp(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (IPAddress.TryParse(a.Trim(), out var x) && IPAddress.TryParse(b.Trim(), out var y))
            return x.Equals(y);
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static NicAddress? FindLocal(string? ip, IEnumerable<NicAddress> nics) =>
        string.IsNullOrWhiteSpace(ip) ? null : nics.FirstOrDefault(n => SameIp(n.Address, ip));

    /// <summary>
    /// Auto-detect fills only the PCB IP. An existing <paramref name="currentLmIp"/> from lmnfig.ini is kept
    /// whenever auto-detect is on, even if it is not currently on a local adapter (the user is asked to pick
    /// from the list instead of silently overwriting). When auto-detect is off, <paramref name="profileLmIp"/>
    /// is used and a change to lmnfig.ini is reported in <see cref="NetworkDecision.Messages"/>.
    /// </summary>
    public static NetworkDecision Resolve(
        string? currentPcbIp,
        string? currentLmIp,
        string? profilePcbIp,
        string? profileLmIp,
        bool autoDetect,
        IReadOnlyList<NicAddress> nics)
    {
        var messages = new List<string>();
        currentLmIp = DropUnusable(currentLmIp, "LM", nics, messages);
        profileLmIp = DropUnusable(profileLmIp, "LM", nics, messages);
        currentPcbIp = DropUnusable(currentPcbIp, "PCB", nics, messages);
        profilePcbIp = DropUnusable(profilePcbIp, "PCB", nics, messages);
        string? pcb = ResolvePcb(currentPcbIp, profilePcbIp, autoDetect, nics, messages);
        var reasonPcb = BindAddress.RejectReason(pcb, "PCB", nics);
        if (reasonPcb is not null)
        {
            messages.Add(reasonPcb);
            pcb = null;
        }
        var (lm, kept) = ResolveLm(currentLmIp, profileLmIp, autoDetect, nics, messages);
        var reasonLm = BindAddress.RejectReason(lm, "LM", nics);
        if (reasonLm is not null)
        {
            messages.Add(reasonLm + " That value will not be kept.");
            lm = null;
            kept = false;
        }

        bool same = SameIp(pcb, lm);
        if (same)
        {
            var others = nics.Where(n => !SameIp(n.Address, pcb)).Select(n => n.Display).Distinct().ToList();
            var msg = SameAddressNote;
            if (others.Count > 0)
                msg += " Other local addresses: " + string.Join("; ", others) + ".";
            messages.Add(msg);
        }

        return new NetworkDecision(pcb, lm, messages, same, kept);
    }

    private static string? ResolvePcb(
        string? currentPcbIp, string? profilePcbIp, bool autoDetect,
        IReadOnlyList<NicAddress> nics, List<string> messages)
    {
        if (!autoDetect) return First(profilePcbIp, currentPcbIp);

        var best = LanAddressDetector.PickBest(nics);
        if (best is null)
        {
            var fallback = First(profilePcbIp, currentPcbIp);
            messages.Add("No network adapter found for auto-detect; PCB IP left as " + (fallback ?? "it is") + ".");
            return fallback;
        }

        messages.Add("This PC's LAN IP (PCB): " + best.Display);
        if (!SameIp(currentPcbIp, best.Address))
            messages.Add($"config.ini IpAddress: {currentPcbIp ?? "(none)"} → {best.Address}");
        else
            messages.Add($"config.ini IpAddress already {best.Address}.");
        return best.Address;
    }

    private static (string? Lm, bool KeptExisting) ResolveLm(
        string? currentLmIp, string? profileLmIp, bool autoDetect,
        IReadOnlyList<NicAddress> nics, List<string> messages)
    {
        if (!string.IsNullOrWhiteSpace(currentLmIp))
        {
            var existing = currentLmIp.Trim();
            if (autoDetect)
            {
                var assigned = FindLocal(existing, nics);
                if (assigned is not null)
                    messages.Add($"lmnfig.ini IpAddress left as {existing} ({assigned.Display}). Auto-detect does not change the LM IP; lmnfig.ini is authoritative.");
                else
                    messages.Add($"lmnfig.ini IpAddress left as {existing} (not assigned to a local adapter). Auto-detect does not change it. Pick a local address from the LM IP list.");
                return (existing, true);
            }

            var chosen = First(profileLmIp, existing);
            if (!SameIp(existing, chosen))
                messages.Add($"lmnfig.ini IpAddress: {existing} → {chosen}");
            else
                messages.Add($"lmnfig.ini IpAddress left as {existing}.");
            return (chosen, SameIp(existing, chosen));
        }

        if (autoDetect)
        {
            // Do not fill the LM with the detected PCB IP.
            if (FindLocal(profileLmIp, nics) is { } assigned)
            {
                messages.Add($"lmnfig.ini has no IpAddress; using the profile LM IP {assigned.Address} ({assigned.Display}).");
                return (assigned.Address, false);
            }
            messages.Add("lmnfig.ini has no IpAddress. Pick an LM IP from this PC's addresses. A separate address from the PCB IP is recommended when running multiple cabs on one PC.");
            return (null, false);
        }

        if (!string.IsNullOrWhiteSpace(profileLmIp))
        {
            messages.Add($"lmnfig.ini IpAddress: (none) → {profileLmIp.Trim()}");
            return (profileLmIp.Trim(), false);
        }
        return (null, false);
    }

    private static string? DropUnusable(string? ip, string label, IReadOnlyList<NicAddress> nics, List<string> messages)
    {
        var reason = BindAddress.RejectReason(ip, label, nics);
        if (reason is null) return ip;
        messages.Add(label + " cannot be used. " + reason + " That value will not be kept.");
        return null;
    }

    private static string? First(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : !string.IsNullOrWhiteSpace(b) ? b.Trim() : null;
}
