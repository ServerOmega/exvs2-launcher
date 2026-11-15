using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>Thin UI over Exvs2Launcher.Core. All file and process logic lives in the core library.</summary>
public partial class MainWindow : Window
{
    private readonly ProfileStore _store = ProfileStore.Default();
    private readonly GameConfigWriter _writer = new();
    private readonly IdentityGenerator _ids = new();
    private readonly WindowsPortOwnerLookup _owners = new();
    private readonly TcpPortProbe _probe = new();
    private readonly CabLauncher _launcher;
    private readonly string _logFile;
    private ProfileFile _file = new();
    private LauncherProfile? _current;
    private bool _loading;
    private CancellationTokenSource? _startCts;

    public MainWindow()
    {
        InitializeComponent();
        _logFile = Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "exvs2-launcher.log");
        var seq = new StartSequencer(new SystemProcessStarter(), _probe, _owners, new RealDelay());
        seq.Log += Log;
        seq.StageChanged += s => Dispatcher.Invoke(() => StageText.Text = s.ToString());
        _launcher = new CabLauncher(seq, _writer);
        _launcher.Log += Log;

        StoreText.Text = "Profiles: " + _store.FilePath;
        try { _file = _store.Load(); }
        catch (Exception ex) { Log("Could not read profiles (" + ex.Message + "). Starting with an empty list."); }
        if (_file.Profiles.Count == 0) _file.Profiles.Add(new LauncherProfile { Name = "Test cab" });
        RefreshProfileBox(_file.Profiles.FirstOrDefault(p => p.Name == _file.LastSelected) ?? _file.Profiles[0]);
        FillNics();
        Log("EXVS2 Launcher " + typeof(MainWindow).Assembly.GetName().Version + " ready. Card values are never shown or logged.");
    }

    // ---------- profile <-> form ----------

    private void RefreshProfileBox(LauncherProfile select)
    {
        _loading = true;
        ProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = _file.Profiles;
        ProfileBox.SelectedItem = select;
        _loading = false;
        ShowProfile(select);
    }

    private void ShowProfile(LauncherProfile p)
    {
        _loading = true;
        _current = p;
        NameBox.Text = p.Name;
        GameFolderBox.Text = p.GameFolder;
        ServerPathBox.Text = p.CardServerPath ?? "";
        ServerArgsBox.Text = p.CardServerArguments;
        StartServerCheck.IsChecked = p.StartCardServer;
        StartLmCheck.IsChecked = p.StartLm;
        StartClientCheck.IsChecked = p.StartClient;
        HostBox.Text = p.CardServerHost;
        PortBox.Text = p.CardServerPort.ToString();
        TimeoutBox.Text = p.PortTimeoutSeconds.ToString();
        DelayBox.Text = p.LmToClientDelaySeconds.ToString();
        PrefixBox.Text = p.AccessCodePrefix;
        SyncSnowCheck.IsChecked = p.SyncSnowLauncherIni;
        AutoIpCheck.IsChecked = p.AutoDetectIp;
        PcbIpBox.Text = p.PcbIp ?? "";
        LmIpBox.Text = p.LmIp ?? "";
        _loading = false;
        RefreshGameState();
    }

    private bool ReadForm()
    {
        if (_current is null) return false;
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) { Warn("Port must be 1-65535."); return false; }
        if (!int.TryParse(TimeoutBox.Text, out int timeout) || timeout < 5) { Warn("Wait time must be at least 5 seconds."); return false; }
        if (!int.TryParse(DelayBox.Text, out int delay) || delay < 0) { Warn("LM → client delay must be 0 or more."); return false; }
        var prefix = PrefixBox.Text.Trim();
        if (prefix.Length > 18 || prefix.Any(c => c is < '0' or > '9')) { Warn("Access code prefix must be digits only (0-18 of them)."); return false; }

        _current.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Cab" : NameBox.Text.Trim();
        _current.GameFolder = GameFolderBox.Text.Trim();
        _current.CardServerPath = string.IsNullOrWhiteSpace(ServerPathBox.Text) ? null : ServerPathBox.Text.Trim();
        _current.CardServerArguments = ServerArgsBox.Text.Trim();
        _current.StartCardServer = StartServerCheck.IsChecked == true;
        _current.StartLm = StartLmCheck.IsChecked == true;
        _current.StartClient = StartClientCheck.IsChecked == true;
        _current.CardServerHost = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
        _current.CardServerPort = port;
        _current.PortTimeoutSeconds = timeout;
        _current.LmToClientDelaySeconds = delay;
        _current.AccessCodePrefix = prefix;
        _current.SyncSnowLauncherIni = SyncSnowCheck.IsChecked == true;
        _current.AutoDetectIp = AutoIpCheck.IsChecked == true;
        _current.PcbIp = NullIfBlank(IpFrom(PcbIpBox.Text));
        _current.LmIp = NullIfBlank(IpFrom(LmIpBox.Text));
        return true;
    }

    private bool SaveAll(bool quiet = false)
    {
        if (!ReadForm()) return false;
        var dupFolder = _file.Profiles.Where(p => !ReferenceEquals(p, _current) && !string.IsNullOrWhiteSpace(p.GameFolder))
            .FirstOrDefault(p => ReferenceEquals(ProfileStore.FindByFolder(new ProfileFile { Profiles = { p } }, _current!.GameFolder), p));
        if (dupFolder is not null) { Warn($"Profile \"{dupFolder.Name}\" already uses this game folder. Use one profile per folder."); return false; }
        _file.LastSelected = _current?.Name;
        try
        {
            _store.Save(_file);
            if (!quiet) Log("Saved profiles to " + _store.FilePath);
            return true;
        }
        catch (Exception ex) { Warn("Could not save profiles: " + ex.Message); return false; }
    }

    private void RefreshGameState()
    {
        var folder = GameFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            SerialText.Text = "(pick a game folder)";
            CardText.Text = "";
            CurrentIpText.Text = "";
            return;
        }
        try
        {
            var s = _writer.Read(folder);
            SerialText.Text = (s.Serial ?? "(no serial in config.ini)") +
                (s.SnowLauncherSerial is not null && s.SnowLauncherSerial != s.Serial ? $"   (SnowLauncher.ini has {s.SnowLauncherSerial})" : "");
            CardText.Text = !s.CardPresent ? "No card in card.ini" : s.CardValid ? "Card present (values hidden)" : "Card present but not in the expected format";
            CurrentIpText.Text = $"In the files now: config.ini IpAddress={s.ClientIp ?? "-"}, lmnfig.ini IpAddress={s.LmIp ?? "-"}, Server={s.ClientServer ?? "-"}";
            if (_current is not null && s.Serial is not null) _current.Serial = s.Serial;
            if (_current is not null)
            {
                var dupes = SerialRegistry.Duplicates(_file, _current, s.Serial, _writer);
                if (dupes.Count > 0) SerialText.Text += "   ⚠ also used by " + string.Join(", ", dupes);
            }
        }
        catch (Exception ex) { SerialText.Text = "Could not read the game files: " + ex.Message; }
    }

    // ---------- profile buttons ----------

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not LauncherProfile p || ReferenceEquals(p, _current)) return;
        ReadForm();
        ShowProfile(p);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        ReadForm();
        var p = new LauncherProfile { Name = "Cab " + (_file.Profiles.Count + 1) };
        _file.Profiles.Add(p);
        RefreshProfileBox(p);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (MessageBox.Show(this, $"Delete the profile \"{_current.Name}\"? The game folder is not touched.", "Delete profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _file.Profiles.Remove(_current);
        if (_file.Profiles.Count == 0) _file.Profiles.Add(new LauncherProfile { Name = "Test cab" });
        _current = null;
        RefreshProfileBox(_file.Profiles[0]);
        SaveAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAll()) RefreshProfileBox(_current!);
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the game (cab) folder that has vsac29_Release.exe and config.ini" };
        if (Directory.Exists(GameFolderBox.Text)) dlg.InitialDirectory = GameFolderBox.Text;
        if (dlg.ShowDialog(this) != true) return;
        var g = new GameFolder(dlg.FolderName);
        if (!g.LooksValid) Warn("That folder has no config.ini or vsac29_Release.exe. Check that it is the game folder.");
        GameFolderBox.Text = g.Root;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text.StartsWith("Cab ") || NameBox.Text == "New cab")
            NameBox.Text = Path.GetFileName(g.Root);
        RefreshGameState();
    }

    private void GameFolderBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) RefreshGameState(); }

    private void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick the patched card server (Server-Infinite.exe or 3-Start-Server.bat)",
            Filter = "Card server (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFileName(dlg.FileName), "Server.exe", StringComparison.OrdinalIgnoreCase))
        {
            Warn("That is the pack's stock card server (Server.exe). Pick Server-Infinite.exe or 3-Start-Server.bat from exvs2-matchmaking.");
            return;
        }
        ServerPathBox.Text = dlg.FileName;
    }

    // ---------- identity ----------

    private bool GameFilesWritable(out string folder)
    {
        folder = GameFolderBox.Text.Trim();
        if (!File.Exists(Path.Combine(folder, GameFolder.ClientIniName)))
        {
            Warn("Pick a game folder that has config.ini first.");
            return false;
        }
        var running = ProcessGuard.RunningConflicts();
        if (running.Count > 0)
        {
            Warn("Close these first, then try again: " + string.Join(", ", running) +
                 "\n\nSnowLauncher keeps its settings in memory and would put the old values back.");
            return false;
        }
        return true;
    }

    private void NewSerial_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm() || !GameFilesWritable(out var folder)) return;
        var taken = SerialRegistry.TakenSerials(_file, _current, _writer).ToList();
        var old = _writer.Read(folder).Serial;
        if (old is not null) taken.Add(old);
        var serial = _ids.NewSerial(taken);
        if (MessageBox.Show(this, $"Change this cab's serial from {old ?? "(none)"} to {serial}?\n\nA backup of each file is made first.",
                "New serial", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var r = _writer.ApplySerial(folder, serial, _current!.SyncSnowLauncherIni);
            _current.Serial = serial;
            Report("New serial " + serial, r);
            SaveAll(quiet: true);
        }
        catch (Exception ex) { Warn("Could not write the serial: " + ex.Message); }
        RefreshGameState();
    }

    private void NewCard_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm() || !GameFilesWritable(out var folder)) return;
        if (MessageBox.Show(this,
                "Make a new card for this cab?\n\nThe card server will see a brand-new player on the next card insert. " +
                "The old card.ini is backed up first (and SnowLauncher.ini, if that option is on), so you can go back.",
                "New card", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var card = _ids.NewCard(_current!.AccessCodePrefix);
            var r = _writer.ApplyCard(folder, card, _current.SyncSnowLauncherIni);
            Report("New card written (values hidden)", r);
            SaveAll(quiet: true);
        }
        catch (Exception ex) { Warn("Could not write the card: " + ex.Message); }
        RefreshGameState();
    }

    // ---------- network ----------

    private void FillNics()
    {
        try
        {
            var nics = LanAddressDetector.Enumerate();
            var items = nics.Select(n => n.Display).ToList();
            PcbIpBox.ItemsSource = items;
            LmIpBox.ItemsSource = items;
        }
        catch (Exception ex) { Log("Could not list network adapters: " + ex.Message); }
    }

    private void DetectIp_Click(object sender, RoutedEventArgs e)
    {
        FillNics();
        var best = LanAddressDetector.PickBest(LanAddressDetector.Enumerate());
        if (best is null) { Warn("No active IPv4 network adapter found."); return; }
        PcbIpBox.Text = best.Address;
        LmIpBox.Text = best.Address;
        Log("Detected LAN IP " + best.Display);
    }

    private void WriteIps_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm() || !GameFilesWritable(out var folder)) return;
        try
        {
            var r = _writer.ApplyNetwork(folder, _current!.PcbIp, _current.LmIp, _current.ServerAddress, _current.SyncSnowLauncherIni);
            Report("IPs written", r);
            SaveAll(quiet: true);
        }
        catch (Exception ex) { Warn("Could not write IPs: " + ex.Message); }
        RefreshGameState();
    }

    // ---------- start / stop ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll(quiet: true)) return;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _startCts = new CancellationTokenSource();
        try
        {
            Log($"--- Start \"{_current!.Name}\" ---");
            var outcome = await _launcher.StartAsync(_file, _current, _startCts.Token);
            if (!outcome.Succeeded && outcome.FinalStage != StartStage.Cancelled)
            {
                Log(outcome.Message);
                StageText.Text = "Failed";
            }
            SaveAll(quiet: true);
            ShowProfile(_current);
        }
        catch (Exception ex) { Warn("Start failed: " + ex.Message); }
        finally
        {
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _startCts?.Cancel();

    private void StopGame_Click(object sender, RoutedEventArgs e)
    {
        int n = _launcher.Sequencer.Stop(includeCardServer: false);
        Log(n == 0 ? "Nothing to stop (only programs this launcher started can be stopped)." : $"Stopped {n} program(s).");
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Close the LM, client AND the card server this launcher started?\n\nDon't do this while someone is mid-match on this card server.",
                "Stop all", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int n = _launcher.Sequencer.Stop(includeCardServer: true);
        Log(n == 0 ? "Nothing to stop (only programs this launcher started can be stopped)." : $"Stopped {n} program(s).");
    }

    private async void CheckPort_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm()) return;
        var plan = _current!.ToStartPlan();
        bool listening = await _probe.IsListeningAsync(plan.Host, plan.Port, CancellationToken.None);
        var owner = _owners.FindListener(plan.Port);
        var kind = PortOwnerClassifier.Classify(listening || owner is not null, owner, plan);
        Log(kind switch
        {
            PortOwnerKind.None => $"Port {plan.Port} is free. Start will launch the card server first.",
            PortOwnerKind.PatchedServer => $"Port {plan.Port}: patched card server {owner!.Describe()}. Start will reuse it.",
            PortOwnerKind.StockServer => $"Port {plan.Port}: STOCK card server {owner!.Describe()}. Close it (and SnowLauncher) before Start.",
            PortOwnerKind.Other => $"Port {plan.Port}: held by {owner!.Describe()}, not the patched card server.",
            _ => $"Port {plan.Port} is in use, but the owning program could not be identified.",
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { ReadForm(); _file.LastSelected = _current?.Name; _store.Save(_file); } catch { }
    }

    // ---------- helpers ----------

    private void Report(string title, WriteReport r)
    {
        Log(title + ".");
        foreach (var w in r.Written) Log("  updated " + w);
        foreach (var b in r.Backups) Log("  backup  " + b);
        foreach (var n in r.Notes) Log("  " + n);
        if (r.Written.Count == 0) Log("  nothing changed");
    }

    private static string IpFrom(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private void Warn(string message)
    {
        Log(message);
        MessageBox.Show(this, message, "EXVS2 Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        try { File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); } catch { }
    }
}
