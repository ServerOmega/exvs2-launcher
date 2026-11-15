using System.Text;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public sealed class TempGameFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "exvs2-launcher-test-" + Guid.NewGuid().ToString("N"));

    public const string Config =
        "[config]\r\n# Game mode, Client or LM.\r\nmode =Client\r\nserial =jZJj\r\n\r\nServer =127.0.0.1:28080\r\n" +
        "# IpAddress=10.158.91.99\r\nIpAddress=192.168.1.69\r\n\r\n[keyboard]\r\nA =J,U,Y,T\r\n";
    public const string Lm = "[config]\r\nWindowed=True\r\nServer=127.0.0.1:28080\r\nMode=LM\r\nIpAddress=192.168.1.69\r\n[display]\r\nResolution=144p\r\n";
    public const string Card = "[card]\r\naccessCode=11112222333344445555\r\nchipId=0000000000000000000000000000ABCD\r\n";
    public const string Snow = "[AppSettings]\r\nStartClient=True\r\n[Game_Config]\r\nSerial=jZJj\r\nPCB_IpAddress=192.168.1.69\r\nLM_IpAddress=192.168.1.69\r\nChipId=0000000000000000000000000000ABCD\r\nAccessCode=11112222333344445555\r\nFramerateLimit=True\r\n";

    public TempGameFolder(bool withSnow = true, bool withCard = true, bool withLm = true)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(P("config.ini"), Config);
        if (withLm) File.WriteAllText(P("lmnfig.ini"), Lm);
        if (withCard) File.WriteAllText(P("card.ini"), Card);
        if (withSnow) File.WriteAllText(P("SnowLauncher.ini"), Snow);
    }

    public string P(string name) => Path.Combine(Root, name);
    public string Text(string name) => File.ReadAllText(P(name));
    public string[] Backups(string name) => Directory.GetFiles(Root, name + ".bak-*");
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class GameConfigWriterTests
{
    private static GameConfigWriter Writer() => new(new BackupService(() => new DateTime(2026, 11, 15, 10, 30, 0)));

    [Fact]
    public void Serial_written_to_config_and_snowlauncher_with_backups()
    {
        using var g = new TempGameFolder();
        var r = Writer().ApplySerial(g.Root, "p8SJ", syncSnowLauncher: true);

        Assert.Equal(TempGameFolder.Config.Replace("serial =jZJj", "serial =p8SJ"), g.Text("config.ini"));
        Assert.Equal(TempGameFolder.Snow.Replace("Serial=jZJj", "Serial=p8SJ"), g.Text("SnowLauncher.ini"));
        Assert.Equal(TempGameFolder.Config, File.ReadAllText(g.P("config.ini.bak-20261115-103000")));
        Assert.Single(g.Backups("SnowLauncher.ini"));
        Assert.Equal(2, r.Written.Count);
        Assert.Equal(TempGameFolder.Lm, g.Text("lmnfig.ini"));
    }

    [Fact]
    public void Serial_without_snowlauncher_sync_leaves_it_alone()
    {
        using var g = new TempGameFolder();
        Writer().ApplySerial(g.Root, "p8SJ", syncSnowLauncher: false);
        Assert.Equal(TempGameFolder.Snow, g.Text("SnowLauncher.ini"));
        Assert.Empty(g.Backups("SnowLauncher.ini"));
    }

    [Fact]
    public void Same_serial_writes_nothing_and_makes_no_backup()
    {
        using var g = new TempGameFolder();
        var r = Writer().ApplySerial(g.Root, "jZJj", true);
        Assert.Empty(r.Written);
        Assert.Empty(r.Backups);
        Assert.Empty(g.Backups("config.ini"));
    }

    [Fact]
    public void Second_backup_in_same_second_gets_suffix()
    {
        using var g = new TempGameFolder(withSnow: false);
        var w = Writer();
        w.ApplySerial(g.Root, "AAAA", false);
        w.ApplySerial(g.Root, "BBBB", false);
        Assert.True(File.Exists(g.P("config.ini.bak-20261115-103000")));
        Assert.True(File.Exists(g.P("config.ini.bak-20261115-103000-2")));
        Assert.Contains("serial =AAAA", File.ReadAllText(g.P("config.ini.bak-20261115-103000-2")));
    }

    [Fact]
    public void Card_written_to_card_ini_and_snowlauncher()
    {
        using var g = new TempGameFolder();
        var card = new IdentityGenerator().NewCard();
        var r = Writer().ApplyCard(g.Root, card, true);

        Assert.Equal($"[card]\r\naccessCode={card.AccessCode}\r\nchipId={card.ChipId}\r\n", g.Text("card.ini"));
        var snow = g.Text("SnowLauncher.ini");
        Assert.Contains($"\r\nChipId={card.ChipId}\r\n", snow);
        Assert.Contains($"\r\nAccessCode={card.AccessCode}\r\n", snow);
        Assert.Contains("FramerateLimit=True", snow);
        Assert.Equal(TempGameFolder.Card, File.ReadAllText(g.Backups("card.ini").Single()));
        Assert.DoesNotContain(r.Notes, n => n.Contains(card.AccessCode) || n.Contains(card.ChipId));

        var state = Writer().Read(g.Root);
        Assert.True(state.CardPresent);
        Assert.True(state.CardValid);
    }

    [Fact]
    public void Card_ini_is_created_when_missing()
    {
        using var g = new TempGameFolder(withSnow: false, withCard: false);
        var card = new IdentityGenerator().NewCard("99");
        var r = Writer().ApplyCard(g.Root, card, true);
        Assert.Empty(r.Backups);
        Assert.Equal($"[card]\r\naccessCode={card.AccessCode}\r\nchipId={card.ChipId}\r\n", g.Text("card.ini"));
    }

    [Fact]
    public void Card_keeps_existing_key_spelling()
    {
        using var g = new TempGameFolder(withSnow: false);
        File.WriteAllText(g.P("card.ini"), "[card]\nAccessCode = 1\nChipId = 2\n");
        var card = new IdentityGenerator().NewCard();
        Writer().ApplyCard(g.Root, card, false);
        Assert.Equal($"[card]\nAccessCode = {card.AccessCode}\nChipId = {card.ChipId}\n", g.Text("card.ini"));
    }

    [Fact]
    public void Network_written_to_both_game_inis_and_snowlauncher()
    {
        using var g = new TempGameFolder();
        Writer().ApplyNetwork(g.Root, "192.168.1.50", "192.168.1.51", "127.0.0.1:28080", true);

        Assert.Equal(TempGameFolder.Config.Replace("IpAddress=192.168.1.69", "IpAddress=192.168.1.50"), g.Text("config.ini"));
        Assert.Contains("# IpAddress=10.158.91.99", g.Text("config.ini"));
        Assert.Equal(TempGameFolder.Lm.Replace("192.168.1.69", "192.168.1.51"), g.Text("lmnfig.ini"));
        var snow = g.Text("SnowLauncher.ini");
        Assert.Contains("PCB_IpAddress=192.168.1.50", snow);
        Assert.Contains("LM_IpAddress=192.168.1.51", snow);
        Assert.Contains("Server=127.0.0.1:28080", snow);
    }

    [Fact]
    public void Network_with_missing_lmnfig_adds_note()
    {
        using var g = new TempGameFolder(withLm: false, withSnow: false);
        var r = Writer().ApplyNetwork(g.Root, "192.168.1.50", "192.168.1.50", null, false);
        Assert.Contains(r.Notes, n => n.Contains("lmnfig.ini"));
        Assert.False(File.Exists(g.P("lmnfig.ini")));
    }

    [Fact]
    public void Read_reports_state_without_card_values()
    {
        using var g = new TempGameFolder();
        var s = Writer().Read(g.Root);
        Assert.Equal("jZJj", s.Serial);
        Assert.Equal("jZJj", s.SnowLauncherSerial);
        Assert.Equal("192.168.1.69", s.ClientIp);
        Assert.Equal("192.168.1.69", s.LmIp);
        Assert.Equal("127.0.0.1:28080", s.ClientServer);
        Assert.True(s.CardValid);
        Assert.DoesNotContain("11112222", s.ToString());
    }

    [Fact]
    public void Bad_serial_is_rejected()
    {
        using var g = new TempGameFolder();
        Assert.Throws<ArgumentException>(() => Writer().ApplySerial(g.Root, "abc", false));
        Assert.Throws<ArgumentException>(() => Writer().ApplySerial(g.Root, "ab=c", false));
    }

    [Fact]
    public void Process_guard_lists_conflicts()
    {
        var running = ProcessGuard.RunningConflicts(n => n == "SnowLauncher_English" ? 1 : 0);
        Assert.Equal(new[] { "SnowLauncher_English.exe" }, running);
    }

    [Fact]
    public void Serial_registry_finds_other_cabs()
    {
        using var a = new TempGameFolder();
        using var b = new TempGameFolder();
        var pa = new LauncherProfile { Name = "A", GameFolder = a.Root };
        var pb = new LauncherProfile { Name = "B", GameFolder = b.Root };
        var file = new ProfileFile { Profiles = { pa, pb, new LauncherProfile { Name = "C", Serial = "Zzzz", GameFolder = "/nope" } } };

        var taken = SerialRegistry.TakenSerials(file, pa);
        Assert.Contains("jzjj", taken, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ZZZZ", taken, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new[] { "B" }, SerialRegistry.Duplicates(file, pa, "jZJj"));
    }
}
