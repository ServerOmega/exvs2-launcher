using System.IO;
using System.Net.Http;
using System.Windows;
using Exvs2Launcher.Core.Cards;
using Exvs2Launcher.Core.Identity;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Matchmaking;
using Exvs2Launcher.Core.Net;

namespace Exvs2Launcher.App;

/// <summary>v0.6.0 Cards tab (card list, this cab card's stats, card.ini backup / restore) and Stats tab (boards, history).</summary>
public partial class MainWindow
{
    private static readonly HttpClient CardHttp = ServerProbe.CreateClient();

    private sealed record BoardItem(string Key, string Label) { public override string ToString() => Label; }

    private void InitCardsAndStats()
    {
        BoardBox.ItemsSource = LeaderboardBoards.All.Select(b => new BoardItem(b, LeaderboardBoards.Label(b))).ToList();
        BoardBox.SelectedIndex = 0;
        RefreshCardBackups();
    }

    private CardServerApi? CardApi(out string? problem)
    {
        problem = null;
        if (_current is null) { problem = "Pick a cab profile first."; return null; }
        var url = WebUiUrl.BuildOrNull(_current.CardServerHost, _current.CardServerPort);
        if (url is null) { problem = "This profile's card server address is not valid."; return null; }
        try { return new CardServerApi(CardHttp, url); }
        catch (Exception ex) { problem = ex.Message; return null; }
    }

    private CardIdentity? CurrentCard(out string? problem)
    {
        problem = null;
        if (_current is null || string.IsNullOrWhiteSpace(_current.GameFolder)) { problem = "Pick this cab's game folder first."; return null; }
        var card = CardIniBackup.ReadCard(_current.GameFolder);
        if (card is null) problem = "This cab's card.ini is missing or has no valid card.";
        return card;
    }

    private async void RefreshCards_Click(object sender, RoutedEventArgs e)
    {
        var api = CardApi(out var problem);
        if (api is null) { CardsStatusText.Text = problem; return; }
        CardsStatusText.Text = "Loading...";
        try
        {
            var cards = await api.GetCardsAsync(CardSearchBox.Text);
            CardsList.ItemsSource = cards;
            CardsStatusText.Text = $"{cards.Count} card(s) on {api.BaseUri.Authority}.";
        }
        catch (Exception ex) { CardsStatusText.Text = "Could not load cards: " + ex.Message + " Is the card server running?"; }
    }

    private async void LoadMyCard_Click(object sender, RoutedEventArgs e)
    {
        var api = CardApi(out var problem);
        var card = api is null ? null : CurrentCard(out problem);
        if (api is null || card is null) { MyCardText.Text = problem; return; }
        MyCardText.Text = "Loading...";
        string name = "(unknown)";
        try { name = await api.GetDisplayNameAsync(card) ?? "(no name yet)"; }
        catch (Exception ex) { MyCardText.Text = "Could not reach the card server: " + ex.Message; return; }
        try
        {
            var s = await api.GetCardStatsAsync(card);
            MyCardText.Text = $"Name: {name}    Battles: {s.Battles} ({s.Wins} W / {s.Losses} L, {s.WinRate:0.#}% wins)    " +
                              $"Last played: {(s.LastPlayed is { } lp ? lp.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "no recorded battles")}";
        }
        catch (Exception ex)
        {
            // Older card servers have no Stats page: fall back to the recent battle list.
            try
            {
                var recent = await api.GetRecentBattlesAsync(card);
                var last = recent.Where(r => r.PlayedAt is not null).Select(r => r.PlayedAt).Max();
                MyCardText.Text = $"Name: {name}    Recent battles: {recent.Count}    Last played: {(last is { } l ? l.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "none")}" +
                                  "    (full stats need a card server with the Stats page)";
            }
            catch { MyCardText.Text = $"Name: {name}. Stats not available: {ex.Message}"; }
        }
    }

    private void RefreshCardBackups()
    {
        if (_current is null) { CardBackupsBox.ItemsSource = null; return; }
        CardBackupsBox.ItemsSource = new CardIniBackup(CardBackupDir).List(_current.Name);
        CardBackupsBox.SelectedIndex = 0;
    }

