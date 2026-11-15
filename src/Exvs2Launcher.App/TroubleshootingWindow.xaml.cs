using System.Windows;
using Exvs2Launcher.Core.Wizard;

namespace Exvs2Launcher.App;

public partial class TroubleshootingWindow : Window
{
    public TroubleshootingWindow()
    {
        InitializeComponent();
        TopicList.ItemsSource = TroubleshootingTopics.All;
    }
}
