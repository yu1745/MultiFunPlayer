using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal sealed record BufferedAxisLimits(double Velocity, double Acceleration, double Jerk)
{
    // Reserve 2% inside the device's limits for float sampling and position encoding.
    public double AllowedDistance(double seconds) => .98 * Math.Min(Velocity * seconds / 2.1875,
        Math.Min(Acceleration * seconds * seconds / 7.513189, Jerk * seconds * seconds * seconds / 52.5));

    public double RequiredSeconds(double distance) => Math.Max(distance * 2.1875 / (.98 * Velocity),
        Math.Max(Math.Sqrt(distance * 7.513189 / (.98 * Acceleration)), Math.Cbrt(distance * 52.5 / (.98 * Jerk))));
}

internal sealed record TCodeBufferedLimits(ulong Revision, BufferedAxisLimits Translation,
    BufferedAxisLimits Rotation, double[] Positions)
{
    public BufferedAxisLimits ForAxis(int axis) => axis < 3 ? Translation : Rotation;

    public static TCodeBufferedLimits Decode(ulong[] fields)
    {
        if (fields.Length != 20 || fields[0] != 101 || fields[4] != 0 || fields[6] == 0 || fields[7] != 1
            || fields.Skip(8).Take(6).Any(x => x == 0 || x > 100000000000UL)
            || fields.Skip(14).Any(x => x > 1000000))
            throw new InvalidDataException("Unsupported or invalid P4 motion limits. Update the device firmware.");
        return new(fields[6], new(fields[8] / 1e6, fields[9] / 1e6, fields[10] / 1e6),
            new(fields[11] / 1e6, fields[12] / 1e6, fields[13] / 1e6), fields.Skip(14).Select(x => x / 1e6).ToArray());
    }
}

internal sealed record LimitedBufferedPlan(List<BufferedNode>[] Nodes, double AmplitudeScale, ulong PositioningMicroseconds);
