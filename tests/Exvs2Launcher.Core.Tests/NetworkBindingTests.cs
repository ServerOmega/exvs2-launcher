using System.Net.NetworkInformation;
using Exvs2Launcher.Core.Net;

namespace Exvs2Launcher.Core.Tests;

public class NetworkBindingTests
{
    private static readonly NicAddress Primary = new("Ethernet", "Realtek", "192.168.1.69", NetworkInterfaceType.Ethernet, true, false);
    private static readonly NicAddress Secondary = new("Ethernet", "Realtek", "192.168.1.245", NetworkInterfaceType.Ethernet, true, false);
    private static readonly IReadOnlyList<NicAddress> Nics = new[] { Primary, Secondary };

    [Fact]
    public void Auto_detect_fills_only_pcb_and_keeps_existing_local_lm_ip()
    {
        var d = NetworkBinding.Resolve(
            currentPcbIp: "192.168.1.69",
            currentLmIp: "192.168.1.245",
            profilePcbIp: null,
            profileLmIp: "192.168.1.69", // stale profile must not win over lmnfig
            autoDetect: true,
            nics: Nics);

        Assert.Equal("192.168.1.69", d.PcbIp);
        Assert.Equal("192.168.1.245", d.LmIp);
        Assert.True(d.KeptExistingLmIp);
        Assert.False(d.LmEqualsPcb);
        Assert.Contains(d.Messages, m => m.Contains("lmnfig.ini IpAddress left as 192.168.1.245") && m.Contains("authoritative"));
        Assert.DoesNotContain(d.Messages, m => m.Contains("192.168.1.245 →"));
        Assert.Contains(d.Messages, m => m.Contains("This PC's LAN IP (PCB):") && m.Contains("192.168.1.69"));
    }

    [Fact]
    public void Auto_detect_does_not_overwrite_lmnfig_even_when_that_address_is_not_local()
    {
        var d = NetworkBinding.Resolve(
            "192.168.1.69", "10.9.9.9", null, "192.168.1.69",
            autoDetect: true, nics: Nics);

        Assert.Equal("10.9.9.9", d.LmIp);
        Assert.True(d.KeptExistingLmIp);
        Assert.Contains(d.Messages, m => m.Contains("not assigned to a local adapter") && m.Contains("10.9.9.9"));
    }

    [Fact]
    public void Auto_detect_does_not_fill_missing_lm_ip_with_the_pcb_address()
    {
        var d = NetworkBinding.Resolve(
            "192.168.1.69", currentLmIp: null, profilePcbIp: null, profileLmIp: null,
            autoDetect: true, nics: Nics);

        Assert.Equal("192.168.1.69", d.PcbIp);
        Assert.Null(d.LmIp);
        Assert.False(d.KeptExistingLmIp);
        Assert.Contains(d.Messages, m => m.Contains("lmnfig.ini has no IpAddress") && m.Contains("Pick an LM IP"));
    }

    [Fact]
    public void Without_auto_detect_an_explicit_lm_ip_is_applied_and_logged()
    {
        var d = NetworkBinding.Resolve(
            "192.168.1.69", "192.168.1.69", "192.168.1.69", "192.168.1.245",
            autoDetect: false, nics: Nics);

        Assert.Equal("192.168.1.69", d.PcbIp);
        Assert.Equal("192.168.1.245", d.LmIp);
        Assert.Contains(d.Messages, m => m.Contains("lmnfig.ini IpAddress: 192.168.1.69 → 192.168.1.245"));
        Assert.DoesNotContain(d.Messages, m => m.Contains("This PC's LAN IP (PCB)"));
    }

    [Fact]
    public void Same_pcb_and_lm_note_is_advisory_not_an_error()
    {
        var d = NetworkBinding.Resolve(
            "192.168.1.69", "192.168.1.69", null, null,
            autoDetect: true, nics: Nics);

        Assert.True(d.LmEqualsPcb);
        Assert.Equal("192.168.1.69", d.LmIp);
        Assert.Contains(d.Messages, m => m.Contains("fine for a single cab"));
        Assert.Contains(d.Messages, m => m.Contains("more than one cab") || m.Contains("more than one LM"));
        Assert.DoesNotContain(d.Messages, m => m.Contains("21-01"));
        Assert.Contains(d.Messages, m => m.Contains("192.168.1.245")); // lists the other local address
    }

    [Fact]
    public void FindLocal_matches_any_address_on_the_adapter_including_secondary()
    {
        Assert.Equal("192.168.1.245", NetworkBinding.FindLocal("192.168.1.245", Nics)!.Address);
        Assert.Equal("192.168.1.69", NetworkBinding.FindLocal("192.168.1.69", Nics)!.Address);
        Assert.Null(NetworkBinding.FindLocal("10.0.0.1", Nics));
        Assert.True(NetworkBinding.SameIp(" 192.168.1.69 ", "192.168.1.69"));
        Assert.False(NetworkBinding.SameIp("192.168.1.69", "192.168.1.245"));
    }
}
