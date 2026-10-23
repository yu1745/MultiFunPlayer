using MultiFunPlayer.Common;
using Stylet;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal sealed class MotionFilterDialog : Screen
{
    public sealed record AxisPreview(string Name, PointCollection Original, PointCollection Filtered, string Summary);
    private readonly IDeviceAxisValueProvider _provider;
    private CancellationTokenSource _previewCancellation;
    private bool _loaded;
    private bool _closed;
    public bool Enabled { get; set; }
    public double ThresholdPercent { get; set; }
    public double SmoothingMilliseconds { get; set; }
    public double PreviewStart { get; set; }
    public double PreviewMaximum { get; private set; }
    public double PreviewSeconds { get; set; } = 10;
    public bool IsBusy { get; private set; }
    public bool CanApply => !IsBusy;
    public ObservableCollection<AxisPreview> Axes { get; } = [];
    public string Status { get; private set; } = "调整参数后自动预览，点击应用才会生效。";
    public string AppliedStatus { get; private set; }
    public string RangeLabel => $"预览 {PreviewStart:F1}–{PreviewStart + PreviewSeconds:F1} 秒";

    public MotionFilterDialog(IDeviceAxisValueProvider provider)
    {
        _provider = provider;
        var settings = provider.GetMotionFilterSettings();
        Enabled = settings.Enabled;
        ThresholdPercent = settings.SpeedThreshold * 100;
        SmoothingMilliseconds = settings.SmoothingMilliseconds;
        var position = provider.GetBufferedPlaybackSnapshot(false).Position;
        PreviewStart = double.IsFinite(position) ? Math.Max(0, position) : 0;
        AppliedStatus = settings.Enabled ? "当前已启用六轴滤波" : "当前未启用滤波";
    }

    protected override async void OnViewLoaded()
    {
        base.OnViewLoaded();
        if (_loaded) return;
        _loaded = true;
        await RefreshPreview().ConfigureAwait(true);
    }
    public void OnEnabledChanged() => QueuePreview();
    public void OnThresholdPercentChanged() => QueuePreview();
    public void OnSmoothingMillisecondsChanged() => QueuePreview();
    public void OnPreviewStartChanged() => QueuePreview();
    public void OnPreviewSecondsChanged() => QueuePreview();

    private async void QueuePreview()
    {
        if (_loaded && !_closed) await RefreshPreview().ConfigureAwait(true);
    }

    public async Task RefreshPreview()
    {
        _previewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        IsBusy = true;
        try
        {
            await Task.Delay(180, cancellation.Token).ConfigureAwait(true);
            var settings = ReadSettings();
            var snapshot = _provider.GetBufferedPlaybackSnapshot(false);
            var maximum = snapshot.Axes.Where(a => MotionScriptFilter.Axes.Contains(a.Name) && a.Keyframes.Count > 0)
                .Select(a => a.Keyframes[^1].Position + a.Offset).DefaultIfEmpty(0).Max();
            PreviewMaximum = Math.Max(0, maximum - PreviewSeconds);
            var start = Math.Clamp(PreviewStart, 0, PreviewMaximum);
            if (PreviewStart != start) { PreviewStart = start; return; }
            var seconds = PreviewSeconds;
            var previews = await Task.Run(() => MotionScriptFilter.Axes.Select(name =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var axis = snapshot.Axes.FirstOrDefault(a => a.Name == name);
                if (axis == null) return new AxisPreview(name, Frozen([]), Frozen([]), "未加载脚本或已旁路");
                var rate = snapshot.Speed > 0 && double.IsFinite(snapshot.Speed) ? snapshot.Speed : 1;
                var output = MotionScriptFilter.Apply(axis.Keyframes, settings, rate, axis.Scale);
                var before = Points(axis.Keyframes, axis, start, seconds);
                var after = Points(output, axis, start, seconds);
                var removed = axis.Keyframes.Zip(output).Select(p => Math.Abs(p.First.Value - p.Second.Value) * Math.Abs(axis.Scale)).DefaultIfEmpty(0).Max();
                return new AxisPreview(name, before, after, $"{axis.Keyframes.Count:N0} 点 · 最大调整 {removed * 100:F1}% 行程");
            }).ToArray(), cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            Axes.Clear();
            foreach (var i in new[] { 0, 3, 1, 4, 2, 5 }) Axes.Add(previews[i]);
            Status = snapshot.Axes.Count == 0 ? "请先加载脚本。参数仍可应用并保存，加载脚本后会自动滤波。"
                : "预览使用实际滤波算法。原文件和时间轴不变；速度阈值用于触发平滑，并非硬性速度上限。";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = e.Message; }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) { _previewCancellation = null; IsBusy = false; }
        }
    }

    public void Apply()
    {
        try
        {
            _provider.ApplyMotionFilterSettings(ReadSettings());
            AppliedStatus = Enabled ? $"已应用六轴滤波：{ThresholdPercent:G}% 行程/秒 · {SmoothingMilliseconds:G} ms"
                : "已关闭六轴滤波，恢复原脚本输出";
            Status = "参数已应用，将随 MFP 配置保存。视频保持暂停，手动播放后使用新设置。";
        }
        catch (Exception e) { Status = e.Message; }
    }

    private MotionFilterSettings ReadSettings()
    {
        var settings = new MotionFilterSettings(Enabled, ThresholdPercent / 100, SmoothingMilliseconds);
        if (!settings.IsValid) throw new ArgumentException("速度阈值范围 1–100000% 行程/秒，平滑时间范围 5–1000 ms。");
        if (!double.IsFinite(PreviewSeconds) || PreviewSeconds is < 1 or > 60 || !double.IsFinite(PreviewStart))
            throw new ArgumentException("预览时长范围 1–60 秒。");
        return settings;
    }

    private static PointCollection Points(IReadOnlyList<Keyframe> keys, BufferedScriptAxis axis, double start, double seconds)
    {
        var from = start - axis.Offset;
        var to = from + seconds;
        var visible = keys.Where(p => p.Position > from && p.Position < to).ToList();
        visible.Insert(0, new(from, Interpolate(keys, from)));
        visible.Add(new(to, Interpolate(keys, to)));
        // Keep each bucket's extrema: dense vibration must not disappear merely
        // because its keyframe happens to fall between display pixels.
        var points = new List<Keyframe>();
        var bucketSize = Math.Max(1, (int)Math.Ceiling(visible.Count / 600d));
        for (var i = 0; i < visible.Count; i += bucketSize)
        {
            var bucket = visible.Skip(i).Take(bucketSize).ToArray();
            points.AddRange(new[] { bucket[0], bucket.MinBy(p => p.Value), bucket.MaxBy(p => p.Value), bucket[^1] }
                .Distinct().OrderBy(p => p.Position));
        }
        return Frozen(points.Select(p => new Point((p.Position - from) / seconds * 600,
            (1 - Map(p.Value)) * 64)));
        double Map(double value)
        {
            value = Math.Clamp(axis.DefaultValue + (value - axis.DefaultValue) * axis.Scale, 0, 1);
            return axis.Invert ? 1 - value : value;
        }
    }

    private static double Interpolate(IReadOnlyList<Keyframe> keys, double time)
    {
        if (keys.Count == 0) return .5;
        var lo = 0; var hi = keys.Count - 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (keys[mid].Position <= time) lo = mid; else hi = mid - 1; }
        if (time <= keys[0].Position) return keys[0].Value;
        if (lo + 1 >= keys.Count) return keys[^1].Value;
        var a = keys[lo]; var b = keys[lo + 1];
        return a.Value + (b.Value - a.Value) * (time - a.Position) / (b.Position - a.Position);
    }
    private static PointCollection Frozen(IEnumerable<Point> points) { var result = new PointCollection(points); result.Freeze(); return result; }
    public void Dismiss() { _closed = true; _previewCancellation?.Cancel(); DialogHelper.CloseByModel(this); }
}
