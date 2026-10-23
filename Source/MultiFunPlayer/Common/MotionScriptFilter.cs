namespace MultiFunPlayer.Common;

// Shared by the preview and every script output path. Times are media seconds;
// the threshold is normalized full travel per wall-clock second after script scale.
internal sealed record MotionFilterSettings(bool Enabled = false, double SpeedThreshold = 1, double SmoothingMilliseconds = 100)
{
    public bool IsValid => double.IsFinite(SpeedThreshold) && SpeedThreshold is >= .01 and <= 1000
        && double.IsFinite(SmoothingMilliseconds) && SmoothingMilliseconds is >= 5 and <= 1000;
}

internal static class MotionScriptFilter
{
    public static readonly string[] Axes = ["L0", "L1", "L2", "R0", "R1", "R2"];

    public static IReadOnlyList<Keyframe> Apply(IReadOnlyList<Keyframe> source, MotionFilterSettings settings,
        double playbackSpeed = 1, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings), "滤波参数超出范围。");
        if (!double.IsFinite(playbackSpeed) || playbackSpeed <= 0 || !double.IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(playbackSpeed));
        if (!settings.Enabled || source.Count < 3 || scale == 0) return source;
        var count = source.Count;
        var input = new double[count];
        var gate = new double[count];
        var active = false;
        for (var i = 0; i < count; i++)
        {
            var point = source[i];
            if (!double.IsFinite(point.Position) || !double.IsFinite(point.Value) || point.Value is < 0 or > 1
                || i > 0 && point.Position <= source[i - 1].Position)
                throw new ArgumentException("脚本时间必须递增，位置必须在 0–1 内。", nameof(source));
            input[i] = point.Value;
            if (i == 0) continue;
            var speed = Math.Abs(point.Value - source[i - 1].Value) / (point.Position - source[i - 1].Position)
                * playbackSpeed * Math.Abs(scale);
            // Soft threshold avoids a discontinuity when a segment just crosses
            // the trigger. This is a smoothing trigger, not a hard velocity cap.
            var ratio = speed / settings.SpeedThreshold;
            var blend = ratio <= 1 + 1e-9 ? 0 : Math.Clamp(ratio - 1, 0, 1);
            blend = blend * blend * (3 - 2 * blend);
            gate[i - 1] = Math.Max(gate[i - 1], blend);
            gate[i] = Math.Max(gate[i], blend);
            active |= blend > 0;
        }
        if (!active) return source;
        var tau = settings.SmoothingMilliseconds / 1000 * playbackSpeed;
        var filtered = LowPass(source, input, tau);
        var weights = LowPass(source, gate, tau / 2);
        var result = new Keyframe[count];
        for (var i = 0; i < count; i++)
        {
            // Preserve the script's endpoints and duration. Ease in/out over one
            // time constant instead of introducing a new jump at the boundary.
            var edge = Math.Clamp(Math.Min(source[i].Position - source[0].Position,
                source[^1].Position - source[i].Position) / tau, 0, 1);
            edge = edge * edge * (3 - 2 * edge);
            var value = input[i] + weights[i] * edge * (filtered[i] - input[i]);
            result[i] = source[i] with { Value = Math.Clamp(value, 0, 1) };
        }
        return result;
    }

    // Classical first-order low-pass in both directions. Integrate each linear
    // input interval analytically: irregular keyframe spacing does not change
    // the time constant, and no graphics/sampling grid participates in filtering.
    private static double[] LowPass(IReadOnlyList<Keyframe> times, double[] input, double tau)
    {
        var result = new double[input.Length];
        result[0] = input[0];
        for (var i = 1; i < input.Length; i++)
            result[i] = Step(input[i - 1], input[i], result[i - 1], times[i].Position - times[i - 1].Position, tau);
        var forward = (double[])result.Clone();
        for (var i = input.Length - 2; i >= 0; i--)
            result[i] = Step(forward[i + 1], forward[i], result[i + 1], times[i + 1].Position - times[i].Position, tau);
        return result;
    }

    private static double Step(double from, double to, double previous, double dt, double tau)
    {
        var r = dt / tau;
        // 1-exp(-r), expanded near zero to avoid cancellation for dense scripts.
        var alpha = r < .0001 ? r * (1 - r * (.5 - r / 6)) : 1 - Math.Exp(-r);
        var ramp = r < .0001 ? r * (.5 - r / 6) : 1 - alpha / r;
        return previous + alpha * (from - previous) + (to - from) * ramp;
    }
}
