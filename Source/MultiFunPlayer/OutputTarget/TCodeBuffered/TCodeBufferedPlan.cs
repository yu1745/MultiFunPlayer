using MultiFunPlayer.Common;
using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal readonly record struct BufferedNode(int Axis, ulong Microseconds, ulong Position);

internal static class TCodeBufferedPlan
{
    public static readonly string[] AxisNames = ["L0", "L1", "L2", "R0", "R1", "R2"];

    public static LimitedBufferedPlan CreateLimited(BufferedPlaybackSnapshot snapshot, TCodeBufferedLimits limits)
    {
        var nodes = Create(snapshot, 0);
        var scale = 1d;
        for (var axis = 0; axis < 6; axis++)
        {
            foreach (var pair in nodes[axis].Zip(nodes[axis].Skip(1)))
            {
                var distance = Math.Abs((double)pair.Second.Position - pair.First.Position);
                if (distance == 0) continue;
                var allowed = limits.ForAxis(axis).AllowedDistance((pair.Second.Microseconds - pair.First.Microseconds) / 1e6) * 10000;
                if (distance > allowed)
                    scale = Math.Min(scale, Math.Max(0, allowed - 2) / distance);
            }
        }
        if (scale < 1)
        {
            for (var axis = 0; axis < 6; axis++)
            {
                if (nodes[axis].Count == 0) continue;
                var settings = snapshot.Axes.Single(x => x.Name == AxisNames[axis]);
                var center = (settings.Minimum + settings.Maximum) * .5 * 9999;
                for (var i = 0; i < nodes[axis].Count; i++)
                    nodes[axis][i] = nodes[axis][i] with { Position = (ulong)Math.Round(center + scale * (nodes[axis][i].Position - center)) };
            }
        }

        // Reposition from the device's queried pose. Long, low-limit positioning
        // is split into feasible <=55 s intervals; media stays paused throughout.
        var steps = 1;
        for (var axis = 0; axis < 6; axis++)
        {
            if (nodes[axis].Count == 0) continue;
            var distance = Math.Abs(nodes[axis][0].Position / 10000d - limits.Positions[axis]);
            steps = Math.Max(steps, checked((int)Math.Ceiling((distance + .0003) / limits.ForAxis(axis).AllowedDistance(55))));
        }
        if (steps > 512)
            throw new InvalidDataException("Device limits require too many initial positioning segments.");
        var segmentSeconds = 1d;
        for (var axis = 0; axis < 6; axis++)
        {
            if (nodes[axis].Count == 0) continue;
            var distance = Math.Abs(nodes[axis][0].Position / 10000d - limits.Positions[axis]);
            segmentSeconds = Math.Max(segmentSeconds, limits.ForAxis(axis).RequiredSeconds(distance / steps + .0003));
        }
        var segmentUs = (ulong)Math.Ceiling(segmentSeconds * 1e6);
        if (segmentUs > 60000000)
            throw new InvalidDataException("Device limits cannot accommodate positioning within the supported interval.");
        var positioning = segmentUs * (ulong)steps;
        for (var axis = 0; axis < 6; axis++)
        {
            if (nodes[axis].Count == 0) continue;
            var first = nodes[axis][0].Position;
            var start = limits.Positions[axis] * 10000;
            var prelude = new List<BufferedNode>();
            for (var i = 1; i < steps; i++)
                prelude.Add(new(axis, segmentUs * (ulong)i, (ulong)Math.Clamp(Math.Round(start + (first - start) * i / steps), 0, 9999)));
            prelude.AddRange(nodes[axis].Select(n => n with { Microseconds = checked(n.Microseconds + positioning) }));
            nodes[axis] = prelude;
            var previousTime = 0UL;
            var previousPosition = limits.Positions[axis];
            foreach (var node in prelude)
            {
                var distance = Math.Abs(node.Position / 10000d - previousPosition);
                if (distance > limits.ForAxis(axis).AllowedDistance((node.Microseconds - previousTime) / 1e6))
                    throw new InvalidDataException($"{AxisNames[axis]}: quantized trajectory exceeds device limits.");
                previousTime = node.Microseconds;
                previousPosition = node.Position / 10000d;
            }
        }
        return new(nodes, scale, positioning);
    }

