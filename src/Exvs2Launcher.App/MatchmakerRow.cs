using System.ComponentModel;
using Exvs2Launcher.Core.Matchmaking;

namespace Exvs2Launcher.App;

/// <summary>One dropdown entry: the saved server plus its latest ping / player count.</summary>
public sealed class MatchmakerRow : INotifyPropertyChanged
{
    public MatchmakerRow(MatchmakingServer server) => Server = server;

    public MatchmakingServer Server { get; }
    public ProbeResult? Probe { get; private set; }

    public string Display => Server.Name + "  —  " +
                             (Server.HasAddress ? Server.Address + "  ·  " + (Probe?.Summary ?? "checking...") : "no address yet (Edit list...)");

    public void SetProbe(ProbeResult r)
    {
        Probe = r;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
