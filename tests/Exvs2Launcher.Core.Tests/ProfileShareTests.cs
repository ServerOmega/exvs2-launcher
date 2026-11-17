using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public class ProfileShareTests
{
    private static LauncherProfile Sample() => new()
    {
        Name = "P2", GameFolder = @"D:\EXVS2IB_V29_P2", CardServerPath = @"D:\mm\server\Server-Infinite.exe",
        StartLm = false, PcbIp = "192.168.1.20", LmIp = "192.168.1.30", Serial = "AB12", LastGameWindowTitle = "PCB_14",
        AccessCodePrefix = "99",
    };

    [Fact]
    public void Export_has_no_ips_window_title_or_serial_by_default()
    {
        var json = ProfileShare.Export(Sample(), includeSerial: false);
        Assert.DoesNotContain("192.168.1.20", json);
        Assert.DoesNotContain("192.168.1.30", json);
        Assert.DoesNotContain("PCB_14", json);
        Assert.DoesNotContain("AB12", json);
        Assert.Contains(SharedProfile.FormatName, json);
        Assert.Contains("EXVS2IB_V29_P2", json);
        Assert.Contains("AB12", ProfileShare.Export(Sample(), includeSerial: true));
    }

    [Fact]
    public void Import_round_trip_new_name_has_no_conflict()
    {
        var json = ProfileShare.Export(Sample(), false, new MatchmakingServer { Name = "Friend MM", Host = "mm.example.org", Port = 28090 });
        var file = new ProfileFile();
        var r = ProfileShare.Review(json, file);
        Assert.Null(r.Conflict);
        Assert.Equal("P2", r.Profile.Name);
        Assert.Null(r.Profile.PcbIp);
        ProfileShare.Apply(r, file, overwrite: false);
        Assert.Single(file.Profiles);
        Assert.Contains(file.MatchmakingServers!, s => s.Host == "mm.example.org");
    }

    [Fact]
    public void Import_with_same_name_never_overwrites_unless_confirmed()
    {
        var file = new ProfileFile();
        var existing = new LauncherProfile { Name = "P2", GameFolder = @"E:\mine", Serial = "ZZ99" };
        file.Profiles.Add(existing);
        var r = ProfileShare.Review(ProfileShare.Export(Sample(), false), file);
        Assert.Same(existing, r.Conflict);
        ProfileShare.Apply(r, file, overwrite: false);
        Assert.Equal(2, file.Profiles.Count);
        Assert.Equal(@"E:\mine", file.Profiles[0].GameFolder);
        Assert.Equal("P2 (imported)", file.Profiles[1].Name);

        var file2 = new ProfileFile();
        file2.Profiles.Add(new LauncherProfile { Name = "P2", GameFolder = @"E:\mine", Serial = "ZZ99" });
        var r2 = ProfileShare.Review(ProfileShare.Export(Sample(), false), file2);
        ProfileShare.Apply(r2, file2, overwrite: true);
        Assert.Single(file2.Profiles);
        Assert.Equal(@"D:\EXVS2IB_V29_P2", file2.Profiles[0].GameFolder);
        Assert.Equal("ZZ99", file2.Profiles[0].Serial); // kept from the replaced cab
    }

    [Fact]
    public void Import_as_new_keeps_a_name_the_user_typed_on_the_review_screen()
    {
        var file = new ProfileFile();
        file.Profiles.Add(new LauncherProfile { Name = "P2", GameFolder = @"E:\mine" });
        var r = ProfileShare.Review(ProfileShare.Export(Sample(), false), file);
        r.Profile.Name = "Cab 3";
        ProfileShare.Apply(r, file, overwrite: false);
        Assert.Equal(new[] { "P2", "Cab 3" }, file.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void Import_drops_duplicate_serial_and_stock_server()
    {
        var file = new ProfileFile();
        file.Profiles.Add(new LauncherProfile { Name = "Other", Serial = "AB12" });
        var p = Sample();
        p.CardServerPath = @"D:\EXVS2IB_V29_P2\server\Server.exe";
        var r = ProfileShare.Review(ProfileShare.Export(p, includeSerial: true), file);
        Assert.Null(r.Profile.Serial);
        Assert.Null(r.Profile.CardServerPath);
        Assert.Equal(2, r.Warnings.Count(w => w.Contains("Serial") || w.Contains("stock")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"exvs2-launcher-profile\",\"version\":99,\"name\":\"x\"}")]
    public void Import_rejects_non_profiles(string json) =>
        Assert.Throws<InvalidDataException>(() => ProfileShare.Review(json, new ProfileFile()));

    [Fact]
    public void Imported_fields_are_clamped_and_cleaned()
    {
        var json = "{\"format\":\"exvs2-launcher-profile\",\"version\":1,\"name\":\"Cab\\u0007X\",\"cardServerHost\":\"evil host\",\"cardServerPort\":99999,\"portTimeoutSeconds\":1,\"accessCodePrefix\":\"9a\"}";
        var r = ProfileShare.Review(json, new ProfileFile());
        Assert.Equal("CabX", r.Profile.Name);
        Assert.Equal("127.0.0.1", r.Profile.CardServerHost);
        Assert.Equal(28080, r.Profile.CardServerPort);
        Assert.Equal(5, r.Profile.PortTimeoutSeconds);
        Assert.Equal("", r.Profile.AccessCodePrefix);
    }
}
