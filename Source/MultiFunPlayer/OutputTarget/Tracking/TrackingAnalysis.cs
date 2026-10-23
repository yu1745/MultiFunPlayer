namespace MultiFunPlayer.OutputTarget.Tracking;

internal sealed record MotorTracking(double? CommandDegrees, double? ActualDegrees, double? ErrorDegrees, ushort FeedbackAgeMs);
internal sealed record TrackingAxis(string Name, string Unit, TrackingPoint[] Points, AxisTrackingStatistics Statistics, string InvalidReason);
internal sealed record TrackingQuality(long Received, long Missing, long Corrupt, long Reordered,
    double? SolveValidPercent, double? MaxResidualMm, ushort FeedbackAgeMs, ushort PeriodMs,
    bool WorkspaceReduced, double LastPacketHostSeconds, double WindowSeconds,
    double? DeliveryAgeMs = null, double? ClockUncertaintyMs = null, double? EffectiveHz = null, double? CoveragePercent = null);
internal sealed record TrackingSnapshot(TrackingAxis[] Axes, MotorTracking[] Motors, TrackingQuality Quality, string State)
{
    public static readonly string[] Names = ["L0", "L1", "L2", "R0", "R1", "R2"];
    public static TrackingSnapshot Empty(string reason = "等待跟随数据") => new(
        Names.Select((n, i) => new TrackingAxis(n, i < 3 ? "mm" : "°", [], AxisTrackingStatistics.Unavailable(reason), reason)).ToArray(),
        Enumerable.Range(0, 7).Select(_ => new MotorTracking(null, null, null, ushort.MaxValue)).ToArray(),
        new(0, 0, 0, 0, null, null, ushort.MaxValue, 20, false, 0, 4), reason);
}

