using System.IO;
using System.Windows;
using Exvs2Launcher.Core.Profiles;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>Review screen for an imported profile. Replacing an existing profile needs a second confirmation.</summary>
public partial class ProfileImportWindow : Window
{
    private readonly ImportReview _review;
    private readonly HashSet<string> _taken;

    public ProfileImportWindow(ImportReview review, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        _review = review;
        _taken = new HashSet<string>(existingNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        LineList.ItemsSource = review.Lines;
        WarningList.ItemsSource = review.Warnings;
        GameBox.Text = review.Profile.GameFolder;
        ServerBox.Text = review.Profile.CardServerPath ?? "";
        if (review.Conflict is not null)
        {
            ConflictPanel.Visibility = Visibility.Visible;
            ConflictText.Text = $"You already have a profile called '{review.Conflict.Name}'.";
            NameBox.Text = review.SuggestedNewName;
        }
        else NameBox.Text = review.Profile.Name;
    }

    public bool Overwrite { get; private set; }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_review?.Conflict is null || NameBox is null) return;
        NameBox.Text = ReplaceRadio.IsChecked == true ? _review.Conflict.Name : _review.SuggestedNewName;
        NameBox.IsEnabled = ReplaceRadio.IsChecked != true;
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick this cab's game folder" };
        if (Directory.Exists(GameBox.Text)) dlg.InitialDirectory = GameBox.Text;
        if (dlg.ShowDialog(this) == true) GameBox.Text = dlg.FolderName;
    }

    private void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Pick the patched card server exe", Filter = "Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == true) ServerBox.Text = dlg.FileName;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "Give the profile a name.", Title); return; }
        var server = ServerBox.Text.Trim();
        if (server.Length > 0 && Path.GetFileName(server).Equals("Server.exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(server) ?? "").Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "That is the stock server\\Server.exe, which the launcher never starts. Pick the patched card server instead.", Title);
            return;
        }
        bool replace = _review.Conflict is not null && ReplaceRadio.IsChecked == true;
        if (!replace && _taken.Contains(name)) { MessageBox.Show(this, "You already have a profile with that name. Pick another name.", Title); return; }
        if (replace && MessageBox.Show(this, $"Replace your profile '{_review.Conflict!.Name}' with the imported one?\n\nThis can't be undone (its serial is kept).",
                "Replace profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _review.Profile.Name = name;
        _review.Profile.GameFolder = GameBox.Text.Trim();
        _review.Profile.CardServerPath = server.Length == 0 ? null : server;
        Overwrite = replace;
        DialogResult = true;
    }
}
