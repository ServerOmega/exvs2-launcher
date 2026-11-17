using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Exvs2Launcher.Core.CardServer;
using Exvs2Launcher.Core.Notifications;
using Exvs2Launcher.Core.Presence;
using Exvs2Launcher.Core.Update;

namespace Exvs2Launcher.App;

/// <summary>v0.6.0 Settings tab: self-update, Discord Rich Presence and notifications.</summary>
public partial class MainWindow
{
    private static readonly HttpClient UpdateHttp = CreateUpdateClient();
    private UpdateCheckResult? _lastUpdate;
    private bool _updating;
    private Notifier? _notifier;
    private DiscordIpcClient? _discord;
    private string? _discordFor;
    private PlayState? _lastPresenceState;
    private int _lastPresenceCabs = -1;
    private DateTimeOffset? _presenceSince;
    private DateTime _nextDiscordTry = DateTime.MinValue;

    private static HttpClient CreateUpdateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("exvs2-launcher");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static string InstalledVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    private void InitNotifier() => _notifier = new Notifier();

    private void ShowNotice(Notice n)
    {
        Log("Notification: " + n.Title + " — " + n.Text);
        _notifier?.Show(n, _file.Settings);
    }

    private void LoadSettingsForm()
    {
        var s = _file.Settings;
        UpdateVersionText.Text = "Installed: " + InstalledVersion;
        UpdateOnStartupCheck.IsChecked = s.CheckForUpdatesOnStartup;
        UpdatePreCheck.IsChecked = s.UpdateIncludePrereleases;
        PresenceCheck.IsChecked = s.RichPresenceEnabled;
        PresenceIdBox.Text = s.RichPresenceClientId;
        PresenceButtonCheck.IsChecked = s.RichPresenceShowInviteButton;
        NotifySoundCheck.IsChecked = s.NotifySound;
        NotifyToastCheck.IsChecked = s.NotifyToast;
        NotifyQueueCheck.IsChecked = s.NotifyOnQueueActivity;
        NotifyMatchCheck.IsChecked = s.NotifyOnMatch;
        var exe = Environment.ProcessPath;
        RollbackButton.IsEnabled = exe is not null && LauncherUpdate.CanRollBack(exe);
        InstallUpdateButton.IsEnabled = false;
        SkipUpdateButton.IsEnabled = false;
        if (exe is not null) { try { LauncherUpdate.CleanUp(exe); } catch { } }
    }

