using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Input;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.App;

/// <summary>Bindings editor for the game's existing controller INI keys. No new input driver.</summary>
public partial class ControllerWindow : Window
{
    private static readonly string[] Modes = { "", "xinput-native", "directinput" };
    private static readonly string[] DeviceIds = Enumerable.Range(0, 17).Select(i => i == 16 ? "16 (all devices)" : i.ToString()).ToArray();

    private static readonly (string Key, string Label, string Tip, bool Advanced)[] Actions =
    {
        ("A", "A (shoot)", "Primary A buttons.", false),
        ("B", "B (melee)", "Primary B buttons.", false),
        ("C", "C (jump)", "Primary C buttons.", false),
        ("D", "D (target)", "Primary D buttons.", false),
        ("Start", "Start", "Start button id(s).", false),
        ("Coin", "Coin", "Coin button id(s).", false),
        ("Card", "Card", "Card button id(s).", false),
        ("Test", "Test", "Test button id(s). Optional.", false),
        ("Service", "Service", "Service button id(s). Optional.", false),
        ("Kill", "Kill", "Kill / exit button id(s). Optional.", false),
        ("AB", "AB combo", "A+B combo button id(s). Written into both A and B lists.", true),
        ("BC", "BC combo", "B+C combo button id(s).", true),
        ("AC", "AC combo", "A+C combo button id(s).", true),
        ("ABC", "ABC combo", "A+B+C combo button id(s).", true),
    };

    private readonly LauncherProfile _profile;
    private readonly ControllerConfigWriter _writer;
    private readonly Action<string> _log;
    private readonly IControllerDeviceSource _devices;
    private readonly IControllerButtonCapture? _capture;
    private readonly SlotEditor _slot1;
    private readonly SlotEditor _slot2;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private HwndSource? _hwndSource;
    private SlotEditor? _bindingSlot;
    private TextBox? _bindingBox;
    private string? _bindingLabel;
    private DateTime _bindUntil;
    private HashSet<int> _heldAtBindStart = new();
    private int? _lastSeenButton;
    private string? _lastSeenName;
    private IReadOnlyList<ConnectedController> _listed = Array.Empty<ConnectedController>();
    private const int BindTimeoutSeconds = 8;