// Single-owner analysis state. All timestamps used for tracking are P4 times;
// wall-clock reception is used only for stale-data warnings, never for lag.
internal sealed class TrackingAnalysis
{
    private sealed class History
    {
        private readonly List<(double Time, double Value)> points = [];
        public double LatestTime => points.Count == 0 ? double.NaN : points[^1].Time;
        public void Clear() => points.Clear();
        public void Add(double time, double value)
        {
            if (!double.IsFinite(time) || !double.IsFinite(value)) return;
            if (points.Count > 0 && time <= points[^1].Time) return;
            points.Add((time, value));
            if (points.Count > 1800) points.RemoveRange(0, points.Count - 1500);
        }
        public bool At(double time, out double value)
        {
            value = double.NaN;
            if (points.Count == 0 || time < points[0].Time - 1e-7 || time > points[^1].Time + 1e-7) return false;
            var lo = 0; var hi = points.Count - 1;
            while (lo + 1 < hi) { var mid = (lo + hi) / 2; if (points[mid].Time <= time) lo = mid; else hi = mid; }
            var a = points[lo]; var b = points[hi];
            if (Math.Abs(time - b.Time) < 1e-7) { value = b.Value; return true; }
            if (Math.Abs(time - a.Time) < 1e-7) { value = a.Value; return true; }
            if (b.Time - a.Time > .1) return false;
            value = a.Value + (b.Value - a.Value) * (b.Time > a.Time ? Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1) : 0);
            return true;
        }
    }

    private readonly History[] target = Enumerable.Range(0, 6).Select(_ => new History()).ToArray();
    private readonly History[] command = Enumerable.Range(0, 7).Select(_ => new History()).ToArray();
    private readonly History[] feedback = Enumerable.Range(0, 7).Select(_ => new History()).ToArray();
    private readonly List<TrackingPoint>[] axes = Enumerable.Range(0, 6).Select(_ => new List<TrackingPoint>()).ToArray();
    private readonly string[] invalid = Enumerable.Repeat("等待时间对齐的反馈", 6).ToArray();
    private readonly List<(double Time, bool Valid)> solves = [];
    private readonly List<double> frameTimes = [];
    private MotorTracking[] motors = Enumerable.Range(0, 7).Select(_ => new MotorTracking(null, null, null, ushort.MaxValue)).ToArray();
    private TrackingPacket latest;
    private uint boot, session, sequence;
    private long received, missing, corrupt, reordered;
    private double lastHost, residual = double.NaN;
    private double[] seed;
    private double? deliveryAge, clockUncertainty;
    public double WindowSeconds { get; set; } = 4;

    public void Reset()
    {
        foreach (var h in target.Concat(command).Concat(feedback)) h.Clear();
        foreach (var list in axes) list.Clear();
        solves.Clear(); frameTimes.Clear(); seed = null; latest = null;
        deliveryAge = clockUncertainty = null;
        boot = session = sequence = 0; received = missing = corrupt = reordered = 0; residual = double.NaN;
        Array.Fill(invalid, "等待时间对齐的反馈");
        motors = Enumerable.Range(0, 7).Select(_ => new MotorTracking(null, null, null, ushort.MaxValue)).ToArray();
    }
    public void Corrupt() => corrupt++;

    public void Add(TrackingPacket p, double hostSeconds, double? deliveryAgeMs = null, double? clockUncertaintyMs = null)
    {
        if (latest != null && (p.Boot != boot || p.Session != session)) Reset();
        if (latest != null)
        {
            var delta = unchecked((int)(p.Sequence - sequence));
            if (delta <= 0 || p.TimeMicroseconds <= latest.TimeMicroseconds) { reordered++; return; }
            missing += delta - 1;
        }
        latest = p; boot = p.Boot; session = p.Session; sequence = p.Sequence; received++; lastHost = hostSeconds;
        var time = p.TimeMicroseconds / 1e6;
        frameTimes.Add(time); deliveryAge = deliveryAgeMs; clockUncertainty = clockUncertaintyMs;
        if (p.Flags.HasFlag(TrackingFlags.TargetValid))
            for (var i = 0; i < 6; i++) target[i].Add(time - p.TargetAgeMs / 1000d, p.Target[i]);
        for (var i = 0; i < 7; i++)
        {
            if ((p.CommandValid & (1 << i)) != 0) command[i].Add(time - p.CommandAgeMs[i] / 1000d, p.Command[i]);
            if ((p.FeedbackValid & (1 << i)) != 0) feedback[i].Add(time - p.FeedbackAgeMs[i] / 1000d, p.Feedback[i]);
            if ((p.FeedbackValid & (1 << i)) != 0 && command[i].At(time - p.FeedbackAgeMs[i] / 1000d, out var wanted))
                motors[i] = new(wanted * 180 / Math.PI, p.Feedback[i] * 180 / Math.PI,
                    (p.Feedback[i] - wanted) * 180 / Math.PI, p.FeedbackAgeMs[i]);
            else motors[i] = new(null, null, null, p.FeedbackAgeMs[i]);
        }

        var status = !p.Flags.HasFlag(TrackingFlags.Running) ? "设备尚未进入运行状态" :
            !p.Flags.HasFlag(TrackingFlags.TargetValid) ? "目标快照已过期" :
            p.Flags.HasFlag(TrackingFlags.Waiting) ? "等待新的动作指令" :
            !p.Flags.HasFlag(TrackingFlags.Reachable) ? "目标超出工作空间" : null;
        if (status != null) { for (var i = 0; i < 6; i++) Gap(i, time, status); return; }

        if ((p.MotorMask & 63) != 63 || (p.FeedbackValid & 63) != 63)
        {
            foreach (var i in new[] { 0, 1, 2, 4, 5 }) Gap(i, time, "并联机构需要六台电机的新鲜反馈");
        }
        else
        {
            var common = feedback.Take(6).Min(h => h.LatestTime);
            var joints = new double[6]; var goal = new double[6]; var aligned = double.IsFinite(common);
            for (var i = 0; i < 6; i++) aligned &= feedback[i].At(common, out joints[i]) && target[i].At(common, out goal[i]);
            if (aligned)
            {
                var solved = CadForwardKinematics.Solve(joints, seed ?? CadForwardKinematics.SeedFromTip(goal, p.StickMm));
                solves.Add((time, solved.Valid)); residual = solved.MaxResidualMm;
                if (solved.Valid)
                {
                    seed = solved.Receiver;
                    // R0 does not influence the five parallel coordinates.
                    var actual = CadForwardKinematics.TipAxes(solved.Receiver, 0, p.StickMm);
                    foreach (var i in new[] { 0, 1, 2, 4, 5 }) AddAxis(i, common, goal[i], actual[i]);
                }
                else foreach (var i in new[] { 0, 1, 2, 4, 5 }) Gap(i, time, "正运动学闭合残差过大或未收敛");
            }
            else foreach (var i in new[] { 0, 1, 2, 4, 5 }) Gap(i, time, "等待同一时刻的六电机反馈与目标");
        }
        if ((p.MotorMask & 64) != 0 && (p.FeedbackValid & 64) != 0 &&
            target[3].At(feedback[6].LatestTime, out var twistTarget))
            AddAxis(3, feedback[6].LatestTime, twistTarget, p.Feedback[6] * 180 / Math.PI);
        else Gap(3, time, (p.MotorMask & 64) == 0 ? "未启用独立 R0 电机" : "R0 反馈过期或尚未对齐");
    }

    private void AddAxis(int axis, double time, double wanted, double actual)
    {
        var list = axes[axis];
        // A previous invalid marker must not make a later valid, slightly older
        // source timestamp look out of order. Remove only that trailing marker.
        while (list.Count > 0 && !double.IsFinite(list[^1].Actual) && time <= list[^1].Time) list.RemoveAt(list.Count - 1);
        if (list.Count > 0 && time <= list[^1].Time) return;
        list.Add(new(time, wanted, actual)); invalid[axis] = null;
        Trim(list);
    }
    private void Gap(int axis, double time, string reason)
    {
        invalid[axis] = reason;
        var list = axes[axis];
        if (list.Count == 0 || time > list[^1].Time)
            list.Add(new(time, double.NaN, double.NaN));
        Trim(list);
    }
    private static void Trim(List<TrackingPoint> list)
    { if (list.Count > 1800) list.RemoveRange(0, list.Count - 1500); }

    public TrackingSnapshot Snapshot()
    {
        if (latest == null)
        {
            var empty = TrackingSnapshot.Empty();
            return empty with { Quality = empty.Quality with { Corrupt = corrupt, Reordered = reordered, WindowSeconds = WindowSeconds } };
        }
        var end = latest.TimeMicroseconds / 1e6;
        solves.RemoveAll(s => s.Time < end - WindowSeconds);
        frameTimes.RemoveAll(t => t < end - WindowSeconds);
        var rate = frameTimes.Count < 2 || frameTimes[^1] <= frameTimes[0] ? (double?)null :
            (frameTimes.Count - 1) / (frameTimes[^1] - frameTimes[0]);
        var enabledAges = Enumerable.Range(0, 7).Where(i => (latest.MotorMask & (1 << i)) != 0).Select(i => latest.FeedbackAgeMs[i]).ToArray();
        return new(axes.Select((list, i) => new TrackingAxis(TrackingSnapshot.Names[i], i < 3 ? "mm" : "°",
            list.Where(p => p.Time >= end - WindowSeconds).ToArray(), invalid[i] == null
                ? TrackingStatistics.Analyze(list, i, WindowSeconds, end) : AxisTrackingStatistics.Unavailable(invalid[i]), invalid[i])).ToArray(),
            (MotorTracking[])motors.Clone(),
            new(received, missing, corrupt, reordered, solves.Count == 0 ? null : 100d * solves.Count(s => s.Valid) / solves.Count,
                double.IsFinite(residual) ? residual : null, enabledAges.Length == 0 ? ushort.MaxValue : enabledAges.Max(), latest.PeriodMs,
                latest.Flags.HasFlag(TrackingFlags.WorkspaceReduced), lastHost, WindowSeconds,
                deliveryAge, clockUncertainty, rate, rate.HasValue ? Math.Min(100, rate.Value * latest.PeriodMs / 10) : null),
            latest.Flags.HasFlag(TrackingFlags.Paused) ? "暂停／保持中" : "实时监测");
    }
}