    public static List<BufferedNode>[] Create(BufferedPlaybackSnapshot snapshot, ulong epoch)
    {
        if (!double.IsFinite(snapshot.Position) || !double.IsFinite(snapshot.Speed) || snapshot.Speed <= 0)
            throw new InvalidDataException("TCode 0.4 requires a finite media position and positive playback speed.");
        var result = Enumerable.Range(0, 6).Select(_ => new List<BufferedNode>()).ToArray();
        foreach (var axis in snapshot.Axes)
        {
            var index = Array.IndexOf(AxisNames, axis.Name);
            if (index < 0)
                continue;
            if (axis.UnsupportedReason != null)
                throw new InvalidDataException($"{axis.Name}: Buffered Update does not support {axis.UnsupportedReason}. Disable it before playback.");
            if (!double.IsFinite(axis.Offset) || !double.IsFinite(axis.Scale) || axis.Scale < 0
                || !double.IsFinite(axis.DefaultValue) || !double.IsFinite(axis.Minimum) || !double.IsFinite(axis.Maximum)
                || axis.Minimum < 0 || axis.Maximum > 1 || axis.Minimum > axis.Maximum)
                throw new InvalidDataException($"Invalid {axis.Name} buffered output settings.");

            var turns = TurningPoints(axis.Keyframes);
            // Split long segments before seeking so the same playback rate has
            // the same curve whether starting at zero or in the middle.
            var points = new List<Keyframe> { turns[0] };
            for (var i = 1; i < turns.Count; i++)
            {
                var a = turns[i - 1];
                var b = turns[i];
                var segments = Math.Ceiling((b.Position - a.Position) / snapshot.Speed / 60);
                if (segments > 1000000)
                    throw new InvalidDataException("TCode trajectory is too long for this playback speed.");
                for (var n = 1; n <= segments; n++)
                    points.Add(new(a.Position + (b.Position - a.Position) * n / segments,
                        a.Value + (b.Value - a.Value) * n / segments));
            }

            var time = snapshot.Position - axis.Offset;
            var next = points.FindIndex(p => p.Position > time);
            if (next < 0) next = points.Count;
            var left = points[Math.Max(0, next - 1)];
            var right = points[Math.Min(next, points.Count - 1)];
            var u = left.Position < right.Position ? Math.Clamp((time - left.Position) / (right.Position - left.Position), 0, 1) : 0;
            var nodes = result[index];
            var leftValue = Map(left.Value, axis);
            var rightValue = Map(right.Value, axis);
            var initialValue = leftValue + ((double)rightValue - leftValue) * Smooth(u);
            nodes.Add(new(index, epoch, (ulong)Math.Round(initialValue, MidpointRounding.AwayFromZero)));
            for (var i = next; i < points.Count; i++)
            {
                var delta = Math.Round((points[i].Position - time) / snapshot.Speed * 1000000);
                if (!double.IsFinite(delta) || delta < 0 || delta > TCodeBufferedProtocol.MaxInteger - epoch)
                    throw new InvalidDataException("TCode trajectory time is out of range.");
                var us = epoch + (ulong)delta;
                // A delayed first keyframe represents a hold, not a very long
                // repositioning segment. P4 limits every interval to 60 s.
                while (us - nodes[^1].Microseconds > 60000000)
                    nodes.Add(new(index, nodes[^1].Microseconds + 60000000, nodes[^1].Position));
                if (us - nodes[^1].Microseconds < 1000)
                    throw new InvalidDataException($"{axis.Name}: trajectory segment shorter than 1 ms; reduce playback speed or seek away from the endpoint.");
                nodes.Add(new(index, us, Map(points[i].Value, axis)));
            }
        }
        if (result.All(x => x.Count == 0))
            throw new InvalidDataException("Buffered Update requires an enabled L0/L1/L2/R0/R1/R2 script.");
        return result;
    }

    internal static double Smooth(double u) => u * u * u * u * (35 + u * (-84 + u * (70 - 20 * u)));

    private static ulong Map(double value, BufferedScriptAxis axis)
    {
        value = Math.Clamp(axis.DefaultValue + (value - axis.DefaultValue) * axis.Scale, 0, 1);
        if (axis.Invert) value = 1 - value;
        return (ulong)Math.Round((axis.Minimum + (axis.Maximum - axis.Minimum) * value) * 9999, MidpointRounding.AwayFromZero);
    }

    // Same 1% hysteresis as CTW's xtp-web planner. Preserve the explicit
    // stationary introduction, rather than stretching the first stroke into it.
    internal static List<Keyframe> TurningPoints(IReadOnlyList<Keyframe> points)
    {
        if (points == null || points.Count == 0)
            throw new InvalidDataException("Empty TCode script.");
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Position) || !double.IsFinite(p.Value) || p.Position < 0
                || p.Value < 0 || p.Value > 1 || (i > 0 && p.Position <= points[i - 1].Position))
                throw new InvalidDataException("TCode 0.4 requires finite, increasing keyframes with values in 0..1.");
        }
        var turns = new List<Keyframe> { points[0] };
        var direction = 0;
        var extreme = points[0];
        var departure = points[0];
        foreach (var point in points.Skip(1))
        {
            if (direction == 0)
            {
                if (point.Value == turns[0].Value) departure = point;
                if (Math.Abs(point.Value - turns[0].Value) >= .01 - 1e-12)
                {
                    if (departure.Position > turns[0].Position) turns.Add(departure);
                    direction = Math.Sign(point.Value - turns[0].Value);
                    extreme = point;
                }
            }
            else if (direction * (point.Value - extreme.Value) >= 0)
                extreme = point;
            else if (direction * (extreme.Value - point.Value) >= .01 - 1e-12)
            {
                turns.Add(extreme);
                direction = -direction;
                extreme = point;
            }
        }
        if (direction != 0 && Math.Abs(extreme.Value - turns[^1].Value) >= .01 - 1e-12)
            turns.Add(extreme);
        if (turns[^1].Position != points[^1].Position)
            turns.Add(points[^1]);
        return turns;
    }
}
