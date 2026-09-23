using System.Text;

namespace Synapsys.Connector.Configuration;

/// <summary>
/// Lector CSV minimo para importar mapeos: comillas dobles al estilo RFC 4180 y separador
/// detectado en la primera linea (<c>;</c>, tab o <c>,</c>), que es como lo exporta Excel
/// segun la configuracion regional.
/// </summary>
public static class SimpleCsv
{
    private static readonly string[] HeaderNames = ["codigo", "código", "code", "testcode", "cod"];

    /// <summary>Filas no vacias con su numero de linea (1-based) y sus campos.</summary>
    public static IReadOnlyList<(int Line, IReadOnlyList<string> Fields)> Parse(string text)
    {
        text = text.TrimStart('﻿');
        var delimiter = DetectDelimiter(text);
        var rows = new List<(int, IReadOnlyList<string>)>();

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowLine = 1;

        void EndRow()
        {
            fields.Add(field.ToString());
            field.Clear();

            if (fields.Any(value => value.Trim().Length > 0))
            {
                // Una descripcion con el separador adentro y sin comillas: el resto va a la descripcion.
                var normalized = fields.Count > 2
                    ? [fields[0], string.Join(delimiter, fields.Skip(1)).TrimEnd(delimiter)]
                    : fields.ToList();
                rows.Add((rowLine, normalized));
            }

            fields = [];
            rowLine = line;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];

            if (inQuotes)
            {
                if (c == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                line++;
                EndRow();
            }
            else if (c != '\r')
            {
                field.Append(c);
            }
        }

        EndRow();
        return rows;
    }

    public static bool LooksLikeHeader(IReadOnlyList<string> fields) =>
        fields.Count > 0 && HeaderNames.Contains(fields[0].Trim(), StringComparer.OrdinalIgnoreCase);

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];

        return first.Contains(';') ? ';' : first.Contains('\t') ? '\t' : ',';
    }
}
