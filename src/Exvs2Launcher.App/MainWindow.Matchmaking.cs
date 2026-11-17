using System.Windows;
using System.Windows.Threading;
using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.App;

/// <summary>Matchmaking server dropdown (ping + player counts every 15 s) and the card server manager window.</summary>
public partial class MainWindow
{
    private readonly ServerProbe _mmProbe = new();
    private readonly DispatcherTimer _mmTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly List<MatchmakerRow> _mmRows = new();
    private bool _mmProbing;
    private bool _mmSyncing;

    private void InitMatchmakers()
    {
        _file.MatchmakingServers = MatchmakingServers.EnsureDefaults(_file.MatchmakingServers);
        RebuildMatchmakerRows(_file.SelectedMatchmakingServer);
        _mmTimer.Tick += async (_, _) => await ProbeMatchmakersAsync();
        _mmTimer.Start();
        _ = ProbeMatchmakersAsync();
    }

    private void RebuildMatchmakerRows(string? select)
    {
        _mmSyncing = true;
        _mmRows.Clear();
        foreach (var s in _file.MatchmakingServers ?? new()) _mmRows.Add(new MatchmakerRow(s));
        MatchmakerBox.ItemsSource = null;
        MatchmakerBox.ItemsSource = _mmRows;
        MatchmakerBox.SelectedItem = _mmRows.FirstOrDefault(r => r.Server.Name == select) ?? _mmRows.FirstOrDefault();
        _mmSyncing = false;
        SyncMatchmakerToServerJson();
    }

    private async Task ProbeMatchmakersAsync()
    {
        if (_mmProbing) return;
        _mmProbing = true;
        try
        {
            var rows = _mmRows.ToList();
            var results = await Task.WhenAll(rows.Select(async r =>
            {
                try { return await _mmProbe.ProbeAsync(r.Server); }
                catch (Exception ex) { return new ProbeResult(null, ProbeMethod.None, null, ex.Message); }
            }));
            var selected = MatchmakerBox.SelectedItem as MatchmakerRow;
            for (int i = 0; i < rows.Count; i++)
            {
                var previous = rows[i].Probe?.Status;
                rows[i].SetProbe(results[i]);
                if (ReferenceEquals(rows[i], selected))
                    foreach (var n in _notifyRules.FromStatus(rows[i].Server.Name, previous, results[i].Status, _file.Settings))
                        ShowNotice(n);
            }
            UpdateMatchmakerStatusText();
        }
        finally { _mmProbing = false; }
    }

    /// <summary>Selects the saved server that this cab's server.json already points at, and says so.</summary>
    private void SyncMatchmakerToServerJson()
    {
        var json = CardServerConfig.FindServerJson(string.IsNullOrWhiteSpace(ServerPathBox.Text) ? null : ServerPathBox.Text.Trim());
        var current = json is null ? null : CardServerConfig.ReadPublic(json)?.MatchUrl;
        var match = MatchmakingServers.FindByMatchUrl(_mmRows.Select(r => r.Server), current);
        if (match is not null)
        {
            _mmSyncing = true;
            MatchmakerBox.SelectedItem = _mmRows.First(r => ReferenceEquals(r.Server, match));
            _mmSyncing = false;
        }
        UpdateMatchmakerStatusText();
    }

    private void UpdateMatchmakerStatusText()
    {
        var json = CardServerConfig.FindServerJson(string.IsNullOrWhiteSpace(ServerPathBox.Text) ? null : ServerPathBox.Text.Trim());
        var current = json is null ? null : CardServerConfig.ReadPublic(json)?.MatchUrl;
        var inUse = MatchmakingServers.FindByMatchUrl(_mmRows.Select(r => r.Server), current);
        MatchmakerStatusText.Text = json is null
            ? "Pick the card server above (or use Card server...) so the launcher can write the matchmaking server into its server.json."
            : current is null
                ? "This card server has no matchmaking server set yet (offline / local matching only)."
                : inUse is not null
                    ? $"This card server uses {inUse.Name} ({current})."
                    : $"This card server uses {current}, which is not in your list.";
    }

    private void MatchmakerBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_mmSyncing || MatchmakerBox.SelectedItem is not MatchmakerRow row) return;
        _file.SelectedMatchmakingServer = row.Server.Name;
    }

    private void UseMatchmaker_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadForm() || _current is null) return;
        if (MatchmakerBox.SelectedItem is not MatchmakerRow row) { Warn("Pick a matchmaking server first."); return; }
        if (row.Server.MatchUrl is null)
        {
            Warn($"\"{row.Server.Name}\" has no address yet. Click Edit list... and fill in its host (ask in the Discord for the LA / EU addresses).");
            return;
        }
        var json = CardServerConfig.FindServerJson(_current.CardServerPath);
        if (json is null)
        {
            Warn("No Configurations/server.json was found next to this cab's card server. Pick the card server first (or install one with Card server...).");
            return;
        }
        var ok = MessageBox.Show(this,
            $"Point this cab's card server at {row.Server.Name}?\n\n{json}\nOnlineMatching.MatchUrl = {row.Server.MatchUrl}\nStunHost = {row.Server.Host}\n\n" +
            "server.json is backed up first. Restart the card server afterwards so it uses the new server.",
            "EXVS2 Launcher", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            var r = CardServerConfig.Apply(json, row.Server.MatchUrl, null);
            Report("Matchmaking server set to " + row.Server.Name, r);
            _file.SelectedMatchmakingServer = row.Server.Name;
            SaveAll(quiet: true);
            _matchUrlDirty = false;
            RefreshServerSettings();
            UpdateMatchmakerStatusText();
        }
        catch (Exception ex)
        {
            Warn("Could not write server.json: " + ex.Message);
        }
    }

    private void EditMatchmakers_Click(object sender, RoutedEventArgs e)
    {
        var win = new MatchmakingServersWindow(_file.MatchmakingServers ?? new()) { Owner = this };
        if (win.ShowDialog() != true) return;
        _file.MatchmakingServers = win.Result;
        var keep = (MatchmakerBox.SelectedItem as MatchmakerRow)?.Server.Name;
        RebuildMatchmakerRows(win.Result.Any(s => s.Name == keep) ? keep : win.Result.FirstOrDefault()?.Name);
        SaveAll(quiet: true);
        Log("Saved " + win.Result.Count + " matchmaking server(s).");
        _ = ProbeMatchmakersAsync();
    }

    private void CardServerManager_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null && !ReadForm()) return;
        var win = new CardServerWindow(_file, _current, () => SaveAll(quiet: true), Log) { Owner = this };
        win.ShowDialog();
        if (_current is not null)
        {
            _loading = true;
            ServerPathBox.Text = _current.CardServerPath ?? "";
            _loading = false;
            RefreshServerVersion();
            RefreshServerSettings();
            SyncMatchmakerToServerJson();
        }
    }
}
