namespace MultiFunPlayer.OutputTarget.Tracking;

internal readonly record struct TrackingPoint(double Time, double Target, double Actual);
internal sealed record TrackingMetric(double? Value, string Reason = null)
{
    public static TrackingMetric Unavailable(string reason) => new(null, reason);
}
internal sealed record AxisTrackingStatistics(
    TrackingMetric Error, TrackingMetric Rms, TrackingMetric P95,
    TrackingMetric DelayMs, TrackingMetric AmplitudePercent, TrackingMetric Overshoot,
    TrackingMetric PhaseDegrees, TrackingMetric FrequencyHz, double? Correlation,
    int Samples, double TimingResolutionMs)
{
    public static AxisTrackingStatistics Unavailable(string reason) => new(
        TrackingMetric.Unavailable(reason), TrackingMetric.Unavailable(reason), TrackingMetric.Unavailable(reason),
        TrackingMetric.Unavailable(reason), TrackingMetric.Unavailable(reason), TrackingMetric.Unavailable(reason),
        TrackingMetric.Unavailable(reason), TrackingMetric.Unavailable(reason), null, 0, 0);
}

internal static class TrackingStatistics
{
    internal readonly record struct Turn(double Time, double Value, int Direction);
    public static AxisTrackingStatistics Analyze(IReadOnlyList<TrackingPoint> input, int axis, double windowSeconds, double endTime)
    {
        var points = input.Where(p => p.Time >= endTime - windowSeconds && p.Time <= endTime).ToArray();
        var valid = points.Where(p => double.IsFinite(p.Target) && double.IsFinite(p.Actual)).ToArray();
        if (valid.Length == 0) return AxisTrackingStatistics.Unavailable("没有时间对齐的有效反馈");
        var errors = valid.Select(p => p.Actual - p.Target).ToArray();
        var rms = Math.Sqrt(errors.Select(x => x * x).Average());
        var p95 = Quantile(errors.Select(Math.Abs).ToArray(), .95);
        var resolution = valid.Zip(valid.Skip(1), (a, b) => b.Time - a.Time).Where(x => x > .0001 && x < .2).ToArray();
        var dt = resolution.Length == 0 ? .02 : Quantile(resolution, .5);
        var minimumSpan = axis < 3 ? .5 : .3;
        var referenceSpan = Quantile(valid.Select(p => p.Target).ToArray(), .95) - Quantile(valid.Select(p => p.Target).ToArray(), .05);
        var noMotion = referenceSpan < minimumSpan;
        var reason = noMotion ? "动作幅度太小或静止" : "需要至少 2 秒连续运动数据";
        var delay = TrackingMetric.Unavailable(reason);
        var amplitude = TrackingMetric.Unavailable(reason);
        var phase = TrackingMetric.Unavailable(reason);
        var frequency = TrackingMetric.Unavailable(reason);
        var overshoot = TrackingMetric.Unavailable("没有完整的转折或到位保持");
        double? correlation = null;
        if (!noMotion && valid.Length >= 40 && valid[^1].Time - valid[0].Time >= 2)
        {
            const double maxLag = .25;
            var step = Math.Max(.005, dt);
            var candidates = new List<(double Lag, double Correlation)>();
            var from = valid[0].Time + maxLag; var to = valid[^1].Time - maxLag;
            var actual = valid.Where(p => p.Time >= from && p.Time <= to).ToArray();
            for (var lag = -maxLag; lag <= maxLag + step * .01; lag += step)
            {
                var pairs = Pairs(points, actual, lag);
                candidates.Add((lag, pairs.Length < Math.Max(30, actual.Length * .8) ? double.NaN : Regression(pairs).Correlation));
            }
            var finite = candidates.Select((p, i) => {
                var lag = p.Lag; var r = p.Correlation;
                if (i > 0 && i + 1 < candidates.Count)
                {
                    var left = candidates[i - 1].Correlation; var right = candidates[i + 1].Correlation;
                    var curvature = left - 2 * r + right;
                    if (double.IsFinite(curvature) && r >= left && r >= right && curvature < -1e-8)
                    {
                        var delta = Math.Clamp(.5 * (left - right) / curvature, -.5, .5);
                        lag += delta * step; r = Math.Min(1, r - .25 * (left - right) * delta);
                    }
                }
                return (Lag: lag, Correlation: r, Index: i);
            }).Where(p => double.IsFinite(p.Correlation)).ToArray();
            if (finite.Length > 2)
            {
                var best = finite.MaxBy(p => p.Correlation);
                correlation = best.Correlation;
                var competitors = finite.Where(p => Math.Abs(p.Lag - best.Lag) >= Math.Max(.08, step * 3)).ToArray();
                var ambiguous = competitors.Any(p => p.Correlation >= best.Correlation - .005);
                if (best.Correlation < .7) reason = "目标与反馈相关性不足";
                else if (best.Index == 0 || best.Index == candidates.Count - 1) reason = "延迟超出 ±250 ms 搜索范围";
                else if (ambiguous) reason = "延迟不唯一：周期混叠或近似匀速运动";
                else
                {
                    var lag = best.Lag;
                    delay = new(lag * 1000, "正值为反馈滞后；源端接收时间对齐，保留传感器／CAN 时间不确定性");
                    var fit = Regression(Pairs(points, actual, lag));
                    amplitude = fit.Gain > 0 && double.IsFinite(fit.Gain)
                        ? new(fit.Gain * 100, "补偿估计延迟后的线性幅度增益；不是整体准确度评分")
                        : TrackingMetric.Unavailable("幅度拟合不可靠");
                }
            }
            else reason = "连续采样不足，无法估计延迟";
            if (delay.Value == null) { delay = TrackingMetric.Unavailable(reason); amplitude = TrackingMetric.Unavailable(reason); }

            var turns = TurningPoints(valid, Math.Max(minimumSpan * .5, referenceSpan * .1));
            var maxima = turns.Where(t => t.Direction > 0).ToArray();
            var minima = turns.Where(t => t.Direction < 0).ToArray();
            var sameDirection = maxima.Length >= minima.Length ? maxima : minima;
            if (sameDirection.Length >= 3)
            {
                var periods = sameDirection.Zip(sameDirection.Skip(1), (a, b) => b.Time - a.Time).ToArray();
                var period = periods.Average();
                var variation = Math.Sqrt(periods.Select(t => (t - period) * (t - period)).Average()) / period;
                if (period >= 4 * dt && variation <= .15)
                {
                    frequency = new(1 / period, "至少两个完整周期，周期变异系数不超过 15%");
                    var harmonic = HarmonicFit(valid, frequency.Value.Value);
                    phase = harmonic.Valid ? new(harmonic.Phase, "主频相位差，折算到 ±180°；不能据此确定整周期延迟") :
                        TrackingMetric.Unavailable("主频拟合不足以代表当前波形");
                    if (harmonic.Valid && !amplitude.Value.HasValue)
                        amplitude = new(harmonic.Gain * 100, "稳定周期的基波幅度比；绝对延迟不唯一");
                }
                else { frequency = TrackingMetric.Unavailable("周期变化过大或采样不足"); phase = TrackingMetric.Unavailable(frequency.Reason); }
            }
            else { frequency = TrackingMetric.Unavailable("需要至少两个完整、稳定周期"); phase = TrackingMetric.Unavailable(frequency.Reason); }

            // Compare matching local extrema after lag compensation, rather
            // than treating the instantaneous error of any moving ramp as overshoot.
            var turnLag = delay.Value.HasValue ? delay.Value.Value / 1000 :
                phase.Value.HasValue && frequency.Value.HasValue ? phase.Value.Value / (360 * frequency.Value.Value) : (double?)null;
            if (turnLag.HasValue)
                for (var i = 1; i + 1 < turns.Length; i++)
                {
                    var turn = turns[i];
                    var half = Math.Min(.25, .45 * Math.Min(turn.Time - turns[i - 1].Time, turns[i + 1].Time - turn.Time));
                    var center = turn.Time + turnLag.Value;
                    if (half < 2 * dt || center - half < valid[0].Time || center + half > valid[^1].Time) continue;
                    var around = valid.Where(p => Math.Abs(p.Time - center) <= half).ToArray();
                    if (around.Length < 3) continue;
                    if (around.Zip(around.Skip(1), (a, b) => b.Time - a.Time).Any(gap => gap > Math.Max(.06, dt * 3))) continue;
                    if (around[0].Time > center - half + 1.5 * dt || around[^1].Time < center + half - 1.5 * dt) continue;
                    var extremum = turn.Direction > 0 ? around.Max(p => p.Actual) : around.Min(p => p.Actual);
                    overshoot = new(Math.Max(0, turn.Direction * (extremum - turn.Value)),
                        delay.Value.HasValue ? "最近一个完整转折处的超调，已对齐估计延迟" : "按主周期相位匹配的转折超调；整周期延迟不唯一");
                }
        }
        var holdOvershoot = HoldOvershoot(valid, minimumSpan);
        if (holdOvershoot.Value.HasValue) overshoot = holdOvershoot;
        return new(new(errors[^1]), new(rms), new(p95), delay, amplitude, overshoot,
            phase, frequency, correlation, valid.Length, Math.Max(1, dt * 1000));
    }

