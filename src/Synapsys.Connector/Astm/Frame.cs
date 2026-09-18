using System.Globalization;

namespace Synapsys.Connector.Astm;

/// <summary>Un frame low-level: STX + numero + datos + ETX/ETB + checksum + CR LF.</summary>
public sealed class Frame
{
    public int Number { get; }
    public string Data { get; }
    public bool IsIntermediate { get; }

    public Frame(int number, string data, bool isIntermediate = false)
    {
        Number = number;
        Data = data;
        IsIntermediate = isIntermediate;
    }

    /// <summary>Serializa el frame a bytes listos para el socket.</summary>
    public byte[] ToBytes(bool useChecksum)
    {
        var end = IsIntermediate ? ControlChars.ETB : ControlChars.ETX;

        // El checksum cubre desde el numero de frame hasta ETX/ETB inclusive.
        var payload = $"{(char)('0' + Number)}{Data}{(char)end}";
        var payloadBytes = ControlChars.Encoding.GetBytes(payload);

        var buffer = new List<byte>(payloadBytes.Length + 5) { ControlChars.STX };
        buffer.AddRange(payloadBytes);

        if (useChecksum)
        {
            buffer.AddRange(ControlChars.Encoding.GetBytes(Checksum(payloadBytes)));
        }

        buffer.Add(ControlChars.CR);
        buffer.Add(ControlChars.LF);
        return [.. buffer];
    }

    /// <summary>
    /// Interpreta un frame crudo (desde STX hasta LF). Devuelve <c>null</c> si esta corrupto
    /// o el checksum no valida cuando se lo exige.
    /// </summary>
    public static Frame? Parse(byte[] raw, bool useChecksum)
    {
        if (raw.Length < 3 || raw[0] != ControlChars.STX)
        {
            return null;
        }

        var endIndex = Array.FindIndex(raw, b => b is ControlChars.ETX or ControlChars.ETB);
        if (endIndex < 2)
        {
            return null;
        }

        var isIntermediate = raw[endIndex] == ControlChars.ETB;
        var number = raw[1] - '0';
        var data = ControlChars.Encoding.GetString(raw, 2, endIndex - 2);

        if (useChecksum)
        {
            var payload = raw[1..(endIndex + 1)];
            var expected = Checksum(payload);
            var actual = ReadChecksum(raw, endIndex + 1);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return new Frame(number, data, isIntermediate);
    }

    /// <summary>Siguiente numero de frame: 1..7 y vuelve a 0.</summary>
    public static int NextNumber(int current) => current >= 7 ? 0 : current + 1;

    private static string Checksum(byte[] payload)
    {
        var sum = 0;
        foreach (var b in payload)
        {
            sum += b;
        }

        return (sum & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string ReadChecksum(byte[] raw, int start)
    {
        var chars = new List<char>(2);
        for (var i = start; i < raw.Length && chars.Count < 2; i++)
        {
            if (raw[i] is ControlChars.CR or ControlChars.LF)
            {
                break;
            }

            chars.Add((char)raw[i]);
        }

        return new string([.. chars]);
    }
}
