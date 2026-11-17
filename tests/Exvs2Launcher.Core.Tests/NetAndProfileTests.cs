using System.Net.NetworkInformation;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public class NetAndProfileTests
{
    [Fact]
    public void Picks_real_lan_adapter_over_virtual_ones()
    {
        var list = new[]
        {
            new NicAddress("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", "26.1.2.3", NetworkInterfaceType.Ethernet, false, true),
            new NicAddress("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.20.0.1", NetworkInterfaceType.Ethernet, false, true),
            new NicAddress("Wi-Fi", "Intel Wi-Fi 6", "192.168.1.70", NetworkInterfaceType.Wireless80211, true, false),
            new NicAddress("Ethernet", "Realtek PCIe GbE", "192.168.1.69", NetworkInterfaceType.Ethernet, true, false),
        };
        Assert.Equal("192.168.1.69", LanAddressDetector.PickBest(list)!.Address);
        Assert.Equal("192.168.1.70", LanAddressDetector.PickBest(list.Where(n => n.InterfaceName != "Ethernet"))!.Address);
        Assert.Equal("26.1.2.3", LanAddressDetector.PickBest(list.Take(1))!.Address);
        Assert.Null(LanAddressDetector.PickBest(Array.Empty<NicAddress>()));
    }

    [Theory]
    [InlineData("Radmin VPN", "", true)]
    [InlineData("Ethernet 3", "TAP-Windows Adapter V9", true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", false)]
    public void Virtual_detection(string name, string desc, bool expected) =>
        Assert.Equal(expected, LanAddressDetector.LooksVirtual(name, desc));

    [Theory]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.3.3", true)]
    [InlineData("172.32.3.3", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("nope", false)]
    public void Private_ranges(string ip, bool expected) => Assert.Equal(expected, LanAddressDetector.IsPrivate(ip));

    [Fact]
    public void Enumerate_does_not_throw()
    {
        var all = LanAddressDetector.Enumerate();
        Assert.All(all, n => Assert.DoesNotContain(n.Address, new[] { "127.0.0.1" }));
    }

    [Fact]
    public void Profile_store_roundtrip_and_folder_lookup()
    {
        var path = Path.Combine(Path.GetTempPath(), "profiles-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new ProfileStore(path);
            Assert.Empty(store.Load().Profiles);
            var file = new ProfileFile { LastSelected = "Test cab", LastSelectedTab = "Card Server" };
            file.Profiles.Add(new LauncherProfile
            {
                Name = "Test cab", GameFolder = "/games/Test", CardServerPath = "/mm/3-Start-Server.bat",
                StartLm = true, Serial = "p8SJ", AccessCodePrefix = "99",
            });
            store.Save(file);

            var back = store.Load();
            Assert.Equal("Test cab", back.LastSelected);
            Assert.Equal("Card Server", back.LastSelectedTab);
            var p = Assert.Single(back.Profiles);
            Assert.Equal("/mm/3-Start-Server.bat", p.CardServerPath);
            Assert.True(p.StartLm);
            Assert.Equal(28080, p.CardServerPort);
            Assert.Same(p, ProfileStore.FindByFolder(back, "/games/Test/"));
            Assert.Null(ProfileStore.FindByFolder(back, "/games/Other"));

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("accessCode\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chipId", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Profile_to_plan()
    {
        var plan = new LauncherProfile { GameFolder = "/g", CardServerPath = "/s.exe", PortTimeoutSeconds = 1, LmToClientDelaySeconds = -4 }.ToStartPlan();
        Assert.Equal(TimeSpan.FromSeconds(5), plan.PortTimeout);
        Assert.Equal(TimeSpan.Zero, plan.LmToClientDelay);
        Assert.Equal(28080, plan.Port);
        Assert.Equal("127.0.0.1", plan.Host);
    }
}
