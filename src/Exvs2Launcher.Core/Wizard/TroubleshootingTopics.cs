namespace Exvs2Launcher.Core.Wizard;

/// <summary>
/// Plain-English explanations of common errors. English copy lives here so Core tests can lock it;
/// the app also exposes the same text through resource strings for later translation.
/// </summary>
public sealed record TroubleTopic(string Id, string Title, string Body);

public static class TroubleshootingTopics
{
    public const string LmDisconnectId = "21-01";
    public const string CardInUseId = "card-in-use";
    public const string CardServerId = "card-server";
    public const string FirewallId = "firewall";

    public static readonly TroubleTopic LmDisconnect = new(
        LmDisconnectId,
        "21-01 Live Monitor disconnect",
        "Error 21-01 means the game could not reach the Live Monitor (LM). Check that the LM is running, that lmnfig.ini IpAddress is a LAN address (not 127.0.0.1), and that the cab and LM are on the same subnet. If more than one cab or LM runs on this PC, give the LM its own IPv4 on the same adapter. Sharing one IP is fine for a single cab as long as the LM is not pinned to an address the game also binds.");

    public static readonly TroubleTopic CardInUse = new(
        CardInUseId,
        "Card in use / このカードは使用中です",
        "The card server thinks this card is already logged in. Close the other game window that is using the same card, wait a moment, then insert the card again. If no other cab is using it, restart only this profile's card server from the launcher. Two profiles that share the same card.ini will also trigger this.");

    public static readonly TroubleTopic CardServer = new(
        CardServerId,
        "Card server not running",
        "The game talks to the patched card server (Server-Infinite.exe) first. In the launcher, pick the latest matchmaking package (v0.4.0 or newer), click Start on Card server, and confirm the port (usually 28080) answers. Do not point the launcher at the pack's stock server\\Server.exe.");

    public static readonly TroubleTopic Firewall = new(
        FirewallId,
        "Firewall or TinyWall blocking",
        "If the card server, LM, or game cannot accept connections, your firewall may be blocking them. The launcher never changes firewall rules. Allow the exact programs listed on the Checks page: vsac29_Release.exe, vsac29_Release_LM.exe, and Server-Infinite.exe. In TinyWall, add those same exes to the allow list.");

    public static IReadOnlyList<TroubleTopic> All { get; } = new[]
    {
        LmDisconnect, CardInUse, CardServer, Firewall,
    };

    public static TroubleTopic? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
