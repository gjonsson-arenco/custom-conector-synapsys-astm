namespace Synapsys.Connector.Astm;

/// <summary>Caracteres de control ASTM E1381 y el codec Latin1 (1 byte = 1 char) que usa el protocolo.</summary>
public static class ControlChars
{
    public const byte ENQ = 0x05;
    public const byte ACK = 0x06;
    public const byte NAK = 0x15;
    public const byte EOT = 0x04;
    public const byte STX = 0x02;
    public const byte ETX = 0x03;
    public const byte ETB = 0x17;
    public const byte CR = 0x0D;
    public const byte LF = 0x0A;
    public const byte VT = 0x0B;
    public const byte FS = 0x1C;

    /// <summary>ISO-8859-1: mapea cada byte a un char sin perdida, que es como viaja ASTM.</summary>
    public static readonly System.Text.Encoding Encoding = System.Text.Encoding.Latin1;

    public static string Describe(int b) => b switch
    {
        ENQ => "<ENQ>",
        ACK => "<ACK>",
        NAK => "<NAK>",
        EOT => "<EOT>",
        STX => "<STX>",
        ETX => "<ETX>",
        ETB => "<ETB>",
        CR => "<CR>",
        LF => "<LF>",
        VT => "<VT>",
        FS => "<FS>",
        < 0 => "<EOF>",
        _ => ((char)b).ToString()
    };
}
