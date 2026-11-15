using Exvs2Launcher.Core.Launch;

namespace Exvs2Launcher.Core.Tests;

public class ProfileProcessMatcherTests
{
    private static StartPlan Plan(
        string game = "/games/EXVS2IB_V29",
        string? server = "/mm/v0.4.1-pre/3-Start-Server.bat") => new()
    {
        GameFolder = game,
        CardServerPath = server,
        StartCardServer = true,
        StartLm = true,
        StartClient = true,
    };

    [Fact]
    public void Client_matches_only_the_exe_in_this_profile_folder()
    {
        var plan = Plan();
        var running = new[]
        {
            new RunningProcessInfo(10, "/games/EXVS2IB_V29_P2/vsac29_Release.exe", "vsac29_Release"),
            new RunningProcessInfo(11, "/games/EXVS2IB_V29/vsac29_Release.exe", "vsac29_Release"),
            new RunningProcessInfo(12, "/games/Test/vsac29_Release.exe", "vsac29_Release"),
        };
        var hits = ProfileProcessMatcher.Find(ComponentRoles.Client, plan, Array.Empty<IStartedProcess>(), running);
        Assert.Equal(new[] { 11 }, hits.Select(h => h.Pid));
        Assert.Equal("this profile's folder", hits[0].How);
    }

    [Fact]
    public void Lm_matches_only_this_folder_and_ignores_name_only_entries()
    {
        var plan = Plan();
        var running = new[]
        {
            new RunningProcessInfo(20, null, "vsac29_Release_LM"), // name only: never a match
            new RunningProcessInfo(21, "/games/EXVS2IB_V29/vsac29_Release_LM.exe", "vsac29_Release_LM"),
            new RunningProcessInfo(22, "/games/EXVS2IB_V29_P3/vsac29_Release_LM.exe", "vsac29_Release_LM"),
        };
        var hits = ProfileProcessMatcher.Find(ComponentRoles.Lm, plan, Array.Empty<IStartedProcess>(), running);
        Assert.Equal(new[] { 21 }, hits.Select(h => h.Pid));
    }

    [Fact]
    public void Card_server_matches_Server_Infinite_next_to_the_profile_bat_not_another_package()
    {
        var plan = Plan(server: "/mm/v0.4.1-pre/3-Start-Server.bat");
        var running = new[]
        {
            new RunningProcessInfo(30, "/downloads/exvs2-matchmaking-v0.1.0-pre/server/Server-Infinite.exe", "Server-Infinite"),
            new RunningProcessInfo(31, "/mm/v0.4.1-pre/Server-Infinite.exe", "Server-Infinite"),
            new RunningProcessInfo(32, "/mm/v0.4.1-pre/server/Server.exe", "Server"), // stock, never
        };
        var hits = ProfileProcessMatcher.Find(ComponentRoles.CardServer, plan, Array.Empty<IStartedProcess>(), running);
        Assert.Equal(new[] { 31 }, hits.Select(h => h.Pid));
    }

    [Fact]
    public void Started_pid_is_preferred_and_path_fallback_is_added_once()
    {
        var plan = Plan();
        var started = new IStartedProcess[] { new FakeProcess(ComponentRoles.Client, 11) };
        var running = new[]
        {
            new RunningProcessInfo(11, "/games/EXVS2IB_V29/vsac29_Release.exe", "vsac29_Release"),
        };
        var hits = ProfileProcessMatcher.Find(ComponentRoles.Client, plan, started, running);
        Assert.Equal(new[] { 11 }, hits.Select(h => h.Pid));
        Assert.Equal("started by this launcher", hits[0].How);
    }

    [Fact]
    public void Exited_started_process_falls_back_to_folder_path()
    {
        var plan = Plan();
        var started = new FakeProcess(ComponentRoles.Lm, 99) { Exited = true };
        var running = new[]
        {
            new RunningProcessInfo(50, "/games/EXVS2IB_V29/vsac29_Release_LM.exe", "vsac29_Release_LM"),
        };
        var hits = ProfileProcessMatcher.Find(ComponentRoles.Lm, plan, new[] { started }, running);
        Assert.Equal(new[] { 50 }, hits.Select(h => h.Pid));
        Assert.Equal("this profile's folder", hits[0].How);
    }

    [Fact]
    public void Status_is_stopped_when_nothing_matches()
    {
        var s = ProfileProcessMatcher.Status(ComponentRoles.Client, Plan(), Array.Empty<IStartedProcess>(), Array.Empty<RunningProcessInfo>());
        Assert.False(s.Running);
        Assert.Equal("Stopped", s.Label);
    }
}

internal sealed class FakeProcessControl : IProcessControl
{
    public List<RunningProcessInfo> Running { get; } = new();
    public List<int> Killed { get; } = new();
    public IReadOnlyList<RunningProcessInfo> List() => Running;
    public void KillTree(int pid)
    {
        Killed.Add(pid);
        Running.RemoveAll(r => r.Pid == pid);
    }
}