    private static (bool Valid, double Gain, double Phase) HarmonicFit(TrackingPoint[] points, double frequency)
    {
        // Fit constant + sin + cos, rather than projecting an incomplete window
        // as if it contained an integer number of cycles. Phase remains useful
        // modulo one cycle even when absolute cross-correlation lag is ambiguous.
        var x = Fit(p => p.Target); var y = Fit(p => p.Actual);
        if (x.R2 < .7 || y.R2 < .5 || x.Amplitude < 1e-8) return (false, 0, 0);
        var phase = x.Phase - y.Phase;
        return (true, y.Amplitude / x.Amplitude, Math.Atan2(Math.Sin(phase), Math.Cos(phase)) * 180 / Math.PI);

        (double Amplitude, double Phase, double R2) Fit(Func<TrackingPoint, double> value)
        {
            var a = new double[3, 4]; var origin = points[0].Time;
            foreach (var p in points)
            {
                var angle = 2 * Math.PI * frequency * (p.Time - origin);
                double[] row = [1, Math.Sin(angle), Math.Cos(angle)];
                for (var i = 0; i < 3; i++) { for (var j = 0; j < 3; j++) a[i, j] += row[i] * row[j]; a[i, 3] += row[i] * value(p); }
            }
            for (var c = 0; c < 3; c++)
            {
                var pivot = c; for (var i = c + 1; i < 3; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[pivot, c])) pivot = i;
                if (Math.Abs(a[pivot, c]) < 1e-12) return (0, 0, -1);
                for (var j = c; j < 4; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
                var divisor = a[c, c]; for (var j = c; j < 4; j++) a[c, j] /= divisor;
                for (var i = 0; i < 3; i++) if (i != c) { var f = a[i, c]; for (var j = c; j < 4; j++) a[i, j] -= f * a[c, j]; }
            }
            var mean = points.Average(value); var total = 0d; var residual = 0d;
            foreach (var p in points)
            {
                var angle = 2 * Math.PI * frequency * (p.Time - origin);
                var v = value(p); var predicted = a[0, 3] + a[1, 3] * Math.Sin(angle) + a[2, 3] * Math.Cos(angle);
                total += (v - mean) * (v - mean); residual += (v - predicted) * (v - predicted);
            }
            return (Math.Sqrt(a[1, 3] * a[1, 3] + a[2, 3] * a[2, 3]), Math.Atan2(a[2, 3], a[1, 3]), total < 1e-12 ? -1 : 1 - residual / total);
        }
    }

