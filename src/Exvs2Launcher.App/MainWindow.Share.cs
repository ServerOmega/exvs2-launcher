using System.IO;
using System.Windows;
using Exvs2Launcher.Core.Diagnostics;
using Exvs2Launcher.Core.Profiles;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>v0.6.0 profile export / import and the Diagnostics bundle.</summary>
public partial class MainWindow
{
    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) { Warn("Pick a profile to export."); return; }
        if (!SaveAll(quiet: true)) return;
        var serial = MessageBoxResult.No;
        if (!string.IsNullOrWhiteSpace(_current.Serial))
        {
            serial = MessageBox.Show(this,
                "Include this cab's PCB serial in the file?\n\nChoose No when sharing with other people (each cab needs its own serial). " +
                "Card codes, keys and passwords are never exported.", "Export profile", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (serial == MessageBoxResult.Cancel) return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Export profile",
            Filter = "Launcher profile (*.exvs2profile.json)|*.exvs2profile.json|JSON (*.json)|*.json",
            FileName = DiagnosticsBundleBuilder.SafeName(_current.Name) + ".exvs2profile.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var mm = (MatchmakerBox.SelectedItem as MatchmakerRow)?.Server;
            File.WriteAllText(dlg.FileName, ProfileShare.Export(_current, serial == MessageBoxResult.Yes, mm));
            Log("Exported profile '" + _current.Name + "' to " + dlg.FileName + ".");
        }
        catch (Exception ex) { Warn("Could not export the profile: " + ex.Message); }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null && !SaveAll(quiet: true)) return;
        var dlg = new OpenFileDialog
        {
            Title = "Import profile",
            Filter = "Launcher profile (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        ImportReview review;
        try
        {
            if (new FileInfo(dlg.FileName).Length > ProfileShare.MaxFileBytes) throw new InvalidDataException("The file is too big to be a launcher profile.");
            review = ProfileShare.Review(File.ReadAllText(dlg.FileName), _file);
        }
        catch (Exception ex) { Warn("Could not import that file: " + ex.Message); return; }

        var win = new ProfileImportWindow(review, _file.Profiles.Select(p => p.Name)) { Owner = this };
        if (win.ShowDialog() != true) return;
        var added = ProfileShare.Apply(review, _file, win.Overwrite);
        try { _store.Save(_file); } catch (Exception ex) { Log("Could not save profiles: " + ex.Message); }
        Log((win.Overwrite ? "Replaced" : "Imported") + " profile '" + added.Name + "'. Check the Play tab before starting.");
        RefreshProfileBox(added);
        RebuildMatchmakerRows((MatchmakerBox.SelectedItem as MatchmakerRow)?.Server.Name);
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) SaveAll(quiet: true);
        var builder = new DiagnosticsBundleBuilder(new WindowsSystemInfo());
        var entries = builder.Plan(_file, _store.FilePath, _logFile, HealthDir, InstalledVersion);
        var win = new DiagnosticsWindow(entries, builder.DefaultFileName()) { Owner = this };
        if (win.ShowDialog() == true && win.SavedPath is not null)
            Log("Saved diagnostics bundle to " + win.SavedPath + ". Secrets are masked; check it before posting.");
    }
}