    private void BackupCard_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || string.IsNullOrWhiteSpace(_current.GameFolder)) { Warn("Pick this cab's game folder first."); return; }
        try
        {
            var path = new CardIniBackup(CardBackupDir).Backup(_current.Name, _current.GameFolder);
            Log("Backed up card.ini to " + path + " (this file holds the card keys; keep it private).");
            RefreshCardBackups();
        }
        catch (Exception ex) { Warn("Could not back up card.ini: " + ex.Message); }
    }

    private void RestoreCard_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || CardBackupsBox.SelectedItem is not CardBackupInfo backup) { Warn("Pick a backup first."); return; }
        var plan = StatusPlan();
        bool running = plan is not null && (_launcher.Sequencer.RoleStatus(ComponentRoles.Lm, plan).Running ||
                                            _launcher.Sequencer.RoleStatus(ComponentRoles.Client, plan).Running);
        if (running) { Warn("Close this cab's game and LM before restoring its card.ini."); return; }
        if (MessageBox.Show(this, $"Replace this cab's card.ini with the backup from {backup.CreatedAt:yyyy-MM-dd HH:mm:ss}?\n\n" +
                                  "The current card.ini is backed up next to it first.", "Restore card.ini",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var replaced = new CardIniBackup(CardBackupDir).Restore(backup.Path, _current.GameFolder, running);
            Log("Restored card.ini from " + Path.GetFileName(backup.Path) + (replaced is null ? "." : "; previous card.ini saved as " + Path.GetFileName(replaced) + "."));
            RefreshGameState();
        }
        catch (Exception ex) { Warn("Could not restore card.ini: " + ex.Message); }
    }

    private void OpenCardBackups_Click(object sender, RoutedEventArgs e)
    {
        var dir = _current is null ? CardBackupDir : new CardIniBackup(CardBackupDir).FolderFor(_current.Name);
        Directory.CreateDirectory(dir);
        OpenPath(dir);
    }

    // ---------- Stats tab ----------

    private async void RefreshBoard_Click(object sender, RoutedEventArgs e)
    {
        var board = (BoardBox.SelectedItem as BoardItem)?.Key ?? LeaderboardBoards.ClassSolo;
        BoardStatusText.Text = "Loading...";
        LeaderboardBoard? result = null;
        string? firstError = null;
        var api = CardApi(out var problem);
        if (api is not null)
        {
            try
            {
                result = await api.GetBoardAsync(board);
                if (!result.Available) { firstError = result.Message ?? "not available"; result = null; }
            }
            catch (Exception ex) { firstError = ex.Message; }
        }
        else firstError = problem;

        if (result is null && MatchmakerBox.SelectedItem is MatchmakerRow row && row.Server.HasAddress)
        {
            try { result = await CardServerApi.GetMatchmakerBoardAsync(CardHttp, row.Server.Host, row.Server.Port, board); }
            catch (Exception ex) { firstError += " / matchmaker: " + ex.Message; }
        }

        if (result is null || !result.Available)
        {
            BoardList.ItemsSource = null;
            BoardStatusText.Text = "Leaderboard not available: " + (result?.Message ?? firstError);
            return;
        }
        BoardList.ItemsSource = result.Rows;
        var mine = "";
        var card = api is null ? null : CurrentCard(out _);
        if (card is not null && api is not null)
        {
            try
            {
                var name = await api.GetDisplayNameAsync(card);
                var row2 = LeaderboardBoards.FindByName(result, name);
                mine = row2 is null ? $"  This cab's card ({name}) is not on this board yet (or its name is hidden)." : $"  This cab's card: rank #{row2.Rank}.";
                if (row2 is not null) BoardList.SelectedItem = row2;
            }
            catch { }
        }
        BoardStatusText.Text = $"{LeaderboardBoards.Label(board)} via {result.Source}{(string.IsNullOrWhiteSpace(result.Region) ? "" : " (" + result.Region + ")")}: " +
                               $"{result.Ranked} ranked, min {result.MinMatches} matches" +
                               (result.Updated is { } u ? $", updated {u.ToLocalTime():HH:mm}" : "") + "." + mine;
    }

    private async void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        var api = CardApi(out var problem);
        var card = api is null ? null : CurrentCard(out problem);
        if (api is null || card is null) { HistoryStatusText.Text = problem; return; }
        HistoryStatusText.Text = "Loading...";
        try
        {
            var list = await api.GetRecentBattlesAsync(card);
            HistoryList.ItemsSource = list;
            HistoryStatusText.Text = list.Count == 0 ? "No recorded battles for this card yet." : $"{list.Count} recent battle(s), {list.Count(b => b.Win)} won.";
        }
        catch (Exception ex) { HistoryStatusText.Text = "Could not load matches: " + ex.Message; }
    }
}
