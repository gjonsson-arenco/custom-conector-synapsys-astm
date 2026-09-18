using System.Text;
using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Monitoring;

/// <summary>Un fragmento de comunicacion ASTM cruda, en una direccion, para el monitor realtime.</summary>
public sealed record CommsMessage(string Direction, string Hex, string Text, DateTimeOffset Timestamp)
{
    /// <summary>Bytes recibidos desde Synapsys.</summary>
    public static CommsMessage Rx(ReadOnlySpan<byte> bytes) => Create("RX", bytes);

    /// <summary>Bytes enviados hacia Synapsys.</summary>
    public static CommsMessage Tx(ReadOnlySpan<byte> bytes) => Create("TX", bytes);

    private static CommsMessage Create(string direction, ReadOnlySpan<byte> bytes) =>
        new(direction, Convert.ToHexString(bytes), Describe(bytes), DateTimeOffset.UtcNow);

    /// <summary>Version legible: imprimibles como texto, control como tokens (&lt;STX&gt;, &lt;CR&gt;, ...).</summary>
    private static string Describe(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? ((char)b).ToString() : ControlChars.Describe(b));
        }

        return sb.ToString();
    }
}
