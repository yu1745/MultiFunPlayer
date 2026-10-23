using MultiFunPlayer.OutputTarget.Tracking;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MultiFunPlayer.UI.Tracking;

internal sealed class TrackingChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points),
        typeof(IReadOnlyList<TrackingPoint>), typeof(TrackingChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ErrorOnlyProperty = DependencyProperty.Register(nameof(ErrorOnly),
        typeof(bool), typeof(TrackingChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit),
        typeof(string), typeof(TrackingChart), new FrameworkPropertyMetadata("mm", FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<TrackingPoint> Points { get => (IReadOnlyList<TrackingPoint>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public bool ErrorOnly { get => (bool)GetValue(ErrorOnlyProperty); set => SetValue(ErrorOnlyProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    private static readonly Brush BackgroundBrush = Frozen("#0B1220"), GridBrush = Frozen("#243248"), LabelBrush = Frozen("#94A3B8");
    private static readonly Brush TargetBrush = Frozen("#CBD5E1"), ActualBrush = Frozen("#22D3EE"), ErrorBrush = Frozen("#FBBF24");
    private static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color); brush.Freeze(); return brush; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        var plot = new Rect(64, 12, Math.Max(1, ActualWidth - 80), Math.Max(1, ActualHeight - 38));
        var points = Points ?? [];
        var finite = points.Where(p => double.IsFinite(p.Target) && double.IsFinite(p.Actual)).ToArray();
        if (finite.Length == 0) { Text(dc, "等待有效数据", new(76, ActualHeight / 2 - 8)); return; }
        var values = ErrorOnly ? finite.Select(p => p.Actual - p.Target).ToArray() : finite.SelectMany(p => new[] { p.Target, p.Actual }).ToArray();
        var min = values.Min(); var max = values.Max();
        if (ErrorOnly) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
        var padding = Math.Max(Unit == "mm" ? .1 : .05, (max - min) * .08);
        min -= padding; max += padding;
        var first = points[0].Time; var last = points[^1].Time;
        if (last - first < .1) first = last - 1;
        double X(double t) => plot.Left + (t - first) / (last - first) * plot.Width;
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(new Pen(GridBrush, 1), new(plot.Left, y), new(plot.Right, y));
            var value = max - (max - min) * i / 4;
            Text(dc, value.ToString("0.##", CultureInfo.CurrentCulture), new(6, y - 7));
            var x = plot.Left + plot.Width * i / 4;
            Text(dc, ((first - last) * (1 - i / 4d)).ToString("0.0", CultureInfo.CurrentCulture) + "s", new(x - 12, plot.Bottom + 5));
        }
        Text(dc, Unit, new(6, ActualHeight - 21));
        if (ErrorOnly) Draw(p => p.Actual - p.Target, ErrorBrush, 1.5);
        else { Draw(p => p.Target, TargetBrush, 1.4); Draw(p => p.Actual, ActualBrush, 1.8); }
        void Draw(Func<TrackingPoint, double> get, Brush brush, double thickness)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var started = false; var previousTime = double.NaN;
                foreach (var p in points)
                {
                    var v = get(p);
                    if (!double.IsFinite(v)) { started = false; continue; }
                    var point = new Point(X(p.Time), Y(v));
                    if (!started || p.Time - previousTime > .1) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                    started = true; previousTime = p.Time;
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, thickness), geometry);
        }
    }
    private void Text(DrawingContext dc, string text, Point point)
        => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}