    private static TrackingMetric HoldOvershoot(TrackingPoint[] points, double minimumSpan)
    {
        var none = TrackingMetric.Unavailable("没有可识别的到位保持");
        if (points.Length < 10) return none;
        var final = points[^1].Target;
        var tolerance = minimumSpan * .02;
        var first = points.Length - 1;
        while (first > 0 && Math.Abs(points[first - 1].Target - final) <= tolerance) first--;
        if (first == 0 || points[^1].Time - points[first].Time < .2) return none;
        var before = first - 1;
        while (before > 0 && Math.Abs(points[before].Target - final) < minimumSpan) before--;
        var move = final - points[before].Target;
        if (Math.Abs(move) < minimumSpan || points[first].Time - points[before].Time > 2) return none;
        var observations = points.Where(p => p.Time >= points[first].Time && p.Time <= points[first].Time + .5).ToArray();
        if (observations.Length < 5) return none;
        if (observations.Zip(observations.Skip(1), (a, b) => b.Time - a.Time).Any(gap => gap > .1)) return none;
        var extreme = move > 0 ? observations.Max(p => p.Actual) : observations.Min(p => p.Actual);
        return new(Math.Max(0, Math.Sign(move) * (extreme - final)), "最近一次到位后前 0.5 秒内的超调；至少观察 0.2 秒保持");
    }

