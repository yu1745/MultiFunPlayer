using MultiFunPlayer.Common;
using Stylet;
using System.Windows.Threading;
using MultiFunPlayer.OutputTarget.Tracking;
using MultiFunPlayer.UI.Controls.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal sealed class MotionFilterDialog : Screen
{
    public sealed record PlotLabel(double X, double Y, string Label, string Detail, double AnchorX);
    public sealed record AxisPreview(string Name, PointCollection Original, PointCollection Filtered, string Summary,
        PlotLabel[] Labels, double LabelHeight, double CursorX) { public double PlotHeight => 64 + LabelHeight; }
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _following;
    private bool _updatingPlayback;
    private CancellationTokenSource _seekCancellation;
    private DateTime _seekHoldUntil;
    public event Action<double> SeekRequested;
    public double PlaybackPosition { get; set; }
    public double PlaybackMaximum { get; private set; }
    public async void OnPlaybackPositionChanged()
    {
        if (_updatingPlayback || !_loaded || _closed || !double.IsFinite(PlaybackPosition)) return;
        _seekCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _seekCancellation = cancellation;
        _seekHoldUntil = DateTime.UtcNow.AddSeconds(2);
        try
        {
            await Task.Delay(250, cancellation.Token);
            if (_closed) return;
            var position = Math.Clamp(PlaybackPosition, 0, PlaybackMaximum);
            _following = true;
            PreviewStart = Math.Clamp(position - PreviewSeconds * .65, 0, PreviewMaximum);
            _following = false;
            SeekRequested?.Invoke(position);
            QueuePreview();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_seekCancellation, cancellation)) _seekCancellation = null; }
    }
    private bool _restoring;
    private CancellationTokenSource _autoApply;
    private bool Playing => (_provider as ScriptViewModel)?.IsPlaying ?? false;
    private async void ScheduleApply(bool includeDisabled=false)
    {
        if(!_loaded || _closed || _restoring || (!Enabled && !includeDisabled)) return;
        _autoApply?.Cancel();
        using var c=new CancellationTokenSource();_autoApply=c;
        try {
            await Task.Delay(400,c.Token);
            if(_closed)return;
            if(ReadSettings()!=_provider.GetMotionFilterSettings()) { FilterUsageLog.Write("ui_adjustment",new{settings=ReadSettings(),video=Video,position=_provider.GetBufferedPlaybackSnapshot(false).Position}); Apply(); }
        } catch(OperationCanceledException){} catch(Exception ex){Status=ex.Message;}
        finally{if(ReferenceEquals(_autoApply,c))_autoApply=null;}
    }
    private void TuningChanged(AxisTuning item,string property)
    {
        if(_restoring)return;
        if(property==nameof(AxisTuning.Enabled) && Playing) {
            _restoring=true;item.Enabled=_provider.GetMotionFilterSettings().ForAxis(item.Name).Enabled;_restoring=false;
            Status="各轴开关需要先暂停；强度可以在Buffered播放中调整。";return;
        }
        QueuePreview();ScheduleApply();
    }
    public bool FollowPlayback { get; set; } = true;
    public bool ShowAnnotations { get; set; } = true;
    public string AnnotationStatus { get; private set; } = "正在读取历史标注…";
    private readonly Dictionary<string,(IReadOnlyList<Keyframe> Source, MotionFilterSettings Settings, double Rate, double Scale, IReadOnlyList<Keyframe> Result)> _cache = new();
    private readonly object _cacheGate = new();
    public void OnShowAnnotationsChanged() => QueuePreview();
    private string Video => (_provider as ScriptViewModel)?.MediaResource?.Path;
    private async void Tick(object sender, EventArgs e)
    {
        if (_closed || IsBusy) return;
        NotifyOfPropertyChange(nameof(CanApply));
        if(!Playing && FilterRuntime.Instance.Text.Contains("需暂停"))Apply();
        var actualPosition = _provider.GetBufferedPlaybackSnapshot(false).Position;
        if (DateTime.UtcNow >= _seekHoldUntil && double.IsFinite(actualPosition))
        {
            _updatingPlayback = true;
            PlaybackPosition = actualPosition;
            _updatingPlayback = false;
        }
        if(FollowPlayback && DateTime.UtcNow >= _seekHoldUntil) {
            var position = _provider.GetBufferedPlaybackSnapshot(false).Position;
            if(double.IsFinite(position)) {
                _following=true;
                PreviewStart=Math.Clamp(position-PreviewSeconds*.65,0,PreviewMaximum);
                _following=false;
            }
        }
        await RefreshPreview();
    }
    private void StopPreview() { _closed=true; _timer.Stop(); _timer.Tick-=Tick; _previewCancellation?.Cancel(); _autoApply?.Cancel(); _seekCancellation?.Cancel(); }
    protected override void OnClose() { StopPreview(); base.OnClose(); }
    protected override void OnDeactivate() { StopPreview(); base.OnDeactivate(); }
    private IReadOnlyList<Keyframe> Filter(BufferedScriptAxis axis, MotionFilterSettings settings,double rate)
    {
        lock(_cacheGate) {
            if(_cache.TryGetValue(axis.Name,out var c) && ReferenceEquals(c.Source,axis.Keyframes)
                && c.Settings==settings && c.Rate==rate && c.Scale==axis.Scale) return c.Result;
            var result=MotionScriptFilter.Apply(axis.Keyframes,settings,rate,axis.Scale,axis.Name);
            _cache[axis.Name]=(axis.Keyframes,settings,rate,axis.Scale,result); return result;
        }
    }
    private static (PlotLabel[],double) Labels(AnnotationStore.Entry[] marks,double start,double seconds)
    {
        var ends=new List<double>();var result=new List<PlotLabel>();
        foreach(var m in marks.Where(m=>m.Seconds>=start && m.Seconds<=start+seconds)) {
            var x=(m.Seconds-start)/seconds*600;
            var width=Math.Min(590,Math.Max(70,m.Label.Length*24d));
            var left=Math.Clamp(x,0,600-width);var row=ends.FindIndex(end=>end+8<=left);
            if(row<0){row=ends.Count;ends.Add(0);}ends[row]=left+width;
            result.Add(new(left,row*32,m.Label,$"{m.Seconds:F3}s · {m.Note}",x));
        }
        return (result.ToArray(),ends.Count*32);
    }
    private readonly IDeviceAxisValueProvider _provider;
    private CancellationTokenSource _previewCancellation;
    private bool _loaded;
    private bool _closed;
    public bool Enabled { get; set; }
    public sealed class AxisTuning : PropertyChangedBase
    {
        public string Name { get; }
        public bool Enabled { get; set; }
        public double Strength { get; set; }
        public AxisTuning(string name, AxisMotionFilter tuning) { Name = name; Enabled = tuning.Enabled; Strength = tuning.Strength; }
    }
    public ObservableCollection<AxisTuning> Tuning { get; } = [];
    public double PreviewStart { get; set; }
    public double PreviewMaximum { get; private set; }
    public double PreviewSeconds { get; set; } = 10;
    public bool IsBusy { get; private set; }
    public bool CanApply => !IsBusy && !((_provider as ScriptViewModel)?.IsPlaying ?? false);
    public ObservableCollection<AxisPreview> Axes { get; } = [];
    public string Status { get; private set; } = "开启后强度自动提交；播放中按缓冲进度平滑衔接。";
    public string AppliedStatus { get; private set; }
    public string RangeLabel => $"预览 {PreviewStart:F1}–{PreviewStart + PreviewSeconds:F1} 秒";

    public MotionFilterDialog(IDeviceAxisValueProvider provider)
    {
        _provider = provider;
        var settings = provider.GetMotionFilterSettings();
        Enabled = settings.Enabled;
        foreach (var name in MotionScriptFilter.Axes)
        {
            var item = new AxisTuning(name, settings.ForAxis(name));
            item.PropertyChanged += (_, e) => TuningChanged(item,e.PropertyName);
            Tuning.Add(item);
        }
        var position = provider.GetBufferedPlaybackSnapshot(false).Position;
        PlaybackPosition = double.IsFinite(position) ? Math.Max(0, position) : 0;
        PreviewStart = double.IsFinite(position) ? Math.Max(0, position) : 0;
        AppliedStatus = settings.Enabled ? "当前已启用六轴滤波" : "当前未启用滤波";
    }

    protected override async void OnViewLoaded()
    {
        base.OnViewLoaded();
        if (_loaded) return;
        _loaded = true;
        _timer.Tick+=Tick; _timer.Start();
        _ = LoadAnnotations();
        await RefreshPreview().ConfigureAwait(true);
    }
    private async Task LoadAnnotations()
    {
        try {
            var failures=await Task.Run(()=>AnnotationOverlay.LoadHistory(@"D:\0SR-plus-work\实机测试"));
            if(!_closed) AnnotationStatus=failures==0 ? "标签按视频时间显示（非离线校时结果）" : $"{failures} 个记录无法读取，其余标注已加载";
        } catch(Exception ex) { if(!_closed) AnnotationStatus="标注读取失败："+ex.Message; }
    }
    public void OnEnabledChanged()
    {
        if(_restoring || !_loaded)return;
        if(Playing) {_restoring=true;Enabled=_provider.GetMotionFilterSettings().Enabled;_restoring=false;Status="开启或关闭滤波需要先暂停。";return;}
        QueuePreview();ScheduleApply(true);
    }
    public void OnPreviewStartChanged() { if(!_following) QueuePreview(); }
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
            await Task.Delay(20, cancellation.Token).ConfigureAwait(true);
            var settings = ReadSettings();
            var video = Video;
            var marks = ShowAnnotations ? AnnotationOverlay.ForVideo(video) : [];
            var snapshot = _provider.GetBufferedPlaybackSnapshot(false);
            var maximum = snapshot.Axes.Where(a => MotionScriptFilter.Axes.Contains(a.Name) && a.Keyframes.Count > 0)
                .Select(a => a.Keyframes[^1].Position + a.Offset).DefaultIfEmpty(0).Max();
            var duration = (_provider as ScriptViewModel)?.MediaDuration ?? 0;
            PlaybackMaximum = double.IsFinite(duration) && duration > 0 ? duration : Math.Max(0, maximum);
            PreviewMaximum = Math.Max(0, maximum - PreviewSeconds);
            var start = Math.Clamp(PreviewStart, 0, PreviewMaximum);
            if (PreviewStart != start) { _following=true; PreviewStart = start; _following=false; }
            var seconds = PreviewSeconds;
            var previews = await Task.Run(() => MotionScriptFilter.Axes.Select(name =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var axis = snapshot.Axes.FirstOrDefault(a => a.Name == name);
                if (axis == null) return new AxisPreview(name, Frozen([]), Frozen([]), "未加载脚本或已旁路", [], 0, 0);
                var rate = snapshot.Speed > 0 && double.IsFinite(snapshot.Speed) ? snapshot.Speed : 1;
                var output = Filter(axis, settings, rate);
                var before = Points(axis.Keyframes, axis, start, seconds);
                var after = Points(output, axis, start, seconds);
                var removed = axis.Keyframes.Zip(output).Select(p => Math.Abs(p.First.Value - p.Second.Value) * Math.Abs(axis.Scale)).DefaultIfEmpty(0).Max();
                var (labels,height)=Labels(marks,start,seconds);
                return new AxisPreview(name, before, after, $"{axis.Keyframes.Count:N0} 点 · 最大调整 {removed * 100:F1}% 行程",
                    labels,height,Math.Clamp((snapshot.Position-start)/seconds*600,0,600));
            }).ToArray(), cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            Axes.Clear();
            foreach (var i in new[] { 0, 3, 1, 4, 2, 5 }) Axes.Add(previews[i]);
            Status = snapshot.Axes.Count == 0 ? "请先加载脚本。参数仍可应用并保存，加载脚本后会自动滤波。"
                : "预览使用实际滤波算法。局部缩程，保留换向时刻；不改原文件。仅本地脚本，非实时游戏滤波；尚未经实机舒适性验证。";
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
            AppliedStatus = Enabled ? "已应用六轴独立自适应缩程（实验版）"
                : "已关闭六轴滤波，恢复原脚本输出";
            Status = "参数已提交。播放中需等待缓冲衔接，以主界面指示灯为准。";
        }
        catch (Exception e) { Status = e.Message; }
    }

    private MotionFilterSettings ReadSettings()
    {
        AxisMotionFilter Read(string name) { var t = Tuning.Single(a => a.Name == name); return new(t.Enabled, t.Strength); }
        var settings = new MotionFilterSettings(Enabled) {
            L0 = Read("L0"), L1 = Read("L1"), L2 = Read("L2"),
            R0 = Read("R0"), R1 = Read("R1"), R2 = Read("R2")
        };
        if (!settings.IsValid) throw new ArgumentException("各轴强度应在 0–100 内。");
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
    public event Action CloseRequested;
    public void Dismiss() { StopPreview(); if(CloseRequested != null) CloseRequested(); else DialogHelper.CloseByModel(this); }
}
