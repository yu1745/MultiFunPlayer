using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal sealed record BufferedAxisLimits(double Velocity, double Acceleration, double Jerk)
{
    // The host adds 2% headroom to script peaks for the P4 float sampler.
    public double AllowedDistance(double seconds) => .98 * Math.Min(Velocity * seconds / 2.1875,
        Math.Min(Acceleration * seconds * seconds / 7.513189, Jerk * seconds * seconds * seconds / 52.5));

    public double RequiredSeconds(double distance) => Math.Max(distance * 2.1875 / (.98 * Velocity),
        Math.Max(Math.Sqrt(distance * 7.513189 / (.98 * Acceleration)), Math.Cbrt(distance * 52.5 / (.98 * Jerk))));
}

internal sealed record BufferedSessionLimits(BufferedAxisLimits Translation, BufferedAxisLimits Rotation)
{
    public BufferedAxisLimits ForAxis(int axis) => axis < 3 ? Translation : Rotation;

    public ulong[] Encode()
    {
        double[] values = [Translation.Velocity, Translation.Acceleration, Translation.Jerk,
            Rotation.Velocity, Rotation.Acceleration, Rotation.Jerk];
        string[] names = ["Translation velocity", "Translation acceleration", "Translation jerk",
            "Rotation velocity", "Rotation acceleration", "Rotation jerk"];
        var encoded = new ulong[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var wireValue = Math.Ceiling(values[i] * 1e6);
            if (!double.IsFinite(wireValue) || wireValue < 1 || wireValue > TCodeBufferedProtocol.MaxInteger)
                throw new InvalidDataException(FormattableString.Invariant(
                    $"{names[i]} ({values[i]:G9}) cannot be encoded as a positive protocol integer at 1e6 precision. No trajectory was sent."));
            encoded[i] = checked((ulong)wireValue);
        }
        return encoded;
    }

    public static BufferedSessionLimits FromScript(List<BufferedNode>[] nodes, double[] positions)
    {
        if (positions.Length != 6 || positions.Any(x => !double.IsFinite(x) || x < 0 || x > 1))
            throw new InvalidDataException("Invalid P4 starting pose.");
        var peaks = new double[2, 3];
        void Include(int group, double distance, double seconds)
        {
            if (distance == 0) return;
            peaks[group, 0] = Math.Max(peaks[group, 0], 2.1875 * distance / seconds);
            peaks[group, 1] = Math.Max(peaks[group, 1], 7.513189 * distance / (seconds * seconds));
            peaks[group, 2] = Math.Max(peaks[group, 2], 52.5 * distance / (seconds * seconds * seconds));
        }
        for (var axis = 0; axis < 6; axis++)
            foreach (var pair in nodes[axis].Zip(nodes[axis].Skip(1)))
                Include(axis / 3, Math.Abs((double)pair.Second.Position - pair.First.Position) / 10000,
                    (pair.Second.Microseconds - pair.First.Microseconds) / 1e6);
        // A static group has no script derivatives. Its only motion is positioning;
        // derive that group's envelope from a one-second move to its first pose.
        for (var group = 0; group < 2; group++)
            if (peaks[group, 0] == 0)
                for (var axis = group * 3; axis < group * 3 + 3; axis++)
                    if (nodes[axis].Count > 0)
                        Include(group, Math.Abs(nodes[axis][0].Position / 10000d - positions[axis]), 1);
        BufferedAxisLimits Group(int group) => peaks[group, 0] == 0 ? new(.01, .01, .01) : new(
            Math.Max(.000001, Math.Ceiling(peaks[group, 0] / .98 * 1e6) / 1e6),
            Math.Max(.000001, Math.Ceiling(peaks[group, 1] / .98 * 1e6) / 1e6),
            Math.Max(.000001, Math.Ceiling(peaks[group, 2] / .98 * 1e6) / 1e6));
        var result = new BufferedSessionLimits(Group(0), Group(1));
        result.Encode(); // reject unsupported demand before acquiring device motion
        return result;
    }
}

internal sealed record BufferedDevicePose(double[] Positions)
{
    public static BufferedDevicePose Decode(ulong[] fields)
    {
        if (fields.Length != 13 || fields[0] != 101 || fields[4] != 0 || fields[6] != 1
            || fields.Skip(7).Any(x => x > 1000000))
            throw new InvalidDataException("Unsupported or invalid P4 pose response.");
        return new(fields.Skip(7).Select(x => x / 1e6).ToArray());
    }
}

internal sealed record BufferedSessionPlan(List<BufferedNode>[] Nodes, BufferedSessionLimits Limits, ulong PositioningMicroseconds);