    public ControllerWindow(LauncherProfile profile, ControllerConfigWriter writer, Action<string> log, Window? owner,
        IControllerDeviceSource? devices = null)
    {
        InitializeComponent();
        Owner = owner;
        _profile = profile;
        _writer = writer;
        _log = log;
        _devices = devices ?? new WindowsControllerDevices();
        _capture = _devices as IControllerButtonCapture;
        Title = "Controller bindings — " + (string.IsNullOrWhiteSpace(profile.Name) ? "cab" : profile.Name);

        XInputModeBox.ItemsSource = Modes;
        XInputDeviceIdBox.ItemsSource = DeviceIds;
        _slot1 = new SlotEditor("controller", StartBind);
        _slot2 = new SlotEditor("controller-1", StartBind);
        Slot1Host.Content = _slot1.SimpleRoot;
        Slot2Host.Content = _slot2.SimpleRoot;
        Slot1AdvancedHost.Content = _slot1.AdvancedRoot;
        Slot2AdvancedHost.Content = _slot2.AdvancedRoot;
        SourceInitialized += Window_SourceInitialized;
        Closed += Window_Closed;
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();
        RefreshDevices();
        LoadFromDisk();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_hwndSource is null) return;
        _hwndSource.AddHook(WndProc);
        if (_capture is not null && !_capture.StartListening(_hwndSource.Handle))
            Status("Raw Input could not be started. Rebind will still try WinMM and XInput.");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        StopBind(cancelled: false, timedOut: false, silent: true);
        if (_hwndSource is not null) _hwndSource.RemoveHook(WndProc);
        _capture?.StopListening();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmInput = 0x00FF;
        if (msg == wmInput) _capture?.ProcessRawInput(lParam);
        return IntPtr.Zero;
    }

    private SlotEditor CurrentSlot => SlotTabs.SelectedIndex == 1 ? _slot2 : _slot1;

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void UseSelected_Click(object sender, RoutedEventArgs e) => UseDeviceForSlot(CurrentSlot);

    private void RefreshDevices()
    {
        try { _listed = _devices.List(); }
        catch (Exception ex)
        {
            _listed = Array.Empty<ConnectedController>();
            Status("Could not list controllers: " + ex.Message);
        }
        DeviceBox.ItemsSource = _listed.Select(d => d.Display).ToList();
        if (_listed.Count > 0 && DeviceBox.SelectedIndex < 0) DeviceBox.SelectedIndex = 0;
        if (_listed.Count == 0)
            Status("No controllers detected through HID, XInput, or WinMM. You can still type a device name and button numbers under Advanced. Plug the stick in and click Refresh.");
        else
            Status("Detected " + _listed.Count + " controller(s). Select one and click “Use selected controller”, then Rebind next to an action.");
    }

    private void LoadFromDisk()
    {
        try
        {
            var state = _writer.Read(_profile.GameFolder);
            ShowState(state);
            Status("Loaded bindings from " + _profile.GameFolder + ".");
        }
        catch (Exception ex)
        {
            Status("Could not read controller settings: " + ex.Message);
        }
    }

    private void ShowState(ControllerFileState state)
    {
        XInputEnabledCheck.IsChecked = state.XInput.EmulatedXInputEnabled ?? false;
        XInputModeBox.Text = state.XInput.Mode ?? "";
        XInputDeviceIdBox.Text = FormatDeviceId(state.XInput.DeviceId);
        _slot1.Show(state.Controller);
        _slot2.Show(state.Controller1);
    }

    private bool TryReadForm(out ControllerFileState state, out string? error)
    {
        state = new ControllerFileState();
        error = null;
        if (!ControllerCombos.TryParseDeviceId(StripDeviceIdLabel(XInputDeviceIdBox.Text), out var xid, out error))
            return false;
        state.XInput.EmulatedXInputEnabled = XInputEnabledCheck.IsChecked == true;
        state.XInput.Mode = NullIfBlank(XInputModeBox.Text);
        state.XInput.DeviceId = xid;
        if (!_slot1.TryRead(out var s1, out error) || !_slot2.TryRead(out var s2, out error))
            return false;
        state.Controller = s1;
        state.Controller1 = s2;
        error = ControllerCombos.ProblemWith(s1) ?? ControllerCombos.ProblemWith(s2);
        return error is null;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reload from the game folder and discard unsaved changes in this window?",
                "Reload bindings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        LoadFromDisk();
    }

    private void Write_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var state, out var error))
        {
            MessageBox.Show(this, error ?? "The bindings are not valid.", "Controller bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_profile.GameFolder) || !File.Exists(Path.Combine(_profile.GameFolder, "config.ini")))
        {
            MessageBox.Show(this, "Pick a game folder that has config.ini first.", "Controller bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var running = ProcessGuard.RunningConflicts();
        if (running.Count > 0)
        {
            MessageBox.Show(this, "Close these first, then try again: " + string.Join(", ", running) +
                                  "\n\nSnowLauncher keeps its settings in memory and would put the old values back.",
                "Controller bindings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var report = _writer.Apply(_profile.GameFolder, state, _profile.SyncSnowLauncherIni);
            _log("Controller bindings written for \"" + _profile.Name + "\".");
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
            MessageBox.Show(this, "Could not write the bindings: " + ex.Message, "Controller bindings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UseDeviceForSlot(SlotEditor slot)
    {
        if (DeviceBox.SelectedIndex < 0 || DeviceBox.SelectedIndex >= _listed.Count)
        {
            Status("Select a detected controller first, or click Refresh.");
            return;
        }
        var dev = _listed[DeviceBox.SelectedIndex];
        slot.ApplyDevice(dev);
        Status("Set " + slot.Section + " to " + dev.IniName + ".");
    }

    private void StartBind(SlotEditor slot, TextBox box, string label)
    {
        _bindingSlot = slot;
        _bindingBox = box;
        _bindingLabel = label;
        _bindUntil = DateTime.UtcNow.AddSeconds(BindTimeoutSeconds);
        _heldAtBindStart = new HashSet<int>(CurrentPressed());
        CancelBindButton.Visibility = Visibility.Visible;
        Status("Press a button on the selected controller for " + slot.Section + " " + label +
               " (" + BindTimeoutSeconds + " seconds, or Cancel)…");
    }

    private void CancelBind_Click(object sender, RoutedEventArgs e) =>
        StopBind(cancelled: true, timedOut: false, silent: false);

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLastPressed();
        if (_bindingBox is null) return;
        if (DateTime.UtcNow >= _bindUntil)
        {
            StopBind(cancelled: false, timedOut: true, silent: false);
            return;
        }

        var remaining = Math.Max(1, (int)Math.Ceiling((_bindUntil - DateTime.UtcNow).TotalSeconds));
        foreach (var dev in BindCandidates())
        {
            IReadOnlyList<int> pressed;
            try { pressed = _devices.PressedButtons(dev); }
            catch { continue; }
            var fresh = pressed.Where(id => !_heldAtBindStart.Contains(id)).ToList();
            if (fresh.Count == 0) continue;
            _bindingBox.Text = ControllerCombos.FormatList(fresh);
            Status("Bound " + (_bindingSlot?.Section ?? "controller") + " " + _bindingLabel + " to " + _bindingBox.Text +
                   " (" + dev.Display + "). Click Write bindings to save.");
            StopBind(cancelled: false, timedOut: false, silent: true);
            return;
        }
        Status("Press a button on the selected controller for " + (_bindingSlot?.Section ?? "controller") + " " +
               _bindingLabel + " (" + remaining + "s left, or Cancel)…");
    }

    private void StopBind(bool cancelled, bool timedOut, bool silent)
    {
        var box = _bindingBox;
        _bindingBox = null;
        _bindingSlot = null;
        _bindingLabel = null;
        _heldAtBindStart.Clear();
        CancelBindButton.Visibility = Visibility.Collapsed;
        if (silent || box is null) return;
        if (cancelled)
            Status("Rebind cancelled. The Last pressed line still updates when you press a button.");
        else if (timedOut)
            Status("No new button was pressed in time. Check that Last pressed updates when you press the stick. You can also type a button number under Advanced.");
    }

    private IEnumerable<ConnectedController> BindCandidates()
    {
        if (DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < _listed.Count)
            return new[] { _listed[DeviceBox.SelectedIndex] }.Concat(_listed.Where((_, i) => i != DeviceBox.SelectedIndex));
        return _listed;
    }

    private ConnectedController? SelectedDevice() =>
        DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < _listed.Count ? _listed[DeviceBox.SelectedIndex] : null;

    private IReadOnlyList<int> CurrentPressed()
    {
        foreach (var dev in BindCandidates())
        {
            try
            {
                var pressed = _devices.PressedButtons(dev);
                if (pressed.Count > 0) return pressed;
            }
            catch { }
        }
        return Array.Empty<int>();
    }

    private void UpdateLastPressed()
    {
        var dev = SelectedDevice();
        IReadOnlyList<int> pressed = Array.Empty<int>();
        if (dev is not null)
        {
            try { pressed = _devices.PressedButtons(dev); }
            catch { }
        }
        if (pressed.Count == 0)
        {
            foreach (var other in BindCandidates())
            {
                if (ReferenceEquals(other, dev)) continue;
                try
                {
                    pressed = _devices.PressedButtons(other);
                    if (pressed.Count > 0) { dev = other; break; }
                }
                catch { }
            }
        }
        if (pressed.Count > 0)
        {
            _lastSeenButton = pressed[0];
            _lastSeenName = dev?.Name;
            LastPressedText.Text = "Last pressed: " +
                (pressed.Count == 1 ? "button " + pressed[0] : "buttons " + string.Join(", ", pressed)) +
                (dev is null ? "" : "  (" + dev.Name + ")");
        }
        else if (_lastSeenButton is int last)
        {
            LastPressedText.Text = "Last pressed: button " + last +
                (string.IsNullOrWhiteSpace(_lastSeenName) ? "" : "  (" + _lastSeenName + ")");
        }
        else if (_capture?.LastGameButton is int captured)
        {
            LastPressedText.Text = "Last pressed: button " + captured;
        }
        else
        {
            LastPressedText.Text = "Last pressed: (none yet)";
        }
    }

    private void Status(string text) => StatusText.Text = text;

    internal static string FormatDeviceId(int? id) =>
        id is null ? "" : id == ControllerCombos.AllDevicesId ? "16 (all devices)" : id.Value.ToString();

    internal static string StripDeviceIdLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    internal static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal sealed class SlotEditor
    {
        public string Section { get; }
        public StackPanel SimpleRoot { get; } = new();
        public Grid AdvancedRoot { get; } = new();
        private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBlock _current = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBox _name = new();
        private readonly ComboBox _mode = new() { IsEditable = true, ItemsSource = Modes };
        private readonly ComboBox _deviceId = new() { IsEditable = true, ItemsSource = DeviceIds };
        private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.Ordinal);
        private readonly Action<SlotEditor, TextBox, string> _bind;

        public SlotEditor(string section, Action<SlotEditor, TextBox, string> bind)
        {
            Section = section;
            _bind = bind;
            SimpleRoot.Children.Add(_enabled);
            SimpleRoot.Children.Add(_current);
            var rebind = new GroupBox { Header = "Rebind" };
            var rebindGrid = NewFieldGrid();
            rebind.Content = rebindGrid;
            int row = 0;
            foreach (var action in Actions.Where(a => !a.Advanced))
                AddActionRow(rebindGrid, row++, action.Key, action.Label, action.Tip);
            SimpleRoot.Children.Add(rebind);

            AdvancedRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            AdvancedRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AdvancedRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int adv = 0;
            AddAdvancedLabeled(adv++, "Name", _name, "The Name= value written to the INI. Use the selector above when you can; this is the game-compatible stored name.");
            AddAdvancedLabeled(adv++, "Mode", _mode, "Leave empty for a DirectInput / fight-stick layout. Use xinput-native for an XInput pad. directinput is also observed.");
            AddAdvancedLabeled(adv++, "DeviceId", _deviceId, "0–15 is one pad or stick; 16 means all devices.");
            foreach (var action in Actions.Where(a => a.Advanced))
                AddActionRow(AdvancedRoot, adv++, action.Key, action.Label, action.Tip);
        }

        public void Show(ControllerSlot slot)
        {
            _enabled.IsChecked = slot.Enabled;
            _name.Text = slot.Name ?? "";
            _mode.Text = slot.Mode ?? "";
            _deviceId.Text = FormatDeviceId(slot.DeviceId);
            Set("A", slot.ButtonA);
            Set("B", slot.ButtonB);
            Set("C", slot.ButtonC);
            Set("D", slot.ButtonD);
            Set("AB", slot.ButtonAB);
            Set("BC", slot.ButtonBC);
            Set("AC", slot.ButtonAC);
            Set("ABC", slot.ButtonABC);
            Set("Start", slot.Start);
            Set("Coin", slot.Coin);
            Set("Card", slot.Card);
            Set("Test", slot.Test);
            Set("Service", slot.Service);
            Set("Kill", slot.Kill);
            RefreshCurrent();
        }

        public bool TryRead(out ControllerSlot slot, out string? error)
        {
            slot = new ControllerSlot { Section = Section };
            error = null;
            if (!ControllerCombos.TryParseDeviceId(StripDeviceIdLabel(_deviceId.Text), out var id, out error))
                return false;
            slot.Enabled = _enabled.IsChecked == true;
            slot.Name = NullIfBlank(_name.Text);
            slot.Mode = NullIfBlank(_mode.Text);
            slot.DeviceId = id;
            slot.ButtonA = Get("A");
            slot.ButtonB = Get("B");
            slot.ButtonC = Get("C");
            slot.ButtonD = Get("D");
            slot.ButtonAB = Get("AB");
            slot.ButtonBC = Get("BC");
            slot.ButtonAC = Get("AC");
            slot.ButtonABC = Get("ABC");
            slot.Start = Get("Start");
            slot.Coin = Get("Coin");
            slot.Card = Get("Card");
            slot.Test = Get("Test");
            slot.Service = Get("Service");
            slot.Kill = Get("Kill");
            error = ControllerCombos.ProblemWith(slot);
            return error is null;
        }

        public void ApplyDevice(ConnectedController dev)
        {
            _enabled.IsChecked = true;
            _name.Text = dev.IniName;
            if (dev.XInputIndex is int slot)
            {
                _mode.Text = "xinput-native";
                _deviceId.Text = slot.ToString();
            }
            else if (dev.WinmmId is int joy)
            {
                if (string.IsNullOrWhiteSpace(_mode.Text) || _mode.Text.Trim() == "xinput-native")
                    _mode.Text = "";
                _deviceId.Text = joy.ToString();
            }
            RefreshCurrent();
        }

        private void RefreshCurrent()
        {
            var name = NullIfBlank(_name.Text) ?? "(no device name yet)";
            var mode = NullIfBlank(_mode.Text);
            _current.Text = "Current device: " + name + (mode is null ? "" : "  ·  " + mode);
        }

        private void Set(string key, string? value) => _boxes[key].Text = value ?? "";
        private string? Get(string key) => NullIfBlank(_boxes[key].Text);

        private static Grid NewFieldGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            return grid;
        }

        private void AddActionRow(Grid grid, int row, string key, string label, string tip)
        {
            EnsureRow(grid, row);
            var box = new TextBox { IsReadOnly = grid != AdvancedRoot && key is not ("Test" or "Service" or "Kill") };
            // Keep primary bindings editable too so people can type a number if capture misses.
            box.IsReadOnly = false;
            _boxes[key] = box;
            var lab = new Label { Content = label, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(lab, row);
            grid.Children.Add(lab);
            box.ToolTip = tip + " 1-based button numbers, or Rebind and press the stick.";
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            var bind = new Button { Content = "Rebind", ToolTip = box.ToolTip, Margin = new Thickness(6, 2, 0, 2), MinWidth = 72 };
            bind.Click += (_, _) => _bind(this, box, label);
            Grid.SetRow(bind, row);
            Grid.SetColumn(bind, 2);
            grid.Children.Add(bind);
            var clear = new Button { Content = "Clear", ToolTip = "Clear this binding.", Margin = new Thickness(6, 2, 0, 2), MinWidth = 64 };
            clear.Click += (_, _) => { box.Text = ""; };
            Grid.SetRow(clear, row);
            Grid.SetColumn(clear, 3);
            grid.Children.Add(clear);
        }

        private void AddAdvancedLabeled(int row, string label, UIElement field, string tip)
        {
            EnsureRow(AdvancedRoot, row);
            var lab = new Label { Content = label, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(lab, row);
            AdvancedRoot.Children.Add(lab);
            field.SetValue(ToolTipProperty, tip);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            Grid.SetColumnSpan(field, 3);
            AdvancedRoot.Children.Add(field);
        }

        private static void EnsureRow(Grid grid, int row)
        {
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }
}
