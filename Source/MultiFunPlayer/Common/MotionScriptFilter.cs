namespace MultiFunPlayer.Common;

internal sealed record AxisMotionFilter(bool Enabled = true, double Strength = 50)
{
    public bool IsValid => double.IsFinite(Strength) && Strength is >= 0 and <= 100;
}

internal sealed record MotionFilterSettings(bool Enabled = false)
{
    public int AlgorithmVersion { get; init; } = 2;
    public int TuningRevision { get; init; } = 3;
    public AxisMotionFilter L0 { get; init; } = new();
    public AxisMotionFilter L1 { get; init; } = new();
    public AxisMotionFilter L2 { get; init; } = new();
    public AxisMotionFilter R0 { get; init; } = new();
    public AxisMotionFilter R1 { get; init; } = new();
    public AxisMotionFilter R2 { get; init; } = new();
    public AxisMotionFilter ForAxis(string axis) => axis switch
    {
        "L0" => L0, "L1" => L1, "L2" => L2, "R0" => R0, "R1" => R1, "R2" => R2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };
    public bool IsValid => AlgorithmVersion == 2 && MotionScriptFilter.Axes.All(a => ForAxis(a)?.IsValid == true);
}

// Offline, local-script transform. It deliberately uses adjacent completed strokes:
// no playback time shift is introduced, but this is NOT a causal live/game filter.
// Strength thresholds are provisional normalized travel rates, not physical mm/s.
internal static class MotionScriptFilter
{
    public static readonly string[] Axes = ["L0", "L1", "L2", "R0", "R1", "R2"];

