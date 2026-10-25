using MultiFunPlayer.OutputTarget.Tracking;
using Stylet;
using System.Globalization;

namespace MultiFunPlayer.UI.Tracking;

internal sealed class TrackingWindowModel(TrackingMonitor monitor) : PropertyChangedBase
{
    public string[] AxisNames { get; } = TrackingSnapshot.Names;
    public double[] Windows { get; } = [2, 4, 8, 12];
    public int SelectedAxis { get; set; }
    public double WindowSeconds { get; set; } = 4;
    public string State { get; private set; } = "等待数据";
    public string Quality { get; private set; }
    public string Health { get; private set; }
    public string MotorStatus { get; private set; }
    public string Timing { get; private set; }
    public string Unit { get; private set; } = "mm";
    public double ChartOpacity { get; private set; } = .4;
    public IReadOnlyList<TrackingPoint> Points { get; private set; } = Array.Empty<TrackingPoint>();
    public Card[] Cards { get; private set; } = [];
    public AxisRow[] Rows { get; private set; } = [];
    public MotorRow[] Motors { get; private set; } = [];
    public void OnSelectedAxisChanged() => UpdateSnapshot();
    public void OnWindowSecondsChanged() => monitor.SetWindow(WindowSeconds);
    public void Reset() => monitor.Reset();
    public void UpdateSnapshot()
    {
        var snapshot = monitor.Snapshot;
        var live = monitor.IsLive;
        var age = snapshot.Quality.LastPacketHostSeconds > 0 ? (TrackingMonitor.HostSeconds - snapshot.Quality.LastPacketHostSeconds) * 1000 : double.NaN;
        State = monitor.Availability ?? (snapshot.Quality.Received == 0 ? "等待新的跟随快照…" :
            live ? snapshot.State : $"数据过期或回传积压 · {Format(age, "0")} ms 未更新，指标暂停");
        ChartOpacity = live ? 1 : .4;
        var q = snapshot.Quality;
        var loss = q.Received + q.Missing == 0 ? double.NaN : 100d * q.Missing / (q.Received + q.Missing);
        Quality = $"有效帧 {q.Received}  ·  序号缺失 {q.Missing}（{Format(loss)}%）  ·  CRC／格式损坏 {q.Corrupt}  ·  重复／乱序 {q.Reordered}  ·  正解有效率 {Format(q.SolveValidPercent)}%  ·  闭合残差 {Format(q.MaxResidualMm, "0.000")} mm";
        Quality += $"\n实际回传 {Format(q.EffectiveHz, "0.0")} Hz  ·  采样覆盖 {Format(q.CoveragePercent, "0.0")}%  ·  传输／排队时延约 {Format(q.DeliveryAgeMs, "0")} ms（时钟同步不确定度 {Format(q.ClockUncertaintyMs, "0.0")} ms）";
        Health = $"实际 {Format(q.EffectiveHz, "0.0")} Hz  ·  反馈年龄 {Age(q.FeedbackAgeMs)}  ·  缺帧 {Format(loss, "0.0")}%  ·  正解有效 {Format(q.SolveValidPercent, "0.0")}%  ·  回传排队约 {Format(q.DeliveryAgeMs, "0")} ms";
        var selected = snapshot.Axes[Math.Clamp(SelectedAxis, 0, 5)];
        if (live && selected.InvalidReason != null) State = selected.Name + " 暂不可用：" + selected.InvalidReason;
        Unit = selected.Unit; Points = selected.Points;
        var s = live ? selected.Statistics : AxisTrackingStatistics.Unavailable(State);
        Timing = $"回传 {1000d / q.PeriodMs:0.#} Hz  ·  源端反馈年龄 {Age(q.FeedbackAgeMs)}  ·  统计窗口 {WindowSeconds:0} s  ·  延迟采样分辨率约 {Math.Max(q.PeriodMs, s.TimingResolutionMs):0.#} ms" +
            (q.WorkspaceReduced ? "  ·  工作空间裁剪中" : "");
        Cards = [
            Make("实时位置误差", s.Error, Unit, "反馈 − 同时刻目标"),
            Make("RMS 跟随误差", s.Rms, Unit, "保留真实滞后"),
            Make("跟随延迟", s.DelayMs, "ms", "正值为滞后"),
            Make("幅度保持率", s.AmplitudePercent, "%", "对齐延迟后的幅度比")
        ];
        Rows = snapshot.Axes.Select(a => {
            var stats = live ? a.Statistics : AxisTrackingStatistics.Unavailable(State);
            return new AxisRow(a.Name, a.Unit, Display(stats.Error), Display(stats.Rms), Display(stats.P95),
                Display(stats.DelayMs, "0"), Display(stats.AmplitudePercent, "0.0"), Display(stats.Overshoot),
                live ? a.InvalidReason ?? (stats.DelayMs.Value.HasValue ? "有效" : stats.DelayMs.Reason) : "数据过期");
        }).ToArray();
        var health = monitor.MotorHealth;
        MotorStatus = monitor.MotorAvailability ?? (health == null ? "等待电机状态" :
            $"状态回传 {1000 / health.Packet.PeriodMs} Hz · 总线忙跳过查询 {health.Packet.SkippedQueries} 次 · — 表示缺失或过期");
        var elapsed = health?.ElapsedMs ?? double.PositiveInfinity;
        Motors = snapshot.Motors.Select((m, i) => {
            var h = health?.Packet.Motors[i];
            string Value(int f, double scale = 1) => Format(h?.Value(f, elapsed) * scale, "0.0");
            var state = h == null || h.StateAgeMs + elapsed > 1000 ? "状态过期" :
                h.Error != 0 ? $"故障 0x{h.Error:X8}" : $"状态 {h.State}";
            return new MotorRow($"M{i+1}", live ? Format(m.CommandDegrees) : "—",
                live ? Format(m.ActualDegrees) : "—", live ? Format(m.ErrorDegrees) : "—", Age(m.FeedbackAgeMs),
                Value(0,180/Math.PI),Value(1),Value(2),Value(3),Value(4),state);
        }).ToArray();
    }
    private static Card Make(string label, TrackingMetric metric, string unit, string hint) => new(label,
        metric.Value.HasValue ? $"{(unit == "ms" ? "≈ " : "")}{Format(metric.Value, unit == "ms" ? "0" : unit == "%" ? "0.0" : "0.00")} {unit}" : "—", metric.Value.HasValue ? hint : metric.Reason, metric.Reason ?? hint);
    private static string Display(TrackingMetric m, string format = "0.00") => m.Value.HasValue ? Format(m.Value, format) : "—";
    private static string Age(ushort age) => age == ushort.MaxValue ? "不可用" : age + " ms";
    private static string Format(double? value, string format = "0.00") => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString(format, CultureInfo.CurrentCulture) : "—";
    internal sealed record Card(string Label, string Value, string Hint, string ToolTip);
    internal sealed record AxisRow(string Axis, string Unit, string Error, string Rms, string P95, string Delay, string Amplitude, string Overshoot, string State);
    internal sealed record MotorRow(string Motor, string Command, string Actual, string Error, string Age, string Speed, string Current, string FetTemperature, string MotorTemperature, string Voltage, string State);
}
