using System.ComponentModel;
using System.Windows.Media;
using Exvs2Launcher.Core.Launch;
using Exvs2Launcher.Core.Profiles;

namespace Exvs2Launcher.App;

/// <summary>One line of the All cabs list: include toggle and three status lights.</summary>
public sealed class CabRow : INotifyPropertyChanged
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush Grey = Brushes.Gray;

    private readonly Dictionary<string, CabLight> _lights = new()
    {
        [ComponentRoles.CardServer] = CabLight.Off, [ComponentRoles.Lm] = CabLight.Off, [ComponentRoles.Client] = CabLight.Off,
    };

    public CabRow(LauncherProfile p) => Profile = p;

    public LauncherProfile Profile { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Include
    {
        get => Profile.IncludeInStartEverything;
        set { Profile.IncludeInStartEverything = value; Changed(nameof(Include)); IncludeChanged?.Invoke(); }
    }

    public event Action? IncludeChanged;

    public string Name => Profile.Name;
    public string Folder => Profile.GameFolder;
    public string Window => Profile.LastGameWindowTitle ?? "";

    public CabLight Light(string role) => _lights[role];

    public void SetLight(string role, CabLight light)
    {
        if (_lights[role] == light) return;
        _lights[role] = light;
        var prefix = role == ComponentRoles.CardServer ? "Server" : role == ComponentRoles.Lm ? "Lm" : "Client";
        Changed(prefix + "Text");
        Changed(prefix + "Brush");
    }

    public string ServerText => Text(ComponentRoles.CardServer);
    public string LmText => Text(ComponentRoles.Lm);
    public string ClientText => Text(ComponentRoles.Client);
    public Brush ServerBrush => BrushFor(_lights[ComponentRoles.CardServer]);
    public Brush LmBrush => BrushFor(_lights[ComponentRoles.Lm]);
    public Brush ClientBrush => BrushFor(_lights[ComponentRoles.Client]);

    public void RefreshText() { Changed(nameof(Name)); Changed(nameof(Folder)); Changed(nameof(Window)); Changed(nameof(Include)); }

    private bool Wanted(string role) => role switch
    {
        ComponentRoles.CardServer => Profile.StartCardServer,
        ComponentRoles.Lm => Profile.StartLm,
        _ => Profile.StartClient,
    };

    private string Text(string role) => _lights[role] switch
    {
        CabLight.Running => "● Running",
        CabLight.Starting => "● Starting",
        CabLight.Waiting => "● Waiting",
        CabLight.Failed => "● Failed",
        CabLight.Skipped => "● Skipped",
        _ => Wanted(role) ? "○ Off" : "– not used",
    };

    private static Brush BrushFor(CabLight l) => l switch
    {
        CabLight.Running => Green,
        CabLight.Starting or CabLight.Waiting => Amber,
        CabLight.Failed or CabLight.Skipped => Red,
        _ => Grey,
    };

    private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