    public static IReadOnlyList<Keyframe> Apply(IReadOnlyList<Keyframe> source, MotionFilterSettings settings,
        double playbackSpeed = 1, double scale = 1, string axis = "L0")
    {
        ArgumentNullException.ThrowIfNull(source);
        if (settings == null || !settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        if (!double.IsFinite(playbackSpeed) || playbackSpeed <= 0 || !double.IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(playbackSpeed));
        var tuning = settings.ForAxis(axis);
        if (!settings.Enabled || !tuning.Enabled || tuning.Strength == 0 || source.Count < 3 || scale == 0) return source;
        for (var i = 0; i < source.Count; i++)
            if (!double.IsFinite(source[i].Position) || !double.IsFinite(source[i].Value)
                || source[i].Value is < 0 or > 1 || i > 0 && source[i].Position <= source[i - 1].Position)
                throw new ArgumentException("脚本时间必须递增，位置必须在 0–1 内。", nameof(source));

        // Retain turns AND both ends of holds. Collinear subdivisions cannot
        // change the decision merely by increasing the keyframe density.
        var knots = new List<int> { 0 };
        for (var i = 1; i < source.Count - 1; i++)
            if (Math.Sign(source[i].Value - source[i - 1].Value) != Math.Sign(source[i + 1].Value - source[i].Value))
                knots.Add(i);
        knots.Add(source.Count - 1);
        var n = knots.Count;
        var x = knots.Select(i => source[i].Value).ToArray();
        var y = (double[])x.Clone();
        var gain = Enumerable.Repeat(1d, n).ToArray();
        var strength = tuning.Strength / 100;
        var speedThreshold = 3.5 - 2.7 * strength;
        var frequencyThreshold = 2.0 - .8 * strength; // complete cycles / second
        var active = false;
        for (var k = 1; k < n - 1; k++)
        {
            var leftK = k;
            var rightK = k;
            while(leftK > 0 && x[leftK - 1] == x[k]) leftK--;
            while(rightK < n - 1 && x[rightK + 1] == x[k]) rightK++;
            if(leftK == 0 || rightK == n - 1) continue;
            var leftTime = (source[knots[leftK]].Position - source[knots[leftK - 1]].Position) / playbackSpeed;
            var rightTime = (source[knots[rightK + 1]].Position - source[knots[rightK]].Position) / playbackSpeed;
            var holdTime = (source[knots[rightK]].Position - source[knots[leftK]].Position) / playbackSpeed;
            var left = x[k] - x[leftK - 1];
            var right = x[rightK + 1] - x[k];
            if (left * right >= 0 || holdTime > .3) continue;
            // Peak slope matters for eased strokes whose average speed hides a fast centre.
            var speed = 0d;
            for(var i=knots[leftK - 1]+1;i<=knots[rightK + 1];i++)
                speed=Math.Max(speed,Math.Abs(source[i].Value-source[i-1].Value)
                    /(source[i].Position-source[i-1].Position)*playbackSpeed*Math.Abs(scale));
            var frequency = 1 / (leftTime + rightTime + holdTime);
            var severity = Math.Max(frequency >= .8 ? speed / speedThreshold : 0, frequency / frequencyThreshold);
            // C1 onset: no discrete on/off jump at the trigger threshold.
            var excess = Math.Max(0, severity - 1);
            gain[k] = 1 / (1 + excess * excess);
            active |= gain[k] < 1;
        }
        if (!active) return source;
        for (var k = 1; k < n - 1; k++)
        {
            // Local envelope, not a global 50% centre. Neighbouring attenuation
            // softens entry/exit without attenuating the whole script.
            var g = Math.Min(gain[k], Math.Min((1 + gain[k - 1]) / 2, (1 + gain[k + 1]) / 2));
            var l=k-1;var r=k+1;
            while(l>0 && x[l]==x[k]) l--;
            while(r<n-1 && x[r]==x[k]) r++;
            var centre = (x[k] + (x[l] + x[r]) / 2) / 2;
            y[k] = centre + (x[k] - centre) * g;
        }

        // Project adjacent endpoints onto their original directional range.
        // This prevents newly created reverse strokes or faster bridge strokes
        // when envelopes/strengths differ. Endpoints stay fixed; holds stay flat.
        for (var pass = 0; pass < 128; pass++)
        {
            var maxCorrection = 0d;
            for (var j = 0; j < n - 1; j++)
            {
                var k = pass % 2 == 0 ? j : n - 2 - j;
                var dx = x[k + 1] - x[k];
                var dy = y[k + 1] - y[k];
                var minimum = dx == 0 ? 0 : Math.Abs(dx) * .001;
                var desired = dx == 0 ? 0 : Math.Sign(dx) * Math.Clamp(Math.Sign(dx) * dy, minimum, Math.Abs(dx));
                var correction = dy - desired;
                maxCorrection = Math.Max(maxCorrection, Math.Abs(correction));
                if (k == 0) y[k + 1] -= correction;
                else if (k + 1 == n - 1) y[k] += correction;
                else { y[k] += correction / 2; y[k + 1] -= correction / 2; }
            }
            if (maxCorrection < 1e-12) break;
        }
        // A pathological chain must never escape the direction/slope contract.
        // Fall back to the unchanged script rather than sending an invalid plan.
        for (var k = 0; k < n - 1; k++)
        {
            var dx = x[k + 1] - x[k];
            var dy = y[k + 1] - y[k];
            if (Math.Abs(dy) > Math.Abs(dx) + 1e-9 || dx * dy < -1e-12) return source;
        }
        // Strength must also bound the amount, not merely change a trigger.
        // Convex mixing after projection preserves all direction/speed constraints.
        // At strength 1 no stroke can lose more than 5%; at 0 it is exact bypass.
        var amount = Math.Min(1, tuning.Strength / 20);
        for(var k=0;k<n;k++) y[k]=x[k]+amount*(y[k]-x[k]);
        var result = new Keyframe[source.Count];
        for (var k = 0; k < n - 1; k++)
            for (var i = knots[k]; i <= knots[k + 1]; i++)
            {
                var dx = x[k + 1] - x[k];
                var fraction = dx == 0 ? 0 : (source[i].Value - x[k]) / dx;
                result[i] = source[i] with { Value = Math.Clamp(y[k] + (y[k + 1] - y[k]) * fraction, 0, 1) };
            }
        return result;
    }
}