    internal static double Quantile(double[] values, double q)
    {
        if (values.Length == 0) return double.NaN;
        Array.Sort(values); var position = Math.Clamp(q, 0, 1) * (values.Length - 1);
        var lo = (int)position; var hi = Math.Min(lo + 1, values.Length - 1);
        return values[lo] + (values[hi] - values[lo]) * (position - lo);
    }

    private static (double Target, double Actual)[] Pairs(TrackingPoint[] points, TrackingPoint[] actual, double lag)
    {
        var pairs = new List<(double, double)>();
        foreach (var p in actual) if (Interpolate(points, p.Time - lag, out var target)) pairs.Add((target, p.Actual));
        return pairs.ToArray();
    }
    private static bool Interpolate(TrackingPoint[] points, double time, out double value)
    {
        value = double.NaN;
        var lo = 0; var hi = points.Length - 1;
        if (hi < 0 || time < points[0].Time || time > points[hi].Time) return false;
        while (lo + 1 < hi) { var mid = (lo + hi) / 2; if (points[mid].Time <= time) lo = mid; else hi = mid; }
        var a = points[lo]; var b = points[hi];
        if (!double.IsFinite(a.Target) || !double.IsFinite(b.Target) || b.Time - a.Time > .1) return false;
        value = a.Target + (b.Target - a.Target) * (hi == lo ? 0 : (time - a.Time) / (b.Time - a.Time));
        return double.IsFinite(value);
    }
    private static (double Gain, double Correlation) Regression((double Target, double Actual)[] pairs)
    {
        if (pairs.Length < 3) return (double.NaN, double.NaN);
        var x = pairs.Average(p => p.Target); var y = pairs.Average(p => p.Actual);
        var xx = 0d; var yy = 0d; var xy = 0d;
        foreach (var p in pairs) { var a = p.Target - x; var b = p.Actual - y; xx += a * a; yy += b * b; xy += a * b; }
        return xx < 1e-12 || yy < 1e-12 ? (double.NaN, double.NaN) : (xy / xx, xy / Math.Sqrt(xx * yy));
    }
    internal static Turn[] TurningPoints(TrackingPoint[] points, double prominence)
    {
        if (points.Length < 3) return [];
        var turns = new List<Turn>(); var extreme = points[0]; var direction = 0;
        foreach (var p in points.Skip(1))
        {
            if (direction == 0)
            {
                if (Math.Abs(p.Target - extreme.Target) >= prominence) { direction = p.Target > extreme.Target ? 1 : -1; extreme = p; }
                continue;
            }
            if (direction * (p.Target - extreme.Target) >= 0) extreme = p;
            else if (direction * (extreme.Target - p.Target) >= prominence)
            { turns.Add(new(extreme.Time, extreme.Target, direction)); direction = -direction; extreme = p; }
        }
        return turns.ToArray();
    }
}
