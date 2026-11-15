using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;
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
    private readonly IProcessStarter _starter;
    private readonly CabLauncher _launcher;
    private readonly string _logFile;
    private ProfileFile _file = new();
    private LauncherProfile? _current;
    private string? _folderWhenShown;
    private bool _loading;
    private bool _starting;
    private string? _lastLogBody;
    private CancellationTokenSource? _startCts;
    private CancellationTokenSource? _serverCheckCts;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _logFile = Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "exvs2-launcher.log");
        _starter = new SystemProcessStarter();
        var seq = new StartSequencer(_starter, _probe, _owners, new RealDelay());
        seq.Log += Log;
        seq.StageChanged += s => Dispatcher.Invoke(() => StageText.Text = s.ToString());
        _launcher = new CabLauncher(seq, _writer);
        _launcher.Log += Log;

        StoreText.Text = "Profiles: " + _store.FilePath;
        try { _file = _store.Load(); }
        catch (Exception ex) { Log("Could not read profiles (" + ex.Message + "). Starting with an empty list."); }
        if (_file.Profiles.Count > 0)
            RefreshProfileBox(_file.Profiles.FirstOrDefault(p => p.Name == _file.LastSelected) ?? _file.Profiles[0]);
        else
            ShowNoProfile();
        FillNics();
        Log("EXVS2 Launcher " + typeof(MainWindow).Assembly.GetName().Version + " ready. Card values are never shown or logged.");
        Loaded += MainWindow_Loaded;
        _statusTimer.Tick += (_, _) => RefreshComponentStatus();
        _statusTimer.Start();
        RefreshComponentStatus();
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
        _folderWhenShown = p.GameFolder;
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
        if (RejectBadBindAddresses()) return false;
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
            CurrentIpText.Text =
                $"In the files now: config.ini IpAddress={s.ClientIp ?? "-"} (PCB), lmnfig.ini IpAddress={s.LmIp ?? "-"} (LM; authoritative), " +
                $"config.ini Server={s.ClientServer ?? "-"}, lmnfig.ini Server={s.LmServer ?? "-"}, " +
                SocketHookSetting.Display(s.LmDisableSocketHook);
            CurrentIpText.Foreground = System.Windows.Media.Brushes.Gray;
            var nics = SafeNics();
            var fileReasons = new List<string>();
            var pcbWhy = BindAddress.RejectReason(s.ClientIp, "PCB", nics);
            var lmWhy = BindAddress.RejectReason(s.LmIp, "LM", nics);
            if (pcbWhy is not null) fileReasons.Add(pcbWhy);
            if (lmWhy is not null) fileReasons.Add(lmWhy);
            var expectedServer = _current is not null ? _current.ServerAddress : CardServerEndpoint.Default;
            fileReasons.AddRange(CardServerEndpoint.Inspect(s.ClientServer, s.LmServer, expectedServer));
            var hookWarn = SocketHookSetting.WarnIfDangerous(
                s.LmDisableSocketHook, s.ClientIp, s.LmIp, MultipleCabsOnThisPc(), s.SnowDisableSocketHook);
            if (hookWarn is not null) fileReasons.Add(hookWarn);
            if (fileReasons.Count > 0)
            {
                CurrentIpText.Text += "  " + string.Join(" ", fileReasons);
                CurrentIpText.Foreground = System.Windows.Media.Brushes.DarkRed;
            }
            else if (NetworkBinding.SameIp(s.ClientIp, s.LmIp) && s.ClientIp is not null)
            {
                CurrentIpText.Text += "  Note: PCB and LM share this address, which is fine for a single cab. Use a separate LM address if you run more than one cab or LM on this PC.";
            }
            ScheduleServerReachability(expectedServer, CurrentIpText.Text);
            if (_current is not null && s.Serial is not null) _current.Serial = s.Serial;
            if (_current is not null)
            {
                var dupes = SerialRegistry.Duplicates(_file, _current, s.Serial, _writer);
                if (dupes.Count > 0) SerialText.Text += "   ⚠ also used by " + string.Join(", ", dupes);
            }
        }
        catch (Exception ex) { SerialText.Text = "Could not read the game files: " + ex.Message; }
        RefreshServerVersion();
        RefreshComponentStatus();
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
        if (_current is not null) ReadForm();
        OpenWizard(firstRun: false);
    }

    private void Troubleshooting_Click(object sender, RoutedEventArgs e)
    {
        new TroubleshootingWindow { Owner = this }.ShowDialog();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_file.Profiles.Count == 0)
            OpenWizard(firstRun: true);
    }

    private void OpenWizard(bool firstRun)
    {
        var wiz = new WizardWindow(_file, _store, _writer, _ids, _probe, _owners, _starter,
            new WindowsEnvironmentProbe(), Log, this);
        bool accepted = wiz.ShowDialog() == true;
        var profile = accepted ? wiz.ResultProfile : null;
        if (profile is not null)
        {
            RefreshProfileBox(profile);
            if (wiz.ResultWrites is not null) Report("Setup wizard", wiz.ResultWrites);
            if (wiz.StartNow) Dispatcher.BeginInvoke(new Action(() => Start_Click(this, new RoutedEventArgs())));
        }
        else if (_file.Profiles.Count == 0)
        {
            ShowNoProfile();
            if (firstRun) Log("Setup wizard was cancelled. Click New to run it again.");
        }
    }

    private void ShowNoProfile()
    {
        _loading = true;
        _current = null;
        ProfileBox.ItemsSource = null;
        NameBox.Text = "";
        GameFolderBox.Text = "";
        ServerPathBox.Text = "";
        SerialText.Text = "Click New to run the setup wizard and create a cab profile.";
        CardText.Text = "";
        CurrentIpText.Text = "";
        _loading = false;
        RefreshComponentStatus();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (MessageBox.Show(this, $"Delete the profile \"{_current.Name}\"? The game folder is not touched.", "Delete profile",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _file.Profiles.Remove(_current);
        _current = null;
        if (_file.Profiles.Count == 0)
        {
            ShowNoProfile();
            try { _store.Save(_file); } catch (Exception ex) { Log("Could not save profiles: " + ex.Message); }
            return;
        }
        RefreshProfileBox(_file.Profiles[0]);
        SaveAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll()) return;
        RefreshProfileBox(_current!);
        OfferFixCardServerIni(requiredForStart: false);
        OfferFixSocketHook(requiredForStart: false);
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
        ApplySerialForChosenFolder();
        RefreshGameState();
    }

    private void GameFolderBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplySerialForChosenFolder();
        RefreshGameState();
    }

    /// <summary>
    /// Adopts a unique serial already in the folder, or writes a new one only when the folder has no serial
    /// or that serial belongs to another profile. Never replaces a unique existing folder serial.
    /// </summary>
    private void ApplySerialForChosenFolder()
    {
        if (!ReadForm() || _current is null) return;
        var folder = GameFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        if (string.Equals(folder, _folderWhenShown, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var decision = SerialAssignment.ForGameFolder(_file, _current, _ids, _writer, writeIfNeeded: true);
            Log(decision.Message);
            if (decision.Write is not null) Report("Serial " + decision.Serial, decision.Write);
            _folderWhenShown = folder;
            SaveAll(quiet: true);
        }
        catch (Exception ex) { Log("Could not assign a serial for this folder: " + ex.Message); }
    }

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
        RefreshServerVersion();
    }

    private void ServerPathBox_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) RefreshServerVersion(); }

    private void RefreshServerVersion()
    {
        var path = ServerPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ServerVersionText.Text = "";
            ServerVersionText.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }
        var info = CardServerInspector.Inspect(path);
        if (info.Summary is null && info.Warning is null)
        {
            ServerVersionText.Text = "";
            return;
        }
        ServerVersionText.Text = (info.Warning is not null ? "⚠ " + info.Warning : "Card server " + info.Summary)
                                 + (info.Source is not null && info.Warning is null ? " (from " + info.Source + ")" : "");
        ServerVersionText.Foreground = info.LooksOld
            ? System.Windows.Media.Brushes.DarkGoldenrod
            : System.Windows.Media.Brushes.Gray;
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
        var nics = LanAddressDetector.Enumerate();
        var best = LanAddressDetector.PickBest(nics);
        if (best is null) { Warn("No active IPv4 network adapter found."); return; }
        PcbIpBox.Text = best.Address;
        Log("Detected LAN IP for the PCB (client): " + best.Display + ". LM IP was not changed.");
        // If the LM box is empty, offer the existing lmnfig.ini address when it is local, otherwise leave it
        // for the user to pick. Never copy the detected PCB IP into the LM box.
        if (string.IsNullOrWhiteSpace(IpFrom(LmIpBox.Text)) && Directory.Exists(GameFolderBox.Text.Trim()))
        {
            try
            {
                var existing = _writer.Read(GameFolderBox.Text.Trim()).LmIp;
                if (BindAddress.RejectReason(existing, "LM", nics) is { } why)
                    Log("lmnfig.ini LM IP was not copied into the box. " + why);
                else if (existing is not null && NetworkBinding.FindLocal(existing, nics) is not null)
                {
                    LmIpBox.Text = existing;
                    Log("LM IP filled from lmnfig.ini: " + existing + ".");
                }
            }
            catch (Exception ex) { Log("Could not read lmnfig.ini: " + ex.Message); }
        }
    }

    private void WriteIps_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm() || !GameFilesWritable(out var folder)) return;
        if (RejectBadBindAddresses()) return;
        try
        {
            if (NetworkBinding.SameIp(_current.PcbIp, _current.LmIp) && _current.PcbIp is not null)
                Log(NetworkBinding.SameAddressNote);
            string? server = _current.ServerAddress;
            var state = _writer.Read(folder);
            var problems = CardServerEndpoint.Inspect(state.ClientServer, state.LmServer, server);
            if (problems.Count > 0 && !ConfirmFixCardServerIni(problems, server))
                server = null;
            var r = _writer.ApplyNetwork(folder, _current.PcbIp, _current.LmIp, server, _current.SyncSnowLauncherIni);
            Report("IPs written", r);
            SaveAll(quiet: true);
            OfferFixSocketHook(requiredForStart: false);
        }
        catch (Exception ex) { Warn("Could not write IPs: " + ex.Message); }
        RefreshGameState();
    }

    // ---------- start / stop ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll(quiet: true)) return;
        await RunStart(() => _launcher.StartAsync(_file, _current!, _startCts!.Token), $"--- Start all \"{_current!.Name}\" ---");
    }

    private async void StartCard_Click(object sender, RoutedEventArgs e) => await StartRole(ComponentRoles.CardServer);
    private async void StartLm_Click(object sender, RoutedEventArgs e) => await StartRole(ComponentRoles.Lm);
    private async void StartClient_Click(object sender, RoutedEventArgs e) => await StartRole(ComponentRoles.Client);

    private async Task StartRole(string role)
    {
        if (!SaveAll(quiet: true) || _current is null) return;
        if (role == ComponentRoles.Lm || role == ComponentRoles.Client)
        {
            if (!OfferFixCardServerIni(requiredForStart: true)) return;
            if (!OfferFixSocketHook(requiredForStart: true)) return;
        }
        await RunStart(() => _launcher.StartRoleAsync(_current, role, _startCts!.Token), $"--- Start {role} \"{_current.Name}\" ---");
    }

    private async Task RunStart(Func<Task<StartOutcome>> start, string header)
    {
        _starting = true;
        SetStartStopEnabled();
        CancelButton.IsEnabled = true;
        _startCts = new CancellationTokenSource();
        try
        {
            Log(header);
            var outcome = await start();
            if (!outcome.Succeeded && outcome.FinalStage != StartStage.Cancelled)
            {
                // Fail() already logged the message; don't write it a second time.
                SetStage("Failed: " + outcome.Message);
            }
            SaveAll(quiet: true);
            if (_current is not null) ShowProfile(_current);
        }
        catch (Exception ex) { Warn("Start failed: " + ex.Message); }
        finally
        {
            _starting = false;
            CancelButton.IsEnabled = false;
            RefreshComponentStatus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _startCts?.Cancel();

    private void StopCard_Click(object sender, RoutedEventArgs e) => StopRole(ComponentRoles.CardServer);
    private void StopLm_Click(object sender, RoutedEventArgs e) => StopRole(ComponentRoles.Lm);
    private void StopClient_Click(object sender, RoutedEventArgs e) => StopRole(ComponentRoles.Client);

    private void StopRole(string role)
    {
        if (_current is null) return;
        var plan = StatusPlan();
        if (plan is null) return;
        int n = _launcher.Sequencer.StopRole(role, plan);
        if (n == 0) Log($"{role} is not running for this profile.");
        RefreshComponentStatus();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (MessageBox.Show(this, "Close this profile's Live Monitor, game, AND card server?\n\nOther cab folders are left alone. Don't do this while someone is mid-match on this card server.",
                "Stop all", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var plan = StatusPlan();
        if (plan is null) return;
        int n = 0;
        foreach (var role in new[] { ComponentRoles.Client, ComponentRoles.Lm, ComponentRoles.CardServer })
            n += _launcher.Sequencer.StopRole(role, plan);
        Log(n == 0 ? "Nothing to stop for this profile." : $"Stopped {n} program(s) for this profile.");
        RefreshComponentStatus();
    }

    private StartPlan? StatusPlan()
    {
        if (_current is null) return null;
        var folder = GameFolderBox.Text.Trim();
        var server = ServerPathBox.Text.Trim();
        var basePlan = _current.ToStartPlan();
        return new StartPlan
        {
            GameFolder = string.IsNullOrWhiteSpace(folder) ? basePlan.GameFolder : folder,
            CardServerPath = string.IsNullOrWhiteSpace(server) ? basePlan.CardServerPath : server,
            CardServerArguments = basePlan.CardServerArguments,
            StartCardServer = true,
            StartLm = true,
            StartClient = true,
            Host = basePlan.Host,
            Port = basePlan.Port,
            PortTimeout = basePlan.PortTimeout,
            PollInterval = basePlan.PollInterval,
            LmToClientDelay = basePlan.LmToClientDelay,
        };
    }

    private void RefreshComponentStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshComponentStatus); return; }
        var plan = StatusPlan();
        ComponentStatus card = new(ComponentRoles.CardServer, false, null, null);
        ComponentStatus lm = new(ComponentRoles.Lm, false, null, null);
        ComponentStatus client = new(ComponentRoles.Client, false, null, null);
        if (plan is not null)
        {
            try
            {
                card = _launcher.Sequencer.RoleStatus(ComponentRoles.CardServer, plan);
                lm = _launcher.Sequencer.RoleStatus(ComponentRoles.Lm, plan);
                client = _launcher.Sequencer.RoleStatus(ComponentRoles.Client, plan);
            }
            catch (Exception ex) { Log("Could not refresh program status: " + ex.Message); }
        }
        ApplyStatus(CardStatusText, StartCardButton, StopCardButton, card);
        ApplyStatus(LmStatusText, StartLmButton, StopLmButton, lm);
        ApplyStatus(ClientStatusText, StartClientButton, StopClientButton, client);
        StartButton.IsEnabled = !_starting;
        StopAllButton.IsEnabled = !_starting && (card.Running || lm.Running || client.Running);
        CancelButton.IsEnabled = _starting;
    }

    private void ApplyStatus(TextBlock label, Button start, Button stop, ComponentStatus status)
    {
        label.Text = status.Label;
        label.FontWeight = status.Running ? FontWeights.SemiBold : FontWeights.Normal;
        start.IsEnabled = !_starting && !status.Running;
        stop.IsEnabled = !_starting && status.Running;
    }

    private void SetStartStopEnabled() => RefreshComponentStatus();

    private async void CheckPort_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm()) return;
        var plan = _current!.ToStartPlan();
        bool listening = await _probe.IsListeningAsync(plan.Host, plan.Port, CancellationToken.None);
        var owner = _owners.FindListener(plan.Port);
        var kind = PortOwnerClassifier.Classify(listening || owner is not null, owner, plan);
        var msg = PortOwnerClassifier.Describe(kind, plan.Port, owner);
        Log(msg);
        SetStage(kind is PortOwnerKind.None or PortOwnerKind.PatchedServer ? msg : "Check port: " + msg);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _statusTimer.Stop();
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

    private IReadOnlyList<NicAddress> SafeNics()
    {
        try { return LanAddressDetector.Enumerate(); }
        catch (Exception) { return Array.Empty<NicAddress>(); }
    }

    private bool MultipleCabsOnThisPc() =>
        _file.Profiles.Count(p => !string.IsNullOrWhiteSpace(p.GameFolder)) > 1;

    private bool OfferFixSocketHook(bool requiredForStart)
    {
        if (_current is null) return true;
        var folder = _current.GameFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return true;
        GameFolderState state;
        try { state = _writer.Read(folder); }
        catch { return true; }
        var warn = SocketHookSetting.WarnIfDangerous(
            state.LmDisableSocketHook, _current.PcbIp ?? state.ClientIp, _current.LmIp ?? state.LmIp,
            MultipleCabsOnThisPc(), state.SnowDisableSocketHook);
        if (warn is null) return true;
        Log(warn);
        var body = warn + "\n\n" + SocketHookSetting.OfferFix;
        if (MessageBox.Show(this, body, "Fix DisableSocketHook (error 21-32)",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            if (requiredForStart)
                Warn("Start cancelled. With DisableSocketHook=True, the LM will grab every address and other LMs fail with 21-32.");
            return false;
        }
        if (!GameFilesWritable(out folder)) return false;
        var r = _writer.ApplyLmSocketHookEnabled(folder, _current.SyncSnowLauncherIni);
        Report("DisableSocketHook written", r);
        RefreshGameState();
        return true;
    }

    private bool OfferFixCardServerIni(bool requiredForStart)
    {
        if (_current is null) return true;
        var folder = _current.GameFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return true;
        GameFolderState state;
        try { state = _writer.Read(folder); }
        catch { return true; }
        var expected = _current.ServerAddress;
        var problems = CardServerEndpoint.Inspect(state.ClientServer, state.LmServer, expected);
        if (problems.Count == 0) return true;
        foreach (var problem in problems) Log(problem);
        if (!ConfirmFixCardServerIni(problems, expected))
        {
            if (requiredForStart)
                Warn("Start cancelled. The LM needs Server= " + expected + " in lmnfig.ini or it fails with error 21-32.");
            return false;
        }
        if (!GameFilesWritable(out folder)) return false;
        var r = _writer.ApplyNetwork(folder, pcbIp: null, lmIp: null, expected, _current.SyncSnowLauncherIni);
        Report("Server= written", r);
        RefreshGameState();
        return true;
    }

    private bool ConfirmFixCardServerIni(IReadOnlyList<string> problems, string expected)
    {
        var body = string.Join("\n\n", problems) + "\n\n" + CardServerEndpoint.OfferFix(expected);
        return MessageBox.Show(this, body, "Fix Server= (error 21-32)",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ScheduleServerReachability(string expected, string snapshot)
    {
        _serverCheckCts?.Cancel();
        _serverCheckCts = new CancellationTokenSource();
        var ct = _serverCheckCts.Token;
        _ = CheckServerReachabilityAsync(expected, snapshot, ct);
    }

    private async Task CheckServerReachabilityAsync(string expected, string snapshot, CancellationToken ct)
    {
        if (!CardServerEndpoint.TryParse(expected, out var host, out var port)) return;
        bool up;
        try { up = await _probe.IsListeningAsync(host, port, ct); }
        catch { return; }
        if (ct.IsCancellationRequested || up) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            if (CurrentIpText.Text != snapshot && !CurrentIpText.Text.StartsWith(snapshot, StringComparison.Ordinal))
                return;
            if (CurrentIpText.Text.Contains("is not answering", StringComparison.Ordinal)) return;
            CurrentIpText.Text += "  " + CardServerEndpoint.Unreachable(expected);
            CurrentIpText.Foreground = System.Windows.Media.Brushes.DarkRed;
        });
    }

    private bool RejectBadBindAddresses()
    {
        var nics = SafeNics();
        var reasons = new[]
        {
            BindAddress.RejectReason(_current?.PcbIp, "PCB", nics),
            BindAddress.RejectReason(_current?.LmIp, "LM", nics),
        }.Where(r => r is not null).ToList();
        if (reasons.Count == 0) return false;
        Warn(string.Join(Environment.NewLine + Environment.NewLine, reasons!));
        return true;
    }

    private void Warn(string message)
    {
        Log(message);
        MessageBox.Show(this, message, "EXVS2 Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetStage(string text)
    {
        StageText.Text = text;
        StageText.ToolTip = text;
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        if (string.Equals(message, _lastLogBody, StringComparison.Ordinal))
            return;
        _lastLogBody = message;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        try { File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); } catch { }
    }
}