    private void SettingsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _file.Settings;
        s.CheckForUpdatesOnStartup = UpdateOnStartupCheck.IsChecked == true;
        s.UpdateIncludePrereleases = UpdatePreCheck.IsChecked == true;
        s.NotifySound = NotifySoundCheck.IsChecked == true;
        s.NotifyToast = NotifyToastCheck.IsChecked == true;
        s.NotifyOnQueueActivity = NotifyQueueCheck.IsChecked == true;
        s.NotifyOnMatch = NotifyMatchCheck.IsChecked == true;
        SaveSettings();
    }

    private void SaveSettings()
    {
        _file.Settings.Normalize();
        try { _store.Save(_file); } catch (Exception ex) { Log("Could not save settings: " + ex.Message); }
    }

    // ---------- Self-update ----------

    private async Task StartupUpdateCheckAsync()
    {
        if (!_file.Settings.CheckForUpdatesOnStartup) return;
        await Task.Delay(TimeSpan.FromSeconds(3));
        var r = await CheckForUpdateAsync(quiet: true);
        if (r is { UpdateAvailable: true } && r.Latest!.Version != _file.Settings.SkippedUpdateTag)
        {
            UpdateNoticeButton.Content = "Update " + r.Latest.Version + " available";
            UpdateNoticeButton.Visibility = Visibility.Visible;
            Log(r.Summary + " Open Settings to install it (nothing is installed automatically).");
        }
    }

    private async Task<UpdateCheckResult?> CheckForUpdateAsync(bool quiet)
    {
        try
        {
            var json = await UpdateHttp.GetStringAsync(LauncherUpdate.ApiUrl);
            var releases = ReleaseFeed.Parse(json);
            var r = LauncherUpdate.Evaluate(InstalledVersion, releases, _file.Settings.UpdateIncludePrereleases);
            _lastUpdate = r;
            UpdateStatusText.Text = r.Summary;
            InstallUpdateButton.IsEnabled = r.UpdateAvailable && !_updating;
            SkipUpdateButton.IsEnabled = r.UpdateAvailable;
            if (!quiet) Log(r.Summary);
            return r;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update check failed: " + ex.Message;
            if (!quiet) Log(UpdateStatusText.Text);
            return null;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking GitHub releases...";
        await CheckForUpdateAsync(quiet: false);
    }

    private void UpdateNotice_Click(object sender, RoutedEventArgs e)
    {
        foreach (var raw in MainTabs.Items)
            if (raw is System.Windows.Controls.TabItem { Header: "Settings" } item) { MainTabs.SelectedItem = item; return; }
    }

    private void SkipUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdate?.Latest is null) return;
        _file.Settings.SkippedUpdateTag = _lastUpdate.Latest.Version;
        SaveSettings();
        UpdateNoticeButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Skipped " + _lastUpdate.Latest.Version + ". You will be told about newer releases.";
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var choice = _lastUpdate?.Latest;
        var exe = Environment.ProcessPath;
        if (choice is null || exe is null || _updating) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Warn("Self-update only works for the single-file launcher exe.");
            return;
        }
        if (MessageBox.Show(this,
                $"Download {choice.Exe.Name} ({choice.Release.Label}), check its SHA-256, and replace this launcher?\n\n" +
                "The current exe is kept as " + Path.GetFileName(exe) + LauncherUpdate.PreviousSuffix + " so you can roll back.\n" +
                "Games and servers that are running are not touched. The launcher restarts afterwards.",
                "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _updating = true;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            var staging = Path.Combine(_dataDir, "update");
            var progress = new Progress<long>(b => UpdateStatusText.Text = $"Downloading... {b / 1024 / 1024} MB");
            var staged = await LauncherUpdate.DownloadAndVerifyAsync(choice, new HttpFileDownloader(UpdateHttp), staging, progress, CancellationToken.None);
            UpdateStatusText.Text = "SHA-256 verified. Installing...";
            LauncherUpdate.Install(exe, staged);
            Log("Installed " + choice.Release.Label + ". Restarting the launcher.");
            SaveAll(quiet: true);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Update not installed: " + ex.Message;
            Warn(UpdateStatusText.Text);
            InstallUpdateButton.IsEnabled = true;
        }
        finally { _updating = false; }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !LauncherUpdate.CanRollBack(exe)) { Warn("There is no previous launcher exe to go back to."); return; }
        if (MessageBox.Show(this, "Go back to the previous launcher exe and restart?", "Roll back update",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            LauncherUpdate.RollBack(exe);
            SaveAll(quiet: true);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Warn("Could not roll back: " + ex.Message); }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenPath(LauncherUpdate.ReleasesPage);

    // ---------- Discord Rich Presence ----------

    private void PresenceSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _file.Settings;
        s.RichPresenceEnabled = PresenceCheck.IsChecked == true;
        s.RichPresenceClientId = PresenceIdBox.Text.Trim();
        s.RichPresenceShowInviteButton = PresenceButtonCheck.IsChecked == true;
        SaveSettings();
        _lastPresenceState = null;
        _nextDiscordTry = DateTime.MinValue;
        if (!s.RichPresenceEnabled) _ = ClearPresenceAsync();
        PresenceStatusText.Text = !s.RichPresenceEnabled ? "Rich Presence is off."
            : DiscordIpcClient.IsValidClientId(s.RichPresenceClientId) ? "Rich Presence is on. It updates within a few seconds while Discord is open."
            : "Paste your Discord application's Application ID (a long number). See 'How to set up'.";
    }

    private async void PresenceTest_Click(object sender, RoutedEventArgs e)
    {
        var id = PresenceIdBox.Text.Trim();
        if (!DiscordIpcClient.IsValidClientId(id)) { PresenceStatusText.Text = "That Application ID does not look right (15–22 digits)."; return; }
        PresenceStatusText.Text = "Connecting to Discord...";
        await using var test = new DiscordIpcClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!await test.ConnectAsync(id, cts.Token)) { PresenceStatusText.Text = "Could not connect: " + (test.LastError ?? "is the Discord desktop app running?"); return; }
            var ok = await test.SetActivityAsync(PresenceTracker.Activity(PlayState.Menus, 1, DateTimeOffset.Now, PresenceButtonCheck.IsChecked == true),
                Environment.ProcessId, cts.Token);
            PresenceStatusText.Text = ok ? "Connected. Your Discord status shows the test for a few seconds." : "Discord refused the activity: " + test.LastError;
            if (ok) await Task.Delay(4000);
        }
        catch (Exception ex) { PresenceStatusText.Text = "Test failed: " + ex.Message; }
        _lastPresenceState = null;
    }

    private void PresenceHelp_Click(object sender, RoutedEventArgs e) =>
        OpenPath("https://github.com/ServerOmega/exvs2-launcher/blob/mvp/docs/RICH-PRESENCE.md");

    private async Task UpdatePresenceAsync(int clientsRunning)
    {
        var s = _file.Settings;
        if (!s.RichPresenceEnabled || !DiscordIpcClient.IsValidClientId(s.RichPresenceClientId))
        {
            if (_discord is not null) await ClearPresenceAsync();
            return;
        }
        var state = _presence.State(clientsRunning > 0, DateTime.Now);
        if (state == _lastPresenceState && clientsRunning == _lastPresenceCabs && _discord?.Connected == true) return;
        if (DateTime.Now < _nextDiscordTry) return;

        try
        {
            if (_discord is null || !_discord.Connected || _discordFor != s.RichPresenceClientId)
            {
                if (_discord is not null) await _discord.DisposeAsync();
                _discord = new DiscordIpcClient();
                _discordFor = s.RichPresenceClientId;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                if (!await _discord.ConnectAsync(s.RichPresenceClientId, cts.Token))
                {
                    _nextDiscordTry = DateTime.Now.AddSeconds(30);
                    await _discord.DisposeAsync();
                    _discord = null;
                    return;
                }
            }
            if (state != _lastPresenceState) _presenceSince = DateTimeOffset.Now;
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var activity = PresenceTracker.Activity(state, clientsRunning, _presenceSince, s.RichPresenceShowInviteButton);
            if (await _discord.SetActivityAsync(activity, Environment.ProcessId, cts2.Token))
            {
                _lastPresenceState = state;
                _lastPresenceCabs = clientsRunning;
            }
            else _nextDiscordTry = DateTime.Now.AddSeconds(30);
        }
        catch (Exception)
        {
            _nextDiscordTry = DateTime.Now.AddSeconds(30);
            if (_discord is not null) { try { await _discord.DisposeAsync(); } catch { } _discord = null; }
        }
    }

    private async Task ClearPresenceAsync()
    {
        var d = _discord;
        _discord = null;
        _lastPresenceState = null;
        if (d is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await d.SetActivityAsync(null, Environment.ProcessId, cts.Token);
        }
        catch { }
        try { await d.DisposeAsync(); } catch { }
    }

    private void ShutdownPresence()
    {
        var d = _discord;
        _discord = null;
        if (d is null) return;
        try { d.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private void NotifyTest_Click(object sender, RoutedEventArgs e)
    {
        if (!NotificationRules.AnyOutput(_file.Settings)) { Warn("Turn on \"Play a sound\" and/or \"Show a Windows notification\" first."); return; }
        ShowNotice(new Notice("match", "EXVS2 Launcher", "This is how a match / queue notification looks."));
    }
}
