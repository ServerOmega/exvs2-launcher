using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Input;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.App;

/// <summary>Bindings editor for the game's existing keyboard INI keys. No new input driver.</summary>
public partial class KeyboardWindow : Window
{
    private static readonly (string Key, string Label, string Tip)[] Actions =
    {
        ("Up", "Up", "Movement up. Written to [keyboard] Up=."),
        ("Down", "Down", "Movement down. Written to [keyboard] Down=."),
        ("Left", "Left", "Movement left. Written to [keyboard] Left=."),
        ("Right", "Right", "Movement right. Written to [keyboard] Right=."),
        ("A", "A (shoot)", "Primary shoot key. Combined with AB/AC/ABC when writing A= in the game INI."),
        ("B", "B (melee)", "Primary melee key. Combined with AB/BC/ABC when writing B=."),
        ("C", "C (jump)", "Primary jump key. Combined with AC/BC/ABC when writing C=."),
        ("D", "D (target)", "Target-switch key. Written to [keyboard] D=."),
        ("AB", "AB combo", "A+B combo key. Written into both A and B lists in the game INI; stored as ButtonAB in SnowLauncher.ini."),
        ("BC", "BC combo", "B+C combo key. Written into both B and C lists."),
        ("AC", "AC combo", "A+C combo key. Written into both A and C lists."),
        ("ABC", "ABC combo", "A+B+C combo key (burst). Written into A, B, and C lists."),
        ("Start", "Start", "Start. Enter is stored as Return, the hook name observed in the pack."),
        ("Coin", "Coin", "Coin key."),
        ("Card", "Card", "Card key."),
        ("Test", "Test", "Test menu key."),
        ("Service", "Service", "Service key. Optional; the pack has F2 on the client and omits it on the LM."),
        ("Kill", "Kill", "Kill / exit key."),
    };

    private readonly LauncherProfile _profile;
    private readonly KeyboardConfigWriter _writer;
    private readonly Action<string> _log;
    private readonly MapEditor _client;
    private readonly MapEditor _lm;
    private readonly DispatcherTimer _timeout = new();
    private MapEditor? _bindingMap;
    private TextBox? _bindingBox;
    private string? _bindingLabel;
    private DateTime _bindUntil;
    private const int BindTimeoutSeconds = 8;

