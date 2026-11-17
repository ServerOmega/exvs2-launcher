using System.Collections.ObjectModel;
using System.Windows;
using Exvs2Launcher.Core.Matchmaking;

namespace Exvs2Launcher.App;

/// <summary>Add / edit / remove saved matchmaking servers. Works on copies; <see cref="Result"/> is set on Save.</summary>
public partial class MatchmakingServersWindow : Window
{
    private readonly ObservableCollection<MatchmakingServer> _items;

    public MatchmakingServersWindow(IEnumerable<MatchmakingServer> servers)
    {
        InitializeComponent();
        _items = new ObservableCollection<MatchmakingServer>(servers.Select(s => s.Clone()));
        List.ItemsSource = _items;
        if (_items.Count > 0) List.SelectedIndex = 0;
    }

    public List<MatchmakingServer> Result { get; private set; } = new();

    private MatchmakingServer? Selected => List.SelectedItem as MatchmakingServer;

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Selected is not { } s) return;
        NameBox.Text = s.Name;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
        ErrorText.Text = "";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var err = MatchmakingServers.TryCreate(NameBox.Text, HostBox.Text, PortBox.Text, _items, out var s);
        ErrorText.Text = err ?? "";
        if (err is not null) return;
        _items.Add(s);
        List.SelectedItem = s;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) { ErrorText.Text = "Pick a server in the list first."; return; }
        var err = MatchmakingServers.TryCreate(NameBox.Text, HostBox.Text, PortBox.Text, _items.Where(x => !ReferenceEquals(x, sel)), out var s);
        ErrorText.Text = err ?? "";
        if (err is not null) return;
        var i = _items.IndexOf(sel);
        _items[i] = s;
        List.SelectedIndex = i;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } sel) return;
        if (MessageBox.Show(this, $"Remove \"{sel.Name}\" from the list?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _items.Remove(sel);
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in MatchmakingServers.Defaults())
            if (!_items.Any(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))) _items.Add(d);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _items.ToList();
        DialogResult = true;
    }
}
