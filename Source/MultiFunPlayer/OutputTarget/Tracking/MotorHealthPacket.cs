using System.Buffers.Binary;
namespace MultiFunPlayer.OutputTarget.Tracking;

internal sealed record MotorHealth(double[] Values, ushort[] AgesMs, ushort StateAgeMs, uint Error, byte State, byte Valid)
{
    public static readonly int[] MaximumAgeMs = [100, 750, 5000, 5000, 5000];
    public double? Value(int field, double elapsedMs) =>
        (Valid & (1 << field)) != 0 && double.IsFinite(Values[field]) &&
        AgesMs[field] + Math.Max(0, elapsedMs) <= MaximumAgeMs[field] ? Values[field] : null;
}
internal sealed record MotorHealthPacket(uint Boot, uint Session, uint Sequence, ulong TimeMicroseconds,
    byte MotorMask, ushort PeriodMs, uint SkippedQueries, MotorHealth[] Motors)
{
    public const string Prefix = "!MTR1,";
    public static bool TryDecode(string line, out MotorHealthPacket packet, out string error)
    {
        packet = null; error = "电机状态包格式或 CRC 无效";
        line = line.TrimEnd((char)13, (char)10);
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length != Prefix.Length + 416) return false;
        Span<byte> b = stackalloc byte[312];
        if (!Convert.TryFromBase64String(line[Prefix.Length..], b, out var size) || size != 312 ||
            BinaryPrimitives.ReadUInt32LittleEndian(b[308..]) != TrackingPacket.Crc32(b[..308])) return false;
        if (b[0] != 1 || b[1] > 127 || BinaryPrimitives.ReadUInt16LittleEndian(b[2..]) is not (100 or 200)) return false;
        var motors = new MotorHealth[7];
        for (var i = 0; i < 7; i++)
        {
            var d = b[(28 + 40 * i)..];
            if (d[37] > 31 || d[38] != 0 || d[39] != 0 || ((b[1] & (1 << i)) == 0 && d[37] != 0)) return false;
            var values = new double[5]; var ages = new ushort[5];
            for (var f = 0; f < 5; f++)
            {
                values[f] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d[(f * 4)..]));
                ages[f] = BinaryPrimitives.ReadUInt16LittleEndian(d[(20 + f * 2)..]);
                if ((d[37] & (1 << f)) != 0 && (!double.IsFinite(values[f]) || ages[f] > MotorHealth.MaximumAgeMs[f])) return false;
            }
            motors[i] = new(values, ages, BinaryPrimitives.ReadUInt16LittleEndian(d[30..]),
                BinaryPrimitives.ReadUInt32LittleEndian(d[32..]), d[36], d[37]);
        }
        var boot = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        var time = BinaryPrimitives.ReadUInt64LittleEndian(b[16..]);
        if (boot == 0 || time == 0) return false;
        packet = new(boot, BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            time, b[1], BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), BinaryPrimitives.ReadUInt32LittleEndian(b[24..]), motors);
        error = null; return true;
    }
}
internal sealed record MotorHealthSnapshot(MotorHealthPacket Packet, double HostSeconds, double DeliveryAgeMs)
{
    public double ElapsedMs => Math.Max(0, (TrackingMonitor.HostSeconds - HostSeconds) * 1000) + DeliveryAgeMs;
}
