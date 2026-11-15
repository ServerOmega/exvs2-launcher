using System.Windows;
using System.Windows.Controls;

namespace Exvs2Launcher.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep tooltips on screen long enough to read, and wrap via the ToolTip style in App.xaml.
        // Per-monitor DPI comes from app.manifest (PerMonitorV2) and ApplicationHighDpiMode.
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(30_000));
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(700));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(200));
        base.OnStartup(e);
    }
}
