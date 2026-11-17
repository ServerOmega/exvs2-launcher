using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Exvs2Launcher.Core.Health;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Notifications;
using Exvs2Launcher.Core.Presence;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.App;

/// <summary>
/// v0.6.0: Start everything / Stop everything, the All cabs lights, and the 2-second background tick that feeds the
/// Health tab, Rich Presence and notifications from one read-only process snapshot.
/// </summary>
public partial class MainWindow
{
    private SequencerCabControl _cabControl = null!;
    private MultiCabOrchestrator _orchestrator = null!;
    private readonly ObservableCollection<CabRow> _cabRows = new();
    private CancellationTokenSource? _everythingCts;
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SystemProcessControl _processList = new();
    private IReadOnlyList<RunningProcessInfo> _snapshot = Array.Empty<RunningProcessInfo>();
    private HealthMonitor _health = null!;
    private readonly ObservableCollection<HealthEvent> _healthEvents = new();
    private readonly PresenceTracker _presence = new();
    private NotificationRules _notifyRules = new();
    private bool _ticking;
    private string _dataDir = "";
    private string HealthDir => Path.Combine(_dataDir, "health-events");
    private string CardBackupDir => Path.Combine(_dataDir, "card-backups");

    private void InitV06()
    {
        _dataDir = Path.GetDirectoryName(_store.FilePath)!;
        _cabControl = new SequencerCabControl(() => _file,
            () => new StartSequencer(_starter, _probe, _owners, new RealDelay()), _writer);
        _cabControl.Log += Log;
        _orchestrator = new MultiCabOrchestrator(_cabControl, new RealDelay());
        _orchestrator.Log += Log;
        _orchestrator.LightChanged += (cab, role, light) => Dispatcher.BeginInvoke(() =>
        {
            foreach (var r in _cabRows.Where(r => r.Name == cab)) r.SetLight(role, light);
        });
        CabsList.ItemsSource = _cabRows;
        RebuildCabRows();

        _health = new HealthMonitor((p, role) => ProfileProcessMatcher.Status(role, SequencerCabControl.StatusPlan(p),
            Array.Empty<IStartedProcess>(), _snapshot), saveDir: HealthDir);
        HealthList.ItemsSource = _healthEvents;
        CollectionViewSource.GetDefaultView(_healthEvents).Filter = o =>
            HealthImportantOnly.IsChecked != true || (o is HealthEvent e && e.Important);

        _loading = true;
        try
        {
            EverythingLmDelayBox.Text = _file.Settings.LmToClientsDelaySeconds.ToString();
            EverythingStaggerBox.Text = _file.Settings.ClientStaggerSeconds.ToString();
            LoadSettingsForm();
        }
        finally { _loading = false; }

        InitCardsAndStats();
        InitNotifier();
        _tickTimer.Tick += async (_, _) => await TickAsync();
        _tickTimer.Start();
        HealthStatusText.Text = "Saved problem events: " + HealthDir;
    }

    private void RebuildCabRows()
    {
        _cabRows.Clear();
        foreach (var p in _file.Profiles)
        {
            var row = new CabRow(p);
            row.IncludeChanged += () => { try { _store.Save(_file); } catch { } };
            _cabRows.Add(row);
        }
    }

