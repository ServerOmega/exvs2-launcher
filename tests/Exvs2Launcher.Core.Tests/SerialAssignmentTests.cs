using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.Core.Tests;

public class SerialAssignmentTests
{
    private static IdentityGenerator Ids(string serial)
    {
        // NewSerial draws 4 alphabet indexes; pick the indexes that spell `serial`.
        var alphabet = IdentityGenerator.SerialAlphabet;
        var idx = serial.Select(c => alphabet.IndexOf(c)).ToArray();
        if (idx.Any(i => i < 0)) throw new ArgumentException(serial);
        var q = new Queue<int>(idx);
        return new IdentityGenerator(_ => q.Dequeue());
    }

    [Fact]
    public void New_profile_without_a_folder_gets_a_unique_serial_and_writes_nothing()
    {
        using var existing = new TempGameFolder(); // serial jZJj
        var file = new ProfileFile();
        file.Profiles.Add(new LauncherProfile { Name = "A", GameFolder = existing.Root, Serial = "jZJj" });
        var p = new LauncherProfile { Name = "New" };
        file.Profiles.Add(p);

        var d = SerialAssignment.ForNewProfile(file, p, Ids("p8SJ"));
        Assert.Equal("p8SJ", d.Serial);
        Assert.Equal("p8SJ", p.Serial);
        Assert.Equal(SerialAction.GeneratedOnProfile, d.Action);
        Assert.Equal(TempGameFolder.Config, existing.Text("config.ini")); // existing cab untouched
        Assert.Empty(existing.Backups("config.ini"));
    }

    [Fact]
    public void New_profile_adopts_a_unique_folder_serial_and_does_not_write()
    {
        using var g = new TempGameFolder(); // serial jZJj
        var p = new LauncherProfile { Name = "New", GameFolder = g.Root, Serial = "AAAA" };
        var file = new ProfileFile { Profiles = { p } };

        var d = SerialAssignment.ForNewProfile(file, p, Ids("p8SJ"));
        Assert.Equal("jZJj", d.Serial);
        Assert.Equal("jZJj", p.Serial);
        Assert.Equal(SerialAction.AdoptedExisting, d.Action);
        Assert.Equal(TempGameFolder.Config, g.Text("config.ini"));
        Assert.Empty(g.Backups("config.ini"));
        Assert.Contains("was not changed", d.Message);
    }

    [Fact]
    public void New_profile_gets_a_new_serial_when_the_folder_serial_is_already_taken()
    {
        using var a = new TempGameFolder(); // jZJj
        using var b = new TempGameFolder(); // also jZJj
        var pa = new LauncherProfile { Name = "A", GameFolder = a.Root, Serial = "jZJj" };
        var pb = new LauncherProfile { Name = "B", GameFolder = b.Root };
        var file = new ProfileFile { Profiles = { pa, pb } };

        var d = SerialAssignment.ForGameFolder(file, pb, Ids("p8SJ"));
        Assert.Equal("p8SJ", d.Serial);
        Assert.Equal("p8SJ", pb.Serial);
        Assert.Equal(SerialAction.WroteBecauseConflict, d.Action);
        Assert.Contains("serial =p8SJ", b.Text("config.ini"));
        Assert.Contains("Serial=p8SJ", b.Text("SnowLauncher.ini"));
        Assert.Equal(TempGameFolder.Config, a.Text("config.ini")); // the other cab is not touched
        Assert.Contains("already used", d.Message);
    }

    [Fact]
    public void Folder_without_a_serial_receives_the_profile_serial()
    {
        using var g = new TempGameFolder();
        // Strip the serial line's value.
        File.WriteAllText(g.P("config.ini"), TempGameFolder.Config.Replace("serial =jZJj", "serial ="));
        var p = new LauncherProfile { Name = "New", GameFolder = g.Root, Serial = "Wxyz" };
        var file = new ProfileFile { Profiles = { p } };

        var d = SerialAssignment.ForGameFolder(file, p, Ids("p8SJ"));
        Assert.Equal("Wxyz", d.Serial);
        Assert.Equal(SerialAction.WroteBecauseMissing, d.Action);
        Assert.Contains("serial =Wxyz", g.Text("config.ini"));
    }

    [Fact]
    public void Folder_without_a_serial_gets_a_generated_one_if_the_profile_has_none()
    {
        using var g = new TempGameFolder();
        File.WriteAllText(g.P("config.ini"), TempGameFolder.Config.Replace("serial =jZJj\r\n", ""));
        var p = new LauncherProfile { Name = "New", GameFolder = g.Root };
        var file = new ProfileFile { Profiles = { p } };

        var d = SerialAssignment.ForGameFolder(file, p, Ids("p8SJ"));
        Assert.Equal("p8SJ", d.Serial);
        Assert.Equal(SerialAction.WroteBecauseMissing, d.Action);
        Assert.Contains("p8SJ", g.Text("config.ini"));
    }

    [Fact]
    public void Existing_unique_folder_serial_is_never_replaced_even_if_the_profile_has_another()
    {
        using var g = new TempGameFolder(); // jZJj
        var p = new LauncherProfile { Name = "Existing", GameFolder = g.Root, Serial = "AbCd" };
        var file = new ProfileFile { Profiles = { p } };

        var d = SerialAssignment.ForGameFolder(file, p, Ids("p8SJ"));
        Assert.Equal(SerialAction.AdoptedExisting, d.Action);
        Assert.Equal("jZJj", p.Serial);
        Assert.Equal(TempGameFolder.Config, g.Text("config.ini"));
        Assert.Empty(g.Backups("config.ini"));
    }
}
