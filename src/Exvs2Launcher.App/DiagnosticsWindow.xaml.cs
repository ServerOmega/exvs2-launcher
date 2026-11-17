using System.IO;
using System.Windows;
using System.Windows.Controls;
using Exvs2Launcher.Core.Diagnostics;
using Microsoft.Win32;

namespace Exvs2Launcher.App;

/// <summary>Preview every file of the diagnostics zip before saving it.</summary>
public partial class DiagnosticsWindow : Window
{
    private readonly IReadOnlyList<BundleEntry> _entries;
    private readonly string _defaultName;
    private readonly Dictionary<BundleEntry, string> _cache = new();
    private const int PreviewChars = 200_000;

    public DiagnosticsWindow(IReadOnlyList<BundleEntry> entries, string defaultName)
    {
        InitializeComponent();
        _entries = entries;
        _defaultName = defaultName;
        EntryList.ItemsSource = entries;
        StatusText.Text = entries.Count + " file(s)";
        if (entries.Count > 0) EntryList.SelectedIndex = 0;
    }

    public string? SavedPath { get; private set; }

    private async void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not BundleEntry entry) return;
        if (!_cache.TryGetValue(entry, out var text))
        {
            PreviewBox.Text = "Loading...";
            try { text = await Task.Run(entry.Content); }
            catch (Exception ex) { text = "(could not read: " + ex.Message + ")"; }
            _cache[entry] = text;
        }
        if (!ReferenceEquals(EntryList.SelectedItem, entry)) return;
        PreviewBox.Text = text.Length > PreviewChars ? text[..PreviewChars] + "\n\n… (preview cut; the whole text is saved)" : text;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Save diagnostics bundle", Filter = "Zip (*.zip)|*.zip", FileName = _defaultName };
        if (dlg.ShowDialog(this) != true) return;
        StatusText.Text = "Saving...";
        IsEnabled = false;
        try
        {
            // Re-use what was previewed so the saved file matches the preview exactly.
            var fixedEntries = _entries.Select(x => _cache.TryGetValue(x, out var t)
                ? new BundleEntry(x.ZipPath, x.Description, () => t) : x).ToList();
            await Task.Run(() => DiagnosticsBundleBuilder.Write(fixedEntries, dlg.FileName));
            SavedPath = dlg.FileName;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            StatusText.Text = "";
            MessageBox.Show(this, "Could not save the bundle: " + ex.Message, "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