    private void EverythingDelay_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ReadEverythingDelays();
        try { _store.Save(_file); } catch { }
    }

    private void ReadEverythingDelays()
    {
        if (int.TryParse(EverythingLmDelayBox.Text.Trim(), out var lm)) _file.Settings.LmToClientsDelaySeconds = lm;
        if (int.TryParse(EverythingStaggerBox.Text.Trim(), out var st)) _file.Settings.ClientStaggerSeconds = st;
        _file.Settings.Normalize();
        EverythingLmDelayBox.Text = _file.Settings.LmToClientsDelaySeconds.ToString();
        EverythingStaggerBox.Text = _file.Settings.ClientStaggerSeconds.ToString();
    }

    private async void StartEverything_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator.IsRunning || _starting) return;
        if (_current is not null && !SaveAll(quiet: true)) return;
        ReadEverythingDelays();
        if (_cabRows.Count != _file.Profiles.Count) RebuildCabRows();
        _everythingCts = new CancellationTokenSource();
        _starting = true;
        SetEverythingButtons();
        EverythingStatusText.Text = "Starting everything...";
        SetStage("Start everything...");
        try
        {
            Log("--- Start everything ---");
            var result = await _orchestrator.StartEverythingAsync(_file.Profiles.ToList(), _file.Settings, _everythingCts.Token);
            var text = result.Message + (result.Problems.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Problems) : "");
            EverythingStatusText.Text = text;
            SetStage(result.Succeeded ? "Everything started" : result.Cancelled ? "Start everything cancelled" : "Start everything: " + result.Problems.Count + " problem(s)");
            try { _store.Save(_file); } catch { }
            if (_current is not null) ShowProfile(_current);
        }
        catch (Exception ex) { Warn("Start everything failed: " + ex.Message); }
        finally
        {
            _starting = false;
            SetEverythingButtons();
            RefreshComponentStatus();
        }
    }

    private void CancelEverything_Click(object sender, RoutedEventArgs e) => _everythingCts?.Cancel();

    private void StopEverything_Click(object sender, RoutedEventArgs e)
    {
        var included = _file.Profiles.Where(p => p.IncludeInStartEverything && !string.IsNullOrWhiteSpace(p.GameFolder)).ToList();
        if (included.Count == 0) { Log("No cab profiles are included in Start everything."); return; }
        if (MessageBox.Show(this,
                $"Close the clients, the LM and the card servers of {included.Count} cab profile(s)?\n\n" +
                string.Join(", ", included.Select(p => p.Name)) + "\n\nDon't do this while someone is mid-match.",
                "Stop everything", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var p in included)
            foreach (var role in ComponentRoles.All) _health.ExpectStop(p.Name, role);
        Log("--- Stop everything ---");
        _orchestrator.StopEverything(included);
        RefreshComponentStatus();
    }

    private void SetEverythingButtons()
    {
        bool busy = _starting;
        StartEverythingButton.IsEnabled = !busy;
        StopEverythingButton.IsEnabled = !busy;
        CancelEverythingButton.IsEnabled = _orchestrator.IsRunning || (busy && _everythingCts is not null && !_everythingCts.IsCancellationRequested);
        StartButton.IsEnabled = !busy;
    }

    /// <summary>One read-only snapshot every 2 s: lights, health events, presence and notifications.</summary>
    private async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var profiles = _file.Profiles.ToList();
            _health.ContextLines = _file.Settings.HealthContextLines;
            var events = await Task.Run(() =>
            {
                try { _snapshot = _processList.List(); } catch (Exception) { _snapshot = Array.Empty<RunningProcessInfo>(); }
                try { return _health.Poll(profiles); } catch (Exception) { return (IReadOnlyList<HealthEvent>)Array.Empty<HealthEvent>(); }
            });

            if (_cabRows.Count != _file.Profiles.Count || _cabRows.Select(r => r.Profile).Except(_file.Profiles).Any()) RebuildCabRows();
            int clientsRunning = 0;
            foreach (var row in _cabRows)
            {
                var plan = SequencerCabControl.StatusPlan(row.Profile);
                foreach (var role in ComponentRoles.All)
                {
                    bool running = ProfileProcessMatcher.Status(role, plan, Array.Empty<IStartedProcess>(), _snapshot).Running;
                    if (role == ComponentRoles.Client && running) clientsRunning++;
                    if (_orchestrator.IsRunning) continue;
                    var cur = row.Light(role);
                    if (running) row.SetLight(role, CabLight.Running);
                    else if (cur is not (CabLight.Failed or CabLight.Skipped)) row.SetLight(role, CabLight.Off);
                }
                row.RefreshText();
            }

            foreach (var ev in events)
            {
                _healthEvents.Insert(0, ev);
                _presence.Observe(ev);
                if (ev.Important) Log($"Health: {ev.Cab} — {ev.KindLabel}: {ev.Message}");
                var notice = _notifyRules.FromHealth(ev, _file.Settings);
                if (notice is not null) ShowNotice(notice);
            }
            while (_healthEvents.Count > 1000) _healthEvents.RemoveAt(_healthEvents.Count - 1);

            await UpdatePresenceAsync(clientsRunning);
        }
        catch (Exception ex) { Log("Background check failed: " + ex.Message); }
        finally { _ticking = false; }
    }

    // ---------- Health tab ----------

    private void HealthFilter_Changed(object sender, RoutedEventArgs e) => CollectionViewSource.GetDefaultView(_healthEvents)?.Refresh();

    private void HealthOpenEvent_Click(object sender, RoutedEventArgs e)
    {
        if (HealthList.SelectedItem is not HealthEvent ev) return;
        if (ev.SavedPath is null || !File.Exists(ev.SavedPath))
        {
            Log("That event has no saved file (only problems are saved).");
            return;
        }
        OpenPath(ev.SavedPath);
    }

    private void HealthOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(HealthDir);
        OpenPath(HealthDir);
    }

    private void HealthClear_Click(object sender, RoutedEventArgs e) => _healthEvents.Clear();

    private void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Warn("Could not open " + path + ": " + ex.Message); }
    }

    private void ShutdownV06()
    {
        _tickTimer.Stop();
        try { _everythingCts?.Cancel(); } catch { }
        ShutdownPresence();
        _notifier?.Dispose();
    }
}
