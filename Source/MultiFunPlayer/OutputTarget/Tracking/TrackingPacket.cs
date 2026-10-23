using System.Buffers.Binary;

namespace MultiFunPlayer.OutputTarget.Tracking;

[Flags]
internal enum TrackingFlags : byte
{
    Reachable = 1, Running = 2, Paused = 4, Mit = 8,
    TargetValid = 16, Waiting = 32, WorkspaceReduced = 64
}

internal sealed record TrackingPacket(
    uint Boot, uint Session, uint Sequence, ulong TimeMicroseconds, byte MotorMask,
    TrackingFlags Flags, byte FeedbackValid, byte CommandValid, byte Axes,
    double[] Target, double[] Command, double[] Feedback,
    ushort[] CommandAgeMs, ushort[] FeedbackAgeMs, double StickMm,
    ushort TargetAgeMs, ushort PeriodMs)
{
    public const string Prefix = "!TRK1,";
    public const ushort Model = 1;
    public const int PayloadLength = 144;
    public const int PacketLength = 148;

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    public static bool TryDecode(string line, out TrackingPacket packet, out string error)
    {
        packet = null;
        error = "跟随快照格式无效";
        line = line.TrimEnd('\r', '\n');
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length != Prefix.Length + 200) return false;
        Span<byte> bytes = stackalloc byte[PacketLength];
        if (!Convert.TryFromBase64String(line[Prefix.Length..], bytes, out var length) || length != PacketLength) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[PayloadLength..]) != Crc32(bytes[..PayloadLength]))
        { error = "跟随快照 CRC 校验失败"; return false; }
        if (bytes[0] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != Model)
        { error = "跟随协议或 CAD 模型版本不匹配"; return false; }
        if (bytes[1] > 127 || bytes[3] > 127 || bytes[4] > 127 || bytes[5] > 63 || bytes[2] > 127 ||
            (bytes[3] & ~bytes[1]) != 0 || (bytes[4] & ~bytes[1]) != 0) return false;
        var period = BinaryPrimitives.ReadUInt16LittleEndian(bytes[142..]);
        var stick = ReadFloat(bytes, 136);
        if (period is < 20 or > 200 || !double.IsFinite(stick) || stick is < 0 or > 300) return false;
        var target = new double[6]; var command = new double[7]; var feedback = new double[7];
        var commandAge = new ushort[7]; var feedbackAge = new ushort[7];
        for (var i = 0; i < 6; i++) target[i] = ReadFloat(bytes, 28 + i * 4);
        for (var i = 0; i < 7; i++)
        {
            command[i] = ReadFloat(bytes, 52 + i * 4); feedback[i] = ReadFloat(bytes, 80 + i * 4);
            commandAge[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(108 + i * 2)..]);
            feedbackAge[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(122 + i * 2)..]);
            if ((bytes[3] & (1 << i)) != 0 && (!double.IsFinite(feedback[i]) || feedbackAge[i] > 100)) return false;
            if ((bytes[4] & (1 << i)) != 0 && (!double.IsFinite(command[i]) || commandAge[i] > 100)) return false;
        }
        var flags = (TrackingFlags)bytes[2];
        var targetAge = BinaryPrimitives.ReadUInt16LittleEndian(bytes[140..]);
        if (flags.HasFlag(TrackingFlags.TargetValid) && (target.Any(x => !double.IsFinite(x)) || targetAge > 100)) return false;
        packet = new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]),
            bytes[1], flags, bytes[3], bytes[4], bytes[5], target, command, feedback, commandAge, feedbackAge, stick, targetAge, period);
        if (packet.Boot == 0 || packet.TimeMicroseconds == 0) { packet = null; return false; }
        error = null; return true;
    }

    private static double ReadFloat(ReadOnlySpan<byte> bytes, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
}
