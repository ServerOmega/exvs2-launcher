using Exvs2Launcher.Core.Files;

namespace Exvs2Launcher.Core.Tests;

public class CardServerInspectorTests
{
    [Fact]
    public void Reads_version_from_parent_folder_name_and_warns_on_old_prerelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "exvs2-csi-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "exvs2-matchmaking-v0.1.0-pre", "exvs2-matchmaking-v0.1.0-pre");
        try
        {
            Directory.CreateDirectory(dir);
            var bat = Path.Combine(dir, "3-Start-Server.bat");
            File.WriteAllText(bat, "rem fake");
            var info = CardServerInspector.Inspect(bat);
            Assert.Equal("v0.1.0-pre", info.VersionLabel);
            Assert.True(info.LooksOld);
            Assert.Contains("v0.1.0-pre", info.Warning);
            Assert.Contains("v0.4.1-pre", info.Warning);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Current_prerelease_is_not_flagged_old_and_reads_BUILD_COMMIT()
    {
        var root = Path.Combine(Path.GetTempPath(), "exvs2-csi-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "exvs2-matchmaking-v0.4.1-pre");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "BUILD_COMMIT"), "abc1234\n");
            var exe = Path.Combine(dir, "Server-Infinite.exe");
            File.WriteAllText(exe, "fake");
            var info = CardServerInspector.Inspect(exe);
            Assert.Equal("v0.4.1-pre", info.VersionLabel);
            Assert.False(info.LooksOld);
            Assert.Null(info.Warning);
            Assert.Contains("BUILD_COMMIT abc1234", info.Detail);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Theory]
    [InlineData("v0.3.9", true)]
    [InlineData("v0.4.0", false)]
    [InlineData("v0.4.0-pre", false)]
    [InlineData("1.2.0", false)]
    [InlineData(null, false)]
    public void LooksLikeOldRelease(string? label, bool old) =>
        Assert.Equal(old, CardServerInspector.LooksLikeOldRelease(label));
}
