namespace Exvs2Launcher.Core.Files;

/// <summary>Well-known file names inside an Infinite Boost game (cab) folder.</summary>
public sealed class GameFolder
{
    public const string ClientExeName = "vsac29_Release.exe";
    public const string LmExeName = "vsac29_Release_LM.exe";
    public const string ClientIniName = "config.ini";
    public const string LmIniName = "lmnfig.ini";
    public const string CardIniName = "card.ini";
    public const string SnowLauncherIniName = "SnowLauncher.ini";
    public const string BngrwDllName = "bngrw.dll";

    public GameFolder(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string ClientExe => Path.Combine(Root, ClientExeName);
    public string LmExe => Path.Combine(Root, LmExeName);
    public string ClientIni => Path.Combine(Root, ClientIniName);
    public string LmIni => Path.Combine(Root, LmIniName);
    public string CardIni => Path.Combine(Root, CardIniName);
    public string SnowLauncherIni => Path.Combine(Root, SnowLauncherIniName);
    public string BngrwDll => Path.Combine(Root, BngrwDllName);
    /// <summary>svd1-wrapped test-mode store. The launcher never creates this file from scratch.</summary>
    public string TestModePcbJson => Path.Combine(Root, "29", "testmode_pcb.json");
    /// <summary>The pack's stock card server. The launcher never starts it.</summary>
    public string StockServerExe => Path.Combine(Root, "server", "Server.exe");

    public bool LooksValid => File.Exists(ClientIni) || File.Exists(ClientExe);
}