    public KeyboardWindow(LauncherProfile profile, KeyboardConfigWriter writer, Action<string> log, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        _profile = profile;
        _writer = writer;
        _log = log;
        Title = "Keyboard bindings — " + (string.IsNullOrWhiteSpace(profile.Name) ? "cab" : profile.Name);
        _client = new MapEditor("Client", StartBind, ResetAction);
        _lm = new MapEditor("LM", StartBind, ResetAction);
        ClientHost.Content = _client.Root;
        LmHost.Content = _lm.Root;
        _timeout.Tick += Timeout_Tick;
        Closed += (_, _) => StopBind(cancelled: false, timedOut: false, silent: true);
        LoadFromDisk();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => LoadFromDisk();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ResetClient_Click(object sender, RoutedEventArgs e)
    {
        _client.Show(KeyboardDefaults.Client());
        Status("Client keys restored to the Infinite Boost pack defaults in this window. Click Write bindings to save them.");
    }

    private void ResetLm_Click(object sender, RoutedEventArgs e)
    {
        _lm.Show(KeyboardDefaults.Lm());
        Status("LM keys restored to the Infinite Boost pack defaults in this window. Click Write bindings to save them.");
    }

    private void LoadFromDisk()
    {
        StopBind(cancelled: true, timedOut: false, silent: true);
        if (string.IsNullOrWhiteSpace(_profile.GameFolder) || !Directory.Exists(_profile.GameFolder))
        {
            Status("Pick a game folder first.");
            return;
        }
        try
        {
            var state = _writer.Read(_profile.GameFolder);
            _client.Show(state.Client);
            _lm.Show(state.Lm);
            Status("Loaded bindings from " + _profile.GameFolder + ".");
        }
        catch (Exception ex)
        {
            Status("Could not read keyboard settings: " + ex.Message);
        }
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        StopBind(cancelled: true, timedOut: false, silent: true);
        if (!TryReadForm(out var state, out var error))
        {
            MessageBox.Show(this, error ?? "The bindings are not valid.", "Keyboard bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_profile.GameFolder) || !File.Exists(Path.Combine(_profile.GameFolder, "config.ini")))
        {
            MessageBox.Show(this, "Pick a game folder that has config.ini first.", "Keyboard bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var running = ProcessGuard.RunningConflicts();
        if (running.Count > 0)
        {
            MessageBox.Show(this, "Close these first, then try again: " + string.Join(", ", running) +
                                  "\n\nThe game and LM only pick up keyboard changes after they are started again. SnowLauncher would also put old values back if it is open.",
                "Keyboard bindings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var report = _writer.Apply(_profile.GameFolder, state, _profile.SyncSnowLauncherIni);
            _log("Keyboard bindings written for \"" + _profile.Name + "\".");
            foreach (var w in report.Written) _log("  updated " + w);
            foreach (var b in report.Backups) _log("  backup  " + b);
            foreach (var n in report.Notes) _log("  " + n);
            if (report.Written.Count == 0) _log("  nothing changed");
            Status(report.Written.Count == 0
                ? "Nothing changed — the files already had these bindings."
                : "Wrote bindings. " + report.Written.Count + " file(s) updated, with backups.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the bindings: " + ex.Message, "Keyboard bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryReadForm(out KeyboardFileState state, out string? error)
    {
        state = new KeyboardFileState();
        error = null;
        if (!_client.TryRead(out var client, out error)) return false;
        if (!_lm.TryRead(out var lm, out error)) return false;
        state.Client = client;
        state.Lm = lm;
        return true;
    }

    private void StartBind(MapEditor map, TextBox box, string label)
    {
        StopBind(cancelled: true, timedOut: false, silent: true);
        _bindingMap = map;
        _bindingBox = box;
        _bindingLabel = label;
        _bindUntil = DateTime.UtcNow.AddSeconds(BindTimeoutSeconds);
        box.Focus();
        _timeout.Interval = TimeSpan.FromMilliseconds(200);
        _timeout.Start();
        Status("Press a key for " + map.Side + " " + label + " (" + BindTimeoutSeconds +
               " seconds, or click another Rebind to cancel)…");
    }

    private void ResetAction(MapEditor map, TextBox box, string action)
    {
        StopBind(cancelled: true, timedOut: false, silent: true);
        var defaults = map.Side == "LM" ? KeyboardDefaults.Lm() : KeyboardDefaults.Client();
        box.Text = KeyboardDefaults.Value(defaults, action) ?? "";
        Status("Reset " + map.Side + " " + action + " to " + (box.Text.Length == 0 ? "(empty)" : box.Text) +
               ". Click Write bindings to save.");
    }

    private void Timeout_Tick(object? sender, EventArgs e)
    {
        if (_bindingBox is null) { _timeout.Stop(); return; }
        var left = _bindUntil - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            StopBind(cancelled: false, timedOut: true, silent: false);
            return;
        }
        Status("Press a key for " + (_bindingMap?.Side ?? "") + " " + _bindingLabel +
               " (" + Math.Ceiling(left.TotalSeconds) + " seconds)…");
    }

    private void StopBind(bool cancelled, bool timedOut, bool silent)
    {
        _timeout.Stop();
        var label = _bindingLabel;
        var side = _bindingMap?.Side;
        _bindingMap = null;
        _bindingBox = null;
        _bindingLabel = null;
        if (silent) return;
        if (cancelled)
            Status("Rebind cancelled.");
        else if (timedOut)
            Status("No key was pressed in time for " + side + " " + label + ". You can also type a hook key name and click Write bindings.");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_bindingBox is null) return;
        if (e.IsRepeat) { e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.DeadCharProcessed or Key.ImeProcessed or Key.None)
        {
            e.Handled = true;
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (!KeyboardKeyNames.TryGetName(vk, out var name))
        {
            e.Handled = true;
            Status("The hook has no name for " + key + " (virtual key " + vk +
                   "). Use a letter, number, arrow, function, Enter, Esc, Space, or NumPad key from the documented table.");
            return;
        }

        _bindingBox.Text = name;
        var side = _bindingMap?.Side ?? "";
        var label = _bindingLabel ?? "";
        StopBind(cancelled: false, timedOut: false, silent: true);
        Status("Bound " + side + " " + label + " to " + name + ". Click Write bindings to save.");
        e.Handled = true;
    }

    private void Status(string text) => StatusText.Text = text;

    internal sealed class MapEditor
    {
        public string Side { get; }
        public StackPanel Root { get; } = new();
        private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 0, 0, 8) };
        private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.Ordinal);
        private readonly Action<MapEditor, TextBox, string> _bind;
        private readonly Action<MapEditor, TextBox, string> _reset;

        public MapEditor(string side, Action<MapEditor, TextBox, string> bind, Action<MapEditor, TextBox, string> reset)
        {
            Side = side;
            _bind = bind;
            _reset = reset;
            Root.Children.Add(_enabled);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int row = 0;
            foreach (var action in Actions)
                AddActionRow(grid, row++, action.Key, action.Label, action.Tip);
            Root.Children.Add(grid);
        }

        public void Show(KeyboardMap map)
        {
            _enabled.IsChecked = map.Enabled;
            Set("Up", map.Up);
            Set("Down", map.Down);
            Set("Left", map.Left);
            Set("Right", map.Right);
            Set("A", map.ButtonA);
            Set("B", map.ButtonB);
            Set("C", map.ButtonC);
            Set("D", map.ButtonD);
            Set("AB", map.ButtonAB);
            Set("BC", map.ButtonBC);
            Set("AC", map.ButtonAC);
            Set("ABC", map.ButtonABC);
            Set("Start", map.Start);
            Set("Coin", map.Coin);
            Set("Card", map.Card);
            Set("Test", map.Test);
            Set("Service", map.Service);
            Set("Kill", map.Kill);
        }

        public bool TryRead(out KeyboardMap map, out string? error)
        {
            map = new KeyboardMap { Enabled = _enabled.IsChecked == true };
            KeyboardDefaults.Set(map, "Up", Get("Up"));
            KeyboardDefaults.Set(map, "Down", Get("Down"));
            KeyboardDefaults.Set(map, "Left", Get("Left"));
            KeyboardDefaults.Set(map, "Right", Get("Right"));
            KeyboardDefaults.Set(map, "A", Get("A"));
            KeyboardDefaults.Set(map, "B", Get("B"));
            KeyboardDefaults.Set(map, "C", Get("C"));
            KeyboardDefaults.Set(map, "D", Get("D"));
            KeyboardDefaults.Set(map, "AB", Get("AB"));
            KeyboardDefaults.Set(map, "BC", Get("BC"));
            KeyboardDefaults.Set(map, "AC", Get("AC"));
            KeyboardDefaults.Set(map, "ABC", Get("ABC"));
            KeyboardDefaults.Set(map, "Start", Get("Start"));
            KeyboardDefaults.Set(map, "Coin", Get("Coin"));
            KeyboardDefaults.Set(map, "Card", Get("Card"));
            KeyboardDefaults.Set(map, "Test", Get("Test"));
            KeyboardDefaults.Set(map, "Service", Get("Service"));
            KeyboardDefaults.Set(map, "Kill", Get("Kill"));
            error = KeyboardCombos.ProblemWith(map, Side);
            return error is null;
        }

        private void Set(string key, string? value) => _boxes[key].Text = value ?? "";
        private string? Get(string key) => string.IsNullOrWhiteSpace(_boxes[key].Text) ? null : _boxes[key].Text.Trim();

        private void AddActionRow(Grid grid, int row, string key, string label, string tip)
        {
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lab = new Label { Content = label, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(lab, row);
            grid.Children.Add(lab);
            var box = new TextBox { ToolTip = tip + " Type a hook key name, or Rebind and press the key." };
            _boxes[key] = box;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            var bind = new Button { Content = "Rebind", ToolTip = "Capture the next key press for this action (8 seconds).", Margin = new Thickness(6, 2, 0, 2), MinWidth = 72 };
            bind.Click += (_, _) => _bind(this, box, key);
            Grid.SetRow(bind, row);
            Grid.SetColumn(bind, 2);
            grid.Children.Add(bind);
            var reset = new Button { Content = "Reset", ToolTip = "Restore this action to the Infinite Boost pack default.", Margin = new Thickness(6, 2, 0, 2), MinWidth = 64 };
            reset.Click += (_, _) => _reset(this, box, key);
            Grid.SetRow(reset, row);
            Grid.SetColumn(reset, 3);
            grid.Children.Add(reset);
        }
    }
}
