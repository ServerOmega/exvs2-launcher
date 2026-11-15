using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Net;
using Exvs2Launcher.Core.Profiles;
using Exvs2Launcher.Core.Wizard;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

public partial class WizardWindow : Window
{
    public const int StepCount = 8;

    private readonly ProfileFile _file;
    private readonly ProfileStore _store;
    private readonly GameConfigWriter _writer;
    private readonly IdentityGenerator _ids;
    private readonly IPortProbe _probe;
    private readonly IPortOwnerLookup _owners;
    private readonly IProcessStarter _starter;
    private readonly IEnvironmentProbe _env;
    private readonly Action<string>? _log;
    private readonly WizardState _state = new();
    private IReadOnlyList<NicAddress> _nics = Array.Empty<NicAddress>();
    private int _step;
    private bool _busy;

    public LauncherProfile? ResultProfile { get; private set; }
    public bool StartNow { get; private set; }
    public WriteReport? ResultWrites { get; private set; }

    public WizardWindow(
        ProfileFile file,
        ProfileStore store,
        GameConfigWriter writer,
        IdentityGenerator ids,
        IPortProbe probe,
        IPortOwnerLookup owners,
        IProcessStarter starter,
        IEnvironmentProbe? env = null,
        Action<string>? log = null,
        Window? owner = null)
    {
        InitializeComponent();
        _file = file;
        _store = store;
        _writer = writer;
        _ids = ids;
        _probe = probe;
        _owners = owners;
        _starter = starter;
        _env = env ?? new WindowsEnvironmentProbe();
        _log = log;
        if (owner is not null) Owner = owner;
        Loaded += (_, _) => ShowStep(0);
    }

    private void ShowStep(int step)
    {
        CollectFromUi();
        _step = Math.Clamp(step, 0, StepCount - 1);
        PageFolder.Visibility = Vis(0);
        PageCard.Visibility = Vis(1);
        PageSerial.Visibility = Vis(2);
        PageNetwork.Visibility = Vis(3);
        PageServer.Visibility = Vis(4);
        PageBattleMode.Visibility = Vis(5);
        PageChecks.Visibility = Vis(6);
        PageSummary.Visibility = Vis(7);

        string[] titles =
        {
            "Game folder", "Card", "Serial", "Network", "Card server", "Online battle mode", "Checks", "Summary",
        };
        StepTitle.Text = titles[_step];
        StepSubtitle.Text = "Step " + (_step + 1) + " of " + StepCount;

        BackButton.IsEnabled = _step > 0;
        bool last = _step == StepCount - 1;
        NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        SaveStartButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;

        switch (_step)
        {
            case 0: RefreshFolderPage(); break;
            case 1: RefreshCardPage(); break;
            case 2: RefreshSerialPage(); break;
            case 3: RefreshNetworkPage(); break;
            case 4: RefreshServerPage(); break;
            case 5: RefreshBattleModePage(); break;
            case 6: break;
            case 7: RefreshSummary(); break;
        }
    }

    private Visibility Vis(int step) => _step == step ? Visibility.Visible : Visibility.Collapsed;

