using System.Globalization;
using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal static class TCodeBufferedProtocol
{
    public const ulong MaxInteger = 9007199254740991;
    public static uint Crc32(string text)
    {
        var crc = uint.MaxValue;
        foreach (var c in text)
        {
            crc ^= c;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    public static string Encode(IEnumerable<ulong> fields)
    {
        var values = fields.ToArray();
        if (values.Any(x => x > MaxInteger))
            throw new ArgumentOutOfRangeException(nameof(fields));
        var body = "!4," + string.Join(',', values.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var wire = body + "*" + Crc32(body).ToString(CultureInfo.InvariantCulture);
        if (wire.Length > 255)
            throw new InvalidDataException("TCode 0.4 frame exceeds 255 bytes.");
        return wire + "\n";
    }

    public static bool TryDecode(string text, out ulong[] fields)
    {
        fields = null;
        text = text.TrimEnd('\r', '\n');
        if (text.Length > 255 || !text.StartsWith("!4,", StringComparison.Ordinal))
            return false;
        var star = text.IndexOf('*');
        if (star < 0 || !Integer(text[(star + 1)..], out var crc) || crc != Crc32(text[..star]))
            return false;
        var parts = text[3..star].Split(',');
        if (parts.Length is < 4 or > 24)
            return false;
        var result = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!Integer(parts[i], out result[i]))
                return false;
        fields = result;
        return true;
    }

    private static bool Integer(string text, out ulong value)
    {
        value = 0;
        return text.Length is > 0 and <= 16 && text.All(c => c is >= '0' and <= '9')
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= MaxInteger;
    }
}

internal sealed class TCodeBufferedException(ulong code, ulong operation, ulong state)
    : IOException($"TCode 0.4 rejected operation {operation}: error {code}, device state {state}.")
{
    public ulong Code { get; } = code;
}
