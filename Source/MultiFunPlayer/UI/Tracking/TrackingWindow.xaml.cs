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
    private void LoadBaselineClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确认仅有完整机构和接收器、没有臀模或配重，并保持静止？本操作只记录数据，不驱动电机。", "采集空载基线", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            model.CaptureBaseline();
    }
    private void ResetClick(object sender, RoutedEventArgs e) => model.Reset();
}
