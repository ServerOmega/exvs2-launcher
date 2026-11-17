using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Exvs2Launcher.Core.CardServer;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Profiles;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>Install / update / start / stop the card server of the current cab profile (thin UI over CardServerManager).</summary>
public partial class CardServerWindow : Window
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly ProfileFile _file;
    private readonly LauncherProfile? _profile;
    private readonly Action _save;
    private readonly Action<string> _mainLog;
    private readonly CardServerManager _manager;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private CardServerLayout? _layout;
    private UpdateCheck? _check;
    private CancellationTokenSource? _busy;

    public CardServerWindow(ProfileFile file, LauncherProfile? profile, Action save, Action<string> mainLog)
    {
        InitializeComponent();
        _file = file;
        _profile = profile;
        _save = save;
        _mainLog = mainLog;
        _manager = new CardServerManager(new GitHubReleaseSource(Http), new HttpFileDownloader(Http), new SystemCardServerHost(),
            new TcpPortProbe(), new PowerShellBuildRunner(), new WindowsPortOwnerLookup());
        IncludePreCheck.IsChecked = file.CardServerIncludePrereleases;
        ReleaseLinkText.Visibility = Visibility.Collapsed;
        var detected = CardServerLayout.Detect(profile?.CardServerPath);
        FolderBox.Text = detected?.Root ?? "";
        SetLayoutFromBox();
        _timer.Tick += async (_, _) => await RefreshStateAsync();
        _timer.Start();
        Loaded += async (_, _) => { await RefreshStateAsync(); await CheckAsync(); };
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("exvs2-launcher");
        return c;
    }

    private void SetLayoutFromBox()
    {
        var text = FolderBox.Text.Trim().Trim('"');
        if (text.Length == 0) { _layout = null; UpdateButtons(); return; }
        try { _layout = CardServerLayout.Detect(text) ?? CardServerLayout.ForInstallFolder(text); }
        catch (Exception) { _layout = null; }
        UpdateButtons();
    }

    private async Task RefreshStateAsync()
    {
        if (_layout is null) { StateText.Text = "Pick a folder: an existing card server, or an empty folder for a new install."; UpdateButtons(); return; }
        try
        {
            var st = await _manager.GetStateAsync(_layout);
            StateText.Text = st.Summary + "  ·  " + _layout.ServerDir;
            StartButton.IsEnabled = _busy is null && st.Installed && !st.Running;
            StopButton.IsEnabled = _busy is null && st.Running;
        }
        catch (Exception ex) { StateText.Text = "Could not read the state: " + ex.Message; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var idle = _busy is null;
        var installed = _layout?.IsInstalled == true;
        var choice = _check?.Latest;
        InstallButton.Content = installed ? "Update" + (choice is null ? "" : " to " + choice.Release.Tag) : "Install" + (choice is null ? "" : " " + choice.Release.Tag);
        InstallButton.IsEnabled = idle && _layout is not null && choice is not null;
        CancelButton.IsEnabled = !idle;
        ImportPicturesButton.IsEnabled = idle && _layout is not null && File.Exists(Path.Combine(_layout.Root, "Import-Images.bat"));
        StartAfterCheck.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        IdleCheck.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckAsync()
    {
        UpdateText.Text = "Checking GitHub releases...";
        _check = await _manager.CheckAsync(_layout, IncludePreCheck.IsChecked == true);
        var installed = _layout?.IsInstalled == true;
        UpdateText.Text = _check.Summary + (installed && _check.Latest is not null && !_check.UpdateAvailable
            ? ". You can still reinstall it with the button below." : "")
            + (_check.Latest?.Kind == PackageKind.BuildPackage ? " (built on this PC from the release's scripts)" : "");
        ReleaseLinkText.Visibility = string.IsNullOrEmpty(_check.Latest?.Release.HtmlUrl) ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    // ------------------------------------------------------------------ events

    private void FolderBox_LostFocus(object sender, RoutedEventArgs e) { SetLayoutFromBox(); _ = RefreshStateAsync(); }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the card server folder (existing, or an empty folder for a new install)" };
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        SetLayoutFromBox();
        _ = RefreshStateAsync();
        _ = CheckAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _layout?.Root;
        if (dir is null || !Directory.Exists(dir)) return;
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = _layout is null ? null : Path.Combine(_layout.ServerDir, "Logs");
        if (dir is null || !Directory.Exists(dir)) { Log("No Logs folder yet."); return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void ImportPictures_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null) return;
        var bat = Path.Combine(_layout.Root, "Import-Images.bat");
        if (!File.Exists(bat)) return;
        Process.Start(new ProcessStartInfo(bat) { WorkingDirectory = _layout.Root, UseShellExecute = true });
        Log("Opened Import-Images.bat (see Import-Pictures.txt in the folder).");
    }

    private void IncludePre_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _file.CardServerIncludePrereleases = IncludePreCheck.IsChecked == true;
        _save();
        _ = CheckAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        var url = _check?.Latest?.Release.HtmlUrl;
        if (!string.IsNullOrEmpty(url) && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null) return;
        await RunBusy(ct => _manager.StartAsync(_layout, Progress(), TimeSpan.FromSeconds(90), ct));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null) return;
        if (MessageBox.Show(this, "Stop the card server? Cabs that use it lose their connection (a match in progress drops).",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBusy(ct => _manager.StopAsync(_layout, Progress(), TimeSpan.FromSeconds(15), ct));
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || _check?.Latest is not { } choice) return;
        var installed = _layout.IsInstalled;
        if (installed && IdleCheck.IsChecked != true)
        {
            MessageBox.Show(this, "Tick \"All cabs that use this card server are idle\" first. The update stops the card server for a minute or two.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var msg = installed
            ? $"Update the card server in\n{_layout.ServerDir}\nfrom {_layout.InstalledVersion() ?? "an unknown version"} to {choice.Release.Label}?\n\n" +
              "1. Download and check SHA-256, build (PowerShell window) while the current server keeps running\n" +
              "2. Stop the card server\n3. Move the old folder to a backup and swap in the new build, keeping Configurations, the card database, pictures and data files\n" +
              "4. Start it and wait until it listens; if it doesn't, put the old version back and restart it"
            : $"Install {choice.Release.Label} into\n{_layout.ServerDir}?\n\nDownload, check SHA-256 and build on this PC (PowerShell window, about 5 minutes the first time).";
        if (MessageBox.Show(this, msg, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var opts = new InstallOptions
        {
            CabsIdleConfirmed = installed && IdleCheck.IsChecked == true,
            StartAfterFreshInstall = StartAfterCheck.IsChecked == true,
        };
        InstallOutcome? outcome = null;
        await RunBusy(async ct => outcome = await _manager.InstallAsync(_layout, choice, opts, Progress(), ct));
        if (outcome is null) return;
        Log(outcome.Message);
        _mainLog("Card server: " + outcome.Message);
        IdleCheck.IsChecked = false;
        if (outcome.Success && _profile is not null &&
            !string.Equals(Path.GetFullPath(_profile.CardServerPath ?? "."), _layout.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            var set = string.IsNullOrWhiteSpace(_profile.CardServerPath) ||
                      MessageBox.Show(this, $"Use this card server for the cab profile \"{_profile.Name}\"?\n{_layout.ExePath}",
                          Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (set)
            {
                _profile.CardServerPath = _layout.ExePath;
                _save();
                Log("The cab profile now uses " + _layout.ExePath);
            }
        }
        if (!outcome.Success)
            MessageBox.Show(this, outcome.Message, Title, MessageBoxButton.OK, outcome.RolledBack ? MessageBoxImage.Warning : MessageBoxImage.Error);
        await CheckAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _busy?.Cancel();

    private async Task RunBusy(Func<CancellationToken, Task> work)
    {
        if (_busy is not null) return;
        _busy = new CancellationTokenSource();
        UpdateButtons();
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        try { await Task.Run(() => work(_busy.Token)); }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log("Error: " + ex.Message); }
        finally
        {
            _busy.Dispose();
            _busy = null;
            await RefreshStateAsync();
        }
    }

    private IProgress<string> Progress() => new Progress<string>(Log);

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(message)); return; }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy is not null)
        {
            MessageBox.Show(this, "Wait until the current step finishes (or press Cancel first).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        _timer.Stop();
    }
}
