namespace Synapsys.Connector.Astm;

/// <summary>
/// Un registro ASTM (H, P, O, R, Q, C, L). Guarda los campos ya separados, incluido el tipo
/// en la posicion 1, y ofrece acceso 1-based tal como los numera el estandar E1394.
/// </summary>
public sealed class AstmRecord
{
    private readonly List<string> _fields;

    public AstmSeparators Separators { get; }

    /// <summary>Letra de tipo de registro (H, P, O, R, Q, C, L, ...).</summary>
    public char Type => _fields.Count > 0 && _fields[0].Length > 0 ? _fields[0][0] : '?';

    private AstmRecord(List<string> fields, AstmSeparators separators)
    {
        _fields = fields;
        Separators = separators;
    }

    /// <summary>Parte una linea de registro (sin CR) en campos.</summary>
    public static AstmRecord Parse(string line, AstmSeparators separators)
    {
        var fields = line.Split(separators.Field).ToList();
        return new AstmRecord(fields, separators);
    }

    /// <summary>Crea un registro vacio del tipo indicado, con el tipo ya puesto en el campo 1.</summary>
    public static AstmRecord Create(char type, AstmSeparators separators) =>
        new([type.ToString()], separators);

    /// <summary>Campo por posicion 1-based (Field(1) es el tipo). Cadena vacia si no existe.</summary>
    public string Field(int position)
    {
        var index = position - 1;
        return index >= 0 && index < _fields.Count ? _fields[index] : string.Empty;
    }

    /// <summary>Componente 1-based dentro de un campo (separado por ^).</summary>
    public string Component(int field, int component)
    {
        var parts = Field(field).Split(Separators.Component);
        var index = component - 1;
        return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
    }

    /// <summary>Fija un campo 1-based, rellenando con vacios los intermedios.</summary>
    public AstmRecord Set(int position, string value)
    {
        while (_fields.Count < position)
        {
            _fields.Add(string.Empty);
        }

        _fields[position - 1] = value;
        return this;
    }

    public override string ToString() => string.Join(Separators.Field, _fields);
}

/// <summary>Separadores de la transmision, tomados de configuracion.</summary>
public sealed record AstmSeparators(char Field, char Component, char Repeat)
{
    public static AstmSeparators Default { get; } = new('|', '^', '\\');
}
