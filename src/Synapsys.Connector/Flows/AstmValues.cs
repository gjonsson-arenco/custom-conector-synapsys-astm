using Synapsys.Connector.Astm;

namespace Synapsys.Connector.Flows;

/// <summary>Helpers para leer identificadores y armar los registros de respuesta.</summary>
internal static class AstmValues
{
    /// <summary>
    /// Primer componente con contenido de un campo. Sirve para sacar el codigo de barras
    /// de "^BARCODE" o el codigo de prueba de "^^^CODE", sin importar en que componente venga.
    /// </summary>
    public static string FirstMeaningful(string field, char componentSeparator)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return string.Empty;
        }

        foreach (var part in field.Split(componentSeparator))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                return part.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>Cabecera H minima y valida para una respuesta del host.</summary>
    public static AstmRecord Header(AstmSeparators separators) =>
        AstmRecord.Create('H', separators)
            .Set(2, $"{separators.Repeat}{separators.Component}&")
            .Set(5, "Synapsys.Connector")
            .Set(12, "P")
            .Set(13, "E1394-97")
            .Set(14, DateTime.Now.ToString("yyyyMMddHHmmss"));

    /// <summary>Terminador L normal.</summary>
    public static AstmRecord Terminator(AstmSeparators separators) =>
        AstmRecord.Create('L', separators).Set(2, "1").Set(3, "N");
}
