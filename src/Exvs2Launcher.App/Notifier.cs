using System.Media;
using Exvs2Launcher.Core.Notifications;
using Exvs2Launcher.Core.Profiles;
using Forms = System.Windows.Forms;

namespace Exvs2Launcher.App;

/// <summary>Plays a system sound and/or shows a tray balloon ("toast"). Both are off unless the user turns them on.</summary>
public sealed class Notifier : IDisposable
{
    private Forms.NotifyIcon? _icon;

    public void Show(Notice n, LauncherSettings s)
    {
        if (s.NotifySound)
        {
            try { (n.Kind == "match" ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play(); } catch { }
        }
        if (s.NotifyToast)
        {
            try
            {
                _icon ??= new Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
                    Text = "EXVS2 Launcher",
                    Visible = true,
                };
                _icon.ShowBalloonTip(5000, n.Title, n.Text, Forms.ToolTipIcon.Info);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