    private void CollectFromUi()
    {
        _state.ProfileName = NameBox.Text.Trim();
        _state.GameFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text.Trim();
        _state.GenerateNewCard = NewCardRadio.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(SerialBox.Text)) _state.Serial = SerialBox.Text.Trim();
        _state.OnlyCabOnThisPc = OnlyCabYes.IsChecked == true;
        _state.PcbIp = IpFrom(PcbIpBox.Text);
        _state.LmIp = IpFrom(LmIpBox.Text);
        _state.CardServerPath = string.IsNullOrWhiteSpace(ServerPathBox.Text) ? null : ServerPathBox.Text.Trim();
        _state.MatchUrl = string.IsNullOrWhiteSpace(MatchUrlBox.Text) ? null : MatchUrlBox.Text.Trim();
        var secret = TagSecretBox.Password;
        _state.TagSecret = string.IsNullOrWhiteSpace(secret) ? null : secret;
        if (BattleModeCheck is not null)
            _state.SetBattleModeOnline = BattleModeCheck.IsChecked == true;
    }

    private static string? IpFrom(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        var t = display.Trim();
        var space = t.IndexOf(' ');
        return space > 0 ? t[..space].Trim() : t;
    }

    // ----- folder -----

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the game (cab) folder that has vsac29_Release.exe and config.ini" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text is "New cab" || NameBox.Text.StartsWith("Cab ", StringComparison.Ordinal))
            NameBox.Text = Path.GetFileName(dlg.FolderName);
        RefreshFolderPage();
        AdoptFolderDefaults();
    }

    private void FolderBox_LostFocus(object sender, RoutedEventArgs e) => RefreshFolderPage();

    private void RefreshFolderPage()
    {
        _state.ProfileName = NameBox.Text.Trim();
        _state.GameFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text.Trim();
        var check = GameFolderValidator.Check(_state.GameFolder, _file, _state.ProfileName);
        if (check.Issues.Count == 0)
            FolderStatus.Text = "This looks like a valid game folder.";
        else
            FolderStatus.Text = string.Join(Environment.NewLine, check.Issues.Select(i => (i.Blocking ? "• " : "Note: ") + i.Message));
        StatusText.Text = check.CanContinue ? "" : (check.Issues.FirstOrDefault(i => i.Blocking)?.Message ?? "");
    }

    private void AdoptFolderDefaults()
    {
        if (string.IsNullOrWhiteSpace(_state.GameFolder) || !Directory.Exists(_state.GameFolder)) return;
        try
        {
            var s = _writer.Read(_state.GameFolder);
            if (s.CardValid)
            {
                _state.GenerateNewCard = false;
                KeepCardRadio.IsChecked = true;
            }
            else
            {
                _state.GenerateNewCard = true;
                NewCardRadio.IsChecked = true;
                _state.PendingCard ??= _ids.NewCard();
            }

            var draft = _state.ToProfile();
            var decision = SerialAssignment.ForGameFolder(_file, draft, _ids, _writer, writeIfNeeded: false);
            _state.Serial = decision.Serial;
            _state.SerialAction = decision.Action;
            _state.SerialMessage = decision.Message;
            SerialBox.Text = decision.Serial;

            try { _nics = LanAddressDetector.Enumerate(); } catch (Exception) { }
            if (!string.IsNullOrWhiteSpace(s.ClientIp) && string.IsNullOrWhiteSpace(PcbIpBox.Text)
                && BindAddress.RejectReason(s.ClientIp, "PCB", _nics) is null)
                PcbIpBox.Text = s.ClientIp;
            if (!string.IsNullOrWhiteSpace(s.LmIp) && string.IsNullOrWhiteSpace(LmIpBox.Text)
                && BindAddress.RejectReason(s.LmIp, "LM", _nics) is null)
                LmIpBox.Text = s.LmIp;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read the game folder: " + ex.Message;
        }
    }

    // ----- card -----

    private void CardChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _state.GenerateNewCard = NewCardRadio.IsChecked == true;
        RefreshCardPage();
    }

    private void GenerateCard_Click(object sender, RoutedEventArgs e)
    {
        NewCardRadio.IsChecked = true;
        _state.GenerateNewCard = true;
        _state.PendingCard = _ids.NewCard();
        RefreshCardPage();
    }

    private void RefreshCardPage()
    {
        GameFolderState? current = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_state.GameFolder) && Directory.Exists(_state.GameFolder))
                current = _writer.Read(_state.GameFolder);
        }
        catch (Exception) { }

        KeepCardRadio.IsEnabled = current?.CardValid == true;
        if (current?.CardValid != true)
        {
            NewCardRadio.IsChecked = true;
            _state.GenerateNewCard = true;
            _state.PendingCard ??= _ids.NewCard();
        }

        if (_state.GenerateNewCard)
        {
            _state.PendingCard ??= _ids.NewCard();
            CardStatus.Text = "A new card is ready and will be written on Save. " + _state.PendingCard +
                              ". The old card.ini (if any) is backed up first. Never share these values.";
        }
        else if (current?.CardValid == true)
        {
            CardStatus.Text = "Existing card.ini is valid and will be kept. Values stay hidden. Never share them.";
        }
        else
        {
            CardStatus.Text = "No valid card was found. Generate a new one.";
        }
    }

    // ----- serial -----

    private void RefreshSerialPage()
    {
        if (string.IsNullOrWhiteSpace(_state.Serial))
        {
            var draft = _state.ToProfile();
            var decision = string.IsNullOrWhiteSpace(_state.GameFolder)
                ? SerialAssignment.ForNewProfile(_file, draft, _ids, _writer)
                : SerialAssignment.ForGameFolder(_file, draft, _ids, _writer, writeIfNeeded: false);
            _state.Serial = decision.Serial;
            _state.SerialAction = decision.Action;
            _state.SerialMessage = decision.Message;
        }
        SerialBox.Text = _state.Serial ?? "";
        SerialStatus.Text = _state.SerialMessage ?? "A unique serial is assigned.";
    }

    private void RegenSerial_Click(object sender, RoutedEventArgs e)
    {
        var taken = _file.Profiles
            .Where(p => !string.Equals(p.Name, _state.ProfileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Serial ?? "")
            .Where(s => s.Length > 0)
            .ToList();
        if (!string.IsNullOrWhiteSpace(_state.Serial)) taken.Add(_state.Serial);
        var serial = _ids.NewSerial(taken);
        _state.Serial = serial;
        _state.SerialAction = SerialAction.GeneratedOnProfile;
        _state.SerialMessage = "Will write a new unique serial " + serial + " to config.ini on Save.";
        SerialBox.Text = serial;
        SerialStatus.Text = _state.SerialMessage;
    }

    // ----- network -----

    private void RefreshNetworkPage()
    {
        try { _nics = LanAddressDetector.Enumerate(); }
        catch (Exception ex)
        {
            NetworkStatus.Text = "Could not list network adapters: " + ex.Message;
            return;
        }

        NicListText.Text = _nics.Count == 0
            ? "No local IPv4 addresses found."
            : "This PC's IPv4 addresses:" + Environment.NewLine + string.Join(Environment.NewLine, _nics.Select(n => "  " + n.Display));

        var items = _nics.Select(n => n.Display).ToList();
        PcbIpBox.ItemsSource = items;
        var lmItems = items.ToList();
        LmIpBox.ItemsSource = lmItems;

        if (string.IsNullOrWhiteSpace(IpFrom(PcbIpBox.Text)))
        {
            var best = LanAddressDetector.PickBest(_nics);
            if (best is not null) PcbIpBox.Text = best.Address;
        }
        ApplyNetworkChoice();
    }

    private void NetworkChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyNetworkChoice();
    }

    private void NetworkChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyNetworkChoice();
    }

    private void ApplyNetworkChoice()
    {
        _state.OnlyCabOnThisPc = OnlyCabYes.IsChecked == true;
        _state.PcbIp = IpFrom(PcbIpBox.Text);
        var typedLm = IpFrom(LmIpBox.Text);

        var typedBad = BindAddress.RejectReason(typedLm, "LM", _nics);
        if (typedBad is not null)
        {
            NetworkStatus.Text = typedBad;
            CommandBox.Visibility = Visibility.Collapsed;
            StatusText.Text = typedBad;
            _state.LmIp = null;
            return;
        }

        var (lm, messages) = SecondaryAddress.ChooseLm(_state.OnlyCabOnThisPc, _state.PcbIp, typedLm, _nics);
        var advice = SecondaryAddress.Advise(_state.PcbIp, _nics);
        if (_state.OnlyCabOnThisPc && string.IsNullOrWhiteSpace(typedLm) && !string.IsNullOrWhiteSpace(lm))
            LmIpBox.Text = lm!;
        else if (!_state.OnlyCabOnThisPc && string.IsNullOrWhiteSpace(typedLm) && advice.HasSecondAddressOnAdapter && advice.SuggestedIp is not null)
            LmIpBox.Text = advice.SuggestedIp;

        _state.LmIp = IpFrom(LmIpBox.Text) ?? lm;
        NetworkStatus.Text = string.Join(Environment.NewLine, messages);
        if (!_state.OnlyCabOnThisPc && advice.Command is not null && !advice.HasSecondAddressOnAdapter)
        {
            CommandBox.Text = advice.Command;
            CommandBox.Visibility = Visibility.Visible;
        }
        else
            CommandBox.Visibility = Visibility.Collapsed;

        StatusText.Text = BindAddress.RejectReason(_state.LmIp, "LM", _nics)
            ?? BindAddress.RejectReason(_state.PcbIp, "PCB", _nics)
            ?? "";
    }

    // ----- card server -----

    private void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick the patched card server (Server-Infinite.exe or 3-Start-Server.bat)",
            Filter = "Patched card server|Server-Infinite.exe;3-Start-Server.bat;*.bat;*.cmd;*.exe|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            ServerPathBox.Text = dlg.FileName;
            RefreshServerPage();
        }
    }

    private void ServerPath_Changed(object sender, RoutedEventArgs e) => RefreshServerPage();

    private void RefreshServerPage()
    {
        _state.CardServerPath = string.IsNullOrWhiteSpace(ServerPathBox.Text) ? null : ServerPathBox.Text.Trim();
        var info = CardServerInspector.Inspect(_state.CardServerPath);
        var bits = new List<string>();
        if (info.VersionLabel is not null)
            bits.Add("Detected version: " + info.VersionLabel + (info.Source is null ? "" : " (" + info.Source + ")"));
        if (info.Warning is not null) bits.Add(info.Warning);
        if (info.Detail is not null) bits.Add(info.Detail);
        if (bits.Count == 0)
            bits.Add(string.IsNullOrWhiteSpace(_state.CardServerPath)
                ? "No card server selected yet. You can download a package from the releases page."
                : "Could not read a version from this path.");
        ServerVersionText.Text = string.Join(Environment.NewLine, bits);
        ServerVersionText.Foreground = info.LooksOld
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Black;

        var json = CardServerConfig.FindServerJson(_state.CardServerPath);
        if (json is not null)
        {
            var pub = CardServerConfig.ReadPublic(json);
            if (pub is not null)
            {
                if (string.IsNullOrWhiteSpace(MatchUrlBox.Text) && !string.IsNullOrWhiteSpace(pub.MatchUrl))
                    MatchUrlBox.Text = pub.MatchUrl;
                SecretStatus.Text = pub.HasTagSecret
                    ? "A tag secret is already set in server.json. Leave the box empty to keep it, or type a new one to replace it. The value is never shown."
                    : "No tag secret is set in server.json yet. Paste the host's secret here. It is stored only in that file and is never logged.";
            }
        }
        else if (!string.IsNullOrWhiteSpace(_state.CardServerPath))
            SecretStatus.Text = "Could not find Configurations/server.json next to that path. The matchmaker address and secret cannot be written until it is found.";
        else
            SecretStatus.Text = "";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Could not open the link: " + ex.Message; }
        e.Handled = true;
    }

    // ----- checks -----

    private async void RunChecks_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        CollectFromUi();
        _busy = true;
        try
        {
            ChecksBox.Text = "Running checks…" + Environment.NewLine;
            var lines = new List<string>();

            var folder = GameFolderValidator.Check(_state.GameFolder, _file, _state.ProfileName);
            lines.Add(folder.CanContinue ? "Game folder: OK — " + folder.Path : "Game folder: " + string.Join(" ", folder.Issues.Select(i => i.Message)));

            var lmWhy = BindAddress.RejectReason(_state.LmIp, "LM", _nics);
            var pcbWhy = BindAddress.RejectReason(_state.PcbIp, "PCB", _nics);
            if (pcbWhy is not null) lines.Add(pcbWhy);
            lines.Add(lmWhy is not null ? lmWhy : "LM IP: " + (_state.LmIp ?? "(not set)"));

            var fw = WizardChecks.DetectFirewall(_env, _state.GameFolder, _state.CardServerPath);
            lines.Add("Firewall:");
            lines.Add(WizardChecks.FirewallAdvice(fw));
            foreach (var ev in fw.Evidence) lines.Add("  " + ev);

            var port = await WizardChecks.CardServerPortAsync(_state, _probe, _owners, _starter);
            lines.Add(port.Name + ": " + (port.Ok ? "OK — " : "") + port.Message);

            if (!string.IsNullOrWhiteSpace(_state.MatchUrl))
            {
                var http = await WizardChecks.MatchmakerHttpAsync(_state.MatchUrl);
                lines.Add(http.Name + ": " + (http.Ok ? "OK — " : "") + http.Message);
            }
            else
                lines.Add("Matchmaker: skipped (no address entered).");

            ChecksBox.Text = string.Join(Environment.NewLine, lines);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            ChecksBox.Text += Environment.NewLine + "Checks failed: " + ex.Message;
        }
        finally { _busy = false; }
    }

    // ----- summary / save -----


    private void BattleMode_Changed(object sender, RoutedEventArgs e) => RefreshBattleModePage();

    private void RefreshBattleModePage()
    {
        if (BattleModeCheck is not null)
            _state.SetBattleModeOnline = BattleModeCheck.IsChecked == true;
        if (string.IsNullOrWhiteSpace(_state.GameFolder))
        {
            BattleModeStatus.Text = "Pick a game folder first.";
            return;
        }
        if (!(_state.SetBattleModeOnline))
        {
            BattleModeStatus.Text = "battleMode will be left as it is. The cab stays on the in-store Live Monitor path if battleMode is not 0.";
            return;
        }
        var inspect = TestModeBattleMode.Inspect(TestModeBattleMode.PathFor(_state.GameFolder));
        BattleModeStatus.Text = inspect.CanWrite ? inspect.Summary : (inspect.Error ?? inspect.Summary);
    }

    private void RefreshSummary()
    {
        CollectFromUi();
        var plan = WizardPlanner.Preview(_state, _file, _writer, _nics);
        var lines = new List<string>();
        if (plan.Writes.Count == 0)
            lines.Add("No game files need to change. The launcher profile will still be saved.");
        foreach (var w in plan.Writes)
        {
            lines.Add((w.Creating ? "Create " : "Update ") + w.Kind + ": " + w.Path);
            lines.Add("  " + w.Summary);
            if (w.BackupFirst && !w.Creating) lines.Add("  A .bak-<timestamp> backup will be made first.");
        }
        if (plan.Notes.Count > 0)
        {
            lines.Add("");
            lines.Add("Notes:");
            foreach (var n in plan.Notes) lines.Add("  " + n);
        }
        if (plan.Blocking.Count > 0)
        {
            lines.Add("");
            lines.Add("Cannot save until these are fixed:");
            foreach (var b in plan.Blocking) lines.Add("  " + b);
        }
        SummaryBox.Text = string.Join(Environment.NewLine, lines);
        StatusText.Text = plan.CanApply ? "" : string.Join(Environment.NewLine, plan.Blocking);
        SaveButton.IsEnabled = plan.CanApply;
        SaveStartButton.IsEnabled = plan.CanApply;
    }

    private bool CanLeaveCurrentStep(out string? reason)
    {
        CollectFromUi();
        reason = null;
        switch (_step)
        {
            case 0:
                var folder = GameFolderValidator.Check(_state.GameFolder, _file, _state.ProfileName);
                if (!folder.CanContinue)
                {
                    reason = folder.Issues.FirstOrDefault(i => i.Blocking)?.Message ?? "Pick a valid game folder.";
                    return false;
                }
                AdoptFolderDefaults();
                return true;
            case 1:
                if (_state.GenerateNewCard && _state.PendingCard is null)
                    _state.PendingCard = _ids.NewCard();
                if (!_state.GenerateNewCard)
                {
                    try
                    {
                        var s = _writer.Read(_state.GameFolder!);
                        if (!s.CardValid)
                        {
                            reason = "This folder has no valid card. Generate a new one, or pick Keep only after a valid card.ini is present.";
                            return false;
                        }
                    }
                    catch (Exception ex) { reason = ex.Message; return false; }
                }
                return true;
            case 2:
                if (string.IsNullOrWhiteSpace(_state.Serial) || !IdentityFormat.IsValidSerial(_state.Serial))
                {
                    reason = "A unique 4-character serial is required.";
                    return false;
                }
                return true;
            case 3:
                if (string.IsNullOrWhiteSpace(_state.PcbIp))
                {
                    reason = "Pick a PCB (cab) IP.";
                    return false;
                }
                var pcbWhy = BindAddress.RejectReason(_state.PcbIp, "PCB", _nics);
                if (pcbWhy is not null) { reason = pcbWhy; return false; }
                var lmWhy = BindAddress.RejectReason(_state.LmIp, "LM", _nics)
                            ?? BindAddress.RejectReason(IpFrom(LmIpBox.Text), "LM", _nics);
                if (lmWhy is not null)
                {
                    reason = lmWhy;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(_state.LmIp))
                {
                    reason = _state.OnlyCabOnThisPc
                        ? "Pick an LM IP (it may match the PCB IP for a single cab)."
                        : "Pick a second address for the LM, or add one with the PowerShell command shown.";
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveCurrentStep(out var reason))
        {
            StatusText.Text = reason ?? "";
            return;
        }
        StatusText.Text = "";
        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        ShowStep(_step - 1);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Finish(start: false);

    private void SaveStart_Click(object sender, RoutedEventArgs e) => Finish(start: true);

    private void Finish(bool start)
    {
        CollectFromUi();
        var plan = WizardPlanner.Preview(_state, _file, _writer, _nics);
        if (!plan.CanApply)
        {
            StatusText.Text = string.Join(Environment.NewLine, plan.Blocking);
            return;
        }
        try
        {
            var report = WizardPlanner.Apply(_state, _file, _writer, nics: _nics);
            _store.Save(_file);
            ResultProfile = _file.Profiles.FirstOrDefault(p => p.Name == _file.LastSelected) ?? _state.ToProfile();
            ResultWrites = report;
            StartNow = start;
            foreach (var n in report.Notes) _log?.Invoke(n);
            _log?.Invoke("Setup wizard saved profile \"" + ResultProfile.Name + "\".");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }
}
