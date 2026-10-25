using System.Windows;

namespace MultiFunPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
internal sealed partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        MultiFunPlayer.OutputTarget.Tracking.TestRecording.StopAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
