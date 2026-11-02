namespace MultiFunPlayer.OutputTarget.Tracking;

// Passive, session-local observer. Current residual is not payload mass or a
// receiver wrench: friction, preload and acceleration also contribute to Iq.
internal sealed class ResidualLoadObserver
{
    internal sealed record Result(string State, double[] ResidualA = null, double[] ExcessA = null,
        bool SustainedChange = false, int BaselineCount = 0, int CaptureCount = 0);
    private sealed record Node(double[] Pose, double[] Mean, double[] Spread);
    private readonly List<Node> nodes = [];
    private readonly Queue<TrackingPacket> poses = new();
    private readonly List<(double[] Pose, double[] Iq)> capture = [];
    private readonly double[] lastCurrentUs = new double[6];
    private uint boot;
    private bool capturing;
    private int changed;
    private ulong lastEvaluation;
    public Result Latest { get; private set; } = new("等待回传；需采集当前连接的空载基线");
    public void Reset()
    {
        nodes.Clear(); poses.Clear(); capture.Clear(); Array.Clear(lastCurrentUs);
        boot = 0; capturing = false; changed = 0; lastEvaluation = 0;
        Latest = new("连接已重置，请重新采集空载基线");
    }
    public void BeginBaseline() { capturing = true; capture.Clear(); changed = 0; }
    public void AddPose(TrackingPacket p)
    {
        if (boot != 0 && boot != p.Boot) Reset();
        boot = p.Boot;
        if (poses.Count > 0 && p.TimeMicroseconds <= poses.Last().TimeMicroseconds) return;
        poses.Enqueue(p);
        while (poses.Count > 1 && p.TimeMicroseconds - poses.Peek().TimeMicroseconds > 1500000) poses.Dequeue();
    }
    private Result Invalid(string reason)
    {
        changed = 0;
        if (capturing) capture.Clear();
        return Latest = new(reason, BaselineCount: nodes.Count);
    }
    private static double Distance(double[] a, double[] b) => a.Take(6).Select((x, i) =>
        Math.Abs(Math.IEEERemainder(x - b[i], 2 * Math.PI)) * 180 / Math.PI).Max();
    public Result Observe(MotorHealthPacket h, double deliveryAgeMs)
    {
        if (h.Boot != boot || poses.Count < 2) return Invalid("等待同一次启动的位置回传");
        var p = poses.Last();
        if (!double.IsFinite(deliveryAgeMs) || deliveryAgeMs > 150 ||
            Math.Abs((double)h.TimeMicroseconds - p.TimeMicroseconds) > 150000)
            return Invalid("回传延迟或位置不同步，暂停估计");
        if ((h.MotorMask & 63) != 63 || h.Motors.Take(6).Any(m => m.State != 8 || m.Error != 0 ||
            m.StateAgeMs + deliveryAgeMs > 750 || !m.Value(1, deliveryAgeMs).HasValue))
            return Invalid("电流过期、电机未闭环或故障，暂停估计");
        if (p.TimeMicroseconds - poses.Peek().TimeMicroseconds < 1000000 || poses.Any(x =>
            (x.FeedbackValid & 63) != 63 || (x.CommandValid & 63) != 63 ||
            x.Feedback.Take(6).Any(v => !double.IsFinite(v)) || x.Command.Take(6).Any(v => !double.IsFinite(v)) ||
            Distance(x.Feedback, p.Feedback) > .15 || Distance(x.Command, p.Command) > .15))
            return Invalid("运动中／尚未静稳：动态载荷未标定");
        var ordered = poses.ToArray();
        if (ordered.Zip(ordered.Skip(1)).Any(x => x.Second.TimeMicroseconds - x.First.TimeMicroseconds > 200000))
            return Invalid("位置回传缺口，暂停估计");
        var stamps = h.Motors.Take(6).Select(m => (double)h.TimeMicroseconds - m.AgesMs[1] * 1000d).ToArray();
        // Ages are rounded to milliseconds: a cached sample can jitter by 1 ms.
        if (stamps.Where((t, i) => t <= lastCurrentUs[i] + 2000).Any())
            return Latest with { State = "等待六台电机新的电流样本 · " + (capturing ? $"空载采集中 {capture.Count}/15" : "") };
        if (lastEvaluation != 0 && h.TimeMicroseconds - lastEvaluation > 1500000) changed = 0;
        lastEvaluation = h.TimeMicroseconds;
        stamps.CopyTo(lastCurrentUs, 0);
        var iq = h.Motors.Take(6).Select(m => m.Values[1]).ToArray();
        if (capturing)
        {
            if (capture.Count > 0 && Distance(capture[0].Pose, p.Feedback) > .15) capture.Clear();
            capture.Add((p.Feedback.Take(6).ToArray(), iq));
            if (capture.Count < 15) return Latest = new("正在采集静止空载基线", BaselineCount: nodes.Count, CaptureCount: capture.Count);
            var mean = Enumerable.Range(0, 6).Select(i => capture.Average(s => s.Iq[i])).ToArray();
            var spread = Enumerable.Range(0, 6).Select(i => Math.Max(.08,
                3 * Math.Sqrt(capture.Sum(s => Math.Pow(s.Iq[i] - mean[i], 2)) / (capture.Count - 1)))).ToArray();
            if (nodes.Count >= 64) nodes.RemoveAt(0);
            nodes.Add(new(capture[0].Pose, mean, spread)); capturing = false; capture.Clear();
            TestRecording.Record("residual_load_baseline", new { h.Boot, node = nodes.Last(), units = "CAD rad; Iq A" });
        }
        var near = nodes.Where(n => Distance(n.Pose, p.Feedback) <= 1).ToArray();
        if (near.Length == 0) return Invalid("当前位置未采集空载基线；不能估计载荷");
        var residual = new double[6]; var excess = new double[6];
        for (var i = 0; i < 6; i++)
        {
            var lo = near.Min(n => n.Mean[i] - n.Spread[i]); var hi = near.Max(n => n.Mean[i] + n.Spread[i]);
            residual[i] = iq[i] - near.Average(n => n.Mean[i]);
            excess[i] = iq[i] > hi ? iq[i] - hi : iq[i] < lo ? iq[i] - lo : 0;
        }
        changed = excess.Any(x => Math.Abs(x) > .05) ? changed + 1 : 0;
        return Latest = new(changed >= 4 ? "持续偏离空载基线（载荷／摩擦／预紧变化）" : "静态电流残差 · 可信度：初步，尚未加载验证",
            residual, excess, changed >= 4, nodes.Count);
    }
}
