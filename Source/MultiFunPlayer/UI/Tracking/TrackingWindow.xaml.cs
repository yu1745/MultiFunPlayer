using MultiFunPlayer.OutputTarget.Tracking;
using System.Windows;
using System.Windows.Threading;

namespace MultiFunPlayer.UI.Tracking;

internal partial class TrackingWindow : Window
{
    private readonly TrackingWindowModel model;
    private readonly DispatcherTimer refresh;
    public TrackingWindow(TrackingMonitor monitor)
    {
        InitializeComponent();
        model = new(monitor); DataContext = model;
        refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => model.UpdateSnapshot(), Dispatcher);
        Loaded += (_, _) => { model.UpdateSnapshot(); refresh.Start(); };
        Closed += (_, _) => refresh.Stop();
    }
    private void ResetClick(object sender, RoutedEventArgs e) => model.Reset();
}
