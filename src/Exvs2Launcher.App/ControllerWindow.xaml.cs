using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exvs2Launcher.Core.Files;
using Exvs2Launcher.Core.Input;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.App;

/// <summary>Bindings editor for the game's existing controller INI keys. No new input driver.</summary>
public partial class ControllerWindow : Window
{
    private static readonly string[] Modes = { "", "xinput-native" };
    private static readonly string[] DeviceIds = Enumerable.Range(0, 17).Select(i => i == 16 ? "16 (all devices)" : i.ToString()).ToArray();

    private readonly LauncherProfile _profile;
    private readonly ControllerConfigWriter _writer;
    private readonly Action<string> _log;
    private readonly IControllerDeviceSource _devices;
    private readonly Window? _ownerWindow;
    private readonly SlotEditor _slot1;
    private readonly SlotEditor _slot2;
    private readonly DispatcherTimer _bindTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private SlotEditor? _bindingSlot;
    private TextBox? _bindingBox;
    private string? _bindingLabel;
    private DateTime _bindUntil;
    private IReadOnlyList<ConnectedController> _listed = Array.Empty<ConnectedController>();

    public ControllerWindow(LauncherProfile profile, ControllerConfigWriter writer, Action<string> log, Window? owner,
        IControllerDeviceSource? devices = null)
    {
        InitializeComponent();
        Owner = owner;
        _ownerWindow = owner;
        _profile = profile;
        _writer = writer;
        _log = log;
        _devices = devices ?? new WindowsControllerDevices();
        Title = "Controller bindings — " + (string.IsNullOrWhiteSpace(profile.Name) ? "cab" : profile.Name);

        XInputModeBox.ItemsSource = Modes;
        XInputDeviceIdBox.ItemsSource = DeviceIds;
        _slot1 = new SlotEditor("controller", UseDeviceForSlot, StartBind);
        _slot2 = new SlotEditor("controller-1", UseDeviceForSlot, StartBind);
        Slot1Host.Content = _slot1.Root;
        Slot2Host.Content = _slot2.Root;
        _bindTimer.Tick += BindTimer_Tick;
        Closed += (_, _) => _bindTimer.Stop();
        RefreshDevices();
        LoadFromDisk();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

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
            Status("No controllers detected through XInput or WinMM. You can still type a device name and button numbers. Plug the stick in and click Refresh.");
        else
            Status("Detected " + _listed.Count + " controller(s). Select one and click “Use this device” on a slot, or press Bind next to a button.");
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
        Status("Set " + slot.Section + " to " + dev.Display + ".");
    }

    private void StartBind(SlotEditor slot, TextBox box, string label)
    {
        _bindingSlot = slot;
        _bindingBox = box;
        _bindingLabel = label;
        _bindUntil = DateTime.UtcNow.AddSeconds(4);
        _bindTimer.Start();
        Status("Press a button on the controller for " + slot.Section + " " + label + " (4 seconds)…");
    }

    private void BindTimer_Tick(object? sender, EventArgs e)
    {
        if (_bindingBox is null)
        {
            _bindTimer.Stop();
            return;
        }
        if (DateTime.UtcNow >= _bindUntil)
        {
            _bindTimer.Stop();
            Status("No button was pressed in time. You can type a button number instead.");
            _bindingBox = null;
            return;
        }

        IEnumerable<ConnectedController> candidates = _listed;
        if (DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < _listed.Count)
            candidates = new[] { _listed[DeviceBox.SelectedIndex] }.Concat(_listed.Where((_, i) => i != DeviceBox.SelectedIndex));

        foreach (var dev in candidates)
        {
            IReadOnlyList<int> pressed;
            try { pressed = _devices.PressedButtons(dev); }
            catch { continue; }
            if (pressed.Count == 0) continue;
            _bindTimer.Stop();
            _bindingBox.Text = ControllerCombos.FormatList(pressed);
            Status("Bound " + (_bindingSlot?.Section ?? "controller") + " " + _bindingLabel + " to " + _bindingBox.Text +
                   " (" + dev.Display + "). Click Write bindings to save.");
            _bindingBox = null;
            return;
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
        public Grid Root { get; } = new();
        private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 4, 14, 4) };
        private readonly TextBox _name = new();
        private readonly ComboBox _mode = new() { IsEditable = true, ItemsSource = Modes };
        private readonly ComboBox _deviceId = new() { IsEditable = true, ItemsSource = DeviceIds };
        private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.Ordinal);
        private readonly Action<SlotEditor> _useDevice;
        private readonly Action<SlotEditor, TextBox, string> _bind;

        public SlotEditor(string section, Action<SlotEditor> useDevice, Action<SlotEditor, TextBox, string> bind)
        {
            Section = section;
            _useDevice = useDevice;
            _bind = bind;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int row = 0;
            AddRow(row++, _enabled, span: 2);
            AddLabeled(row++, "Name", _name, "Use this device", (_, _) => _useDevice(this),
                "Copy the selected detected controller into this slot (name, and XInput mode or device id when known).");
            AddLabeled(row++, "Mode", _mode, null, null, "Leave empty for a DirectInput / fight-stick layout. Use xinput-native for an XInput pad.");
            AddLabeled(row++, "DeviceId", _deviceId, null, null, "0–15 is one pad or stick; 16 means all devices.");

            foreach (var (key, tip) in new[]
                     {
                         ("A", "Primary A buttons."),
                         ("B", "Primary B buttons."),
                         ("C", "Primary C buttons."),
                         ("D", "Primary D buttons."),
                         ("AB", "A+B combo button id(s). Written into both A and B lists."),
                         ("BC", "B+C combo button id(s)."),
                         ("AC", "A+C combo button id(s)."),
                         ("ABC", "A+B+C combo button id(s)."),
                         ("Start", "Start button id(s)."),
                         ("Coin", "Coin button id(s)."),
                         ("Card", "Card button id(s)."),
                         ("Test", "Test button id(s). Optional."),
                         ("Service", "Service button id(s). Optional."),
                         ("Kill", "Kill / exit button id(s). Optional."),
                     })
            {
                var box = new TextBox();
                _boxes[key] = box;
                var label = key;
                AddLabeled(row++, key, box, "Bind", (_, _) => _bind(this, box, label),
                    tip + " Type 1-based button numbers, or click Bind and press the stick.");
            }
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
            _name.Text = dev.Name;
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
        }

        private void Set(string key, string? value) => _boxes[key].Text = value ?? "";
        private string? Get(string key) => NullIfBlank(_boxes[key].Text);

        private void AddRow(int row, UIElement el, int span = 1)
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, row);
            Grid.SetColumnSpan(el, span);
            Root.Children.Add(el);
        }

        private void AddLabeled(int row, string label, UIElement field, string? button, RoutedEventHandler? click, string tip)
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lab = new Label { Content = label, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(lab, row);
            Root.Children.Add(lab);
            field.SetValue(ToolTipProperty, tip);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            Root.Children.Add(field);
            if (button is not null && click is not null)
            {
                var b = new Button { Content = button, ToolTip = tip, Margin = new Thickness(0, 2, 0, 2) };
                b.Click += click;
                Grid.SetRow(b, row);
                Grid.SetColumn(b, 2);
                Root.Children.Add(b);
            }
        }
    }
}
