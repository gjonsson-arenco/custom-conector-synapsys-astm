namespace Synapsys.Connector.Configuration;

/// <summary>
/// Catalogo codigo => descripcion persistido en un archivo de settings. Traduce los codigos que
/// manda el equipo (resultados codificados, microorganismos, antibioticos) al texto que se informa
/// al LIS, y concentra las ediciones del front: alta/edicion, baja, borrar todo e importacion CSV.
/// </summary>
/// <remarks>El codigo es la clave y no distingue mayusculas.</remarks>
public sealed class CodeCatalog
{
    private static readonly StringComparer CodeComparer = StringComparer.OrdinalIgnoreCase;

    private readonly SettingsFile<CodeCatalogSettings> _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string> _byCode;

    public CodeCatalog(SettingsFile<CodeCatalogSettings> file)
    {
        _file = file;
        _byCode = Index(file.Current.Mappings);
    }

    public IReadOnlyList<CodeMapping> All => _file.Current.Mappings;

    /// <summary>La descripcion del codigo, o <c>null</c> si no esta en el catalogo.</summary>
    public string? Translate(string code) =>
        Volatile.Read(ref _byCode).GetValueOrDefault(code.Trim());

    /// <summary>
    /// Alta o edicion. <paramref name="originalCode"/> es el codigo que se esta editando (null en un
    /// alta); si difiere del nuevo, la fila se renombra. Devuelve el error si no se puede aplicar.
    /// </summary>
    public Task<string?> UpsertAsync(string? originalCode, CodeMapping mapping, CancellationToken cancellationToken) =>
        MutateAsync(list =>
        {
            var clean = Clean(mapping);
            if (clean is null)
            {
                return "El codigo y la descripcion son obligatorios.";
            }

            var existing = list.FindIndex(row => CodeComparer.Equals(row.Code, clean.Code));
            var original = originalCode is null ? -1 : list.FindIndex(row => CodeComparer.Equals(row.Code, originalCode.Trim()));

            if (originalCode is not null && original < 0)
            {
                return $"No existe el codigo {originalCode}.";
            }

            if (existing >= 0 && existing != original)
            {
                return $"Ya existe el codigo {clean.Code}.";
            }

            if (original >= 0)
            {
                list[original] = clean;
            }
            else
            {
                list.Add(clean);
            }

            return null;
        }, cancellationToken);

    public async Task<bool> DeleteAsync(string code, CancellationToken cancellationToken) =>
        await MutateAsync(list =>
            list.RemoveAll(row => CodeComparer.Equals(row.Code, code.Trim())) > 0 ? null : "no existe",
            cancellationToken) is null;

    public Task ClearAsync(CancellationToken cancellationToken) =>
        MutateAsync(list =>
        {
            list.Clear();
            return null;
        }, cancellationToken);

    /// <summary>
    /// Importa filas <c>codigo;descripcion</c> (o con coma / tab). Con <paramref name="replace"/>
    /// reemplaza todo el catalogo; si no, agrega los codigos nuevos y pisa la descripcion de los que
    /// ya existian. Las filas invalidas se informan y no frenan al resto.
    /// </summary>
    public async Task<CodeCatalogImport> ImportCsvAsync(string csv, bool replace, CancellationToken cancellationToken)
    {
        var rows = SimpleCsv.Parse(csv);
        var errors = new List<string>();
        var added = 0;
        var updated = 0;

        await MutateAsync(list =>
        {
            if (replace)
            {
                list.Clear();
            }

            for (var index = 0; index < rows.Count; index++)
            {
                var (line, fields) = rows[index];

                if (index == 0 && SimpleCsv.LooksLikeHeader(fields))
                {
                    continue;
                }

                var mapping = fields.Count >= 2 ? Clean(new CodeMapping(fields[0], fields[1])) : null;
                if (mapping is null)
                {
                    errors.Add($"Linea {line}: se esperan codigo y descripcion no vacios.");
                    continue;
                }

                var existing = list.FindIndex(row => CodeComparer.Equals(row.Code, mapping.Code));
                if (existing >= 0)
                {
                    list[existing] = mapping;
                    updated++;
                }
                else
                {
                    list.Add(mapping);
                    added++;
                }
            }

            return null;
        }, cancellationToken);

        return new CodeCatalogImport(added, updated, All.Count, errors);
    }

    /// <summary>Aplica un cambio sobre una copia y la guarda solo si no hubo error.</summary>
    private async Task<string?> MutateAsync(Func<List<CodeMapping>, string?> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var list = _file.Current.Mappings.ToList();
            var error = change(list);

            if (error is not null)
            {
                return error;
            }

            await _file.SaveAsync(new CodeCatalogSettings { Mappings = list }, cancellationToken);
            Volatile.Write(ref _byCode, Index(list));
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CodeMapping? Clean(CodeMapping mapping)
    {
        var code = mapping.Code?.Trim();
        var description = mapping.Description?.Trim();

        return string.IsNullOrEmpty(code) || string.IsNullOrEmpty(description)
            ? null
            : new CodeMapping(code, description);
    }

    private static Dictionary<string, string> Index(IEnumerable<CodeMapping> mappings)
    {
        var index = new Dictionary<string, string>(CodeComparer);
        foreach (var mapping in mappings)
        {
            index[mapping.Code.Trim()] = mapping.Description;
        }

        return index;
    }
}

/// <summary>Resumen de una importacion CSV.</summary>
public sealed record CodeCatalogImport(int Added, int Updated, int Total, IReadOnlyList<string> Errors);

/// <summary>
/// Los catalogos del conector, cada uno en su archivo de settings/. El nombre es el que usa la
/// API del front (<c>/api/settings/catalogs/{nombre}</c>).
/// </summary>
public sealed class CodeCatalogs
{
    public CodeCatalogs(CodeCatalog results, CodeCatalog organisms, CodeCatalog antibiotics)
    {
        Results = results;
        Organisms = organisms;
        Antibiotics = antibiotics;
    }

    /// <summary>Resultados codificados (settings/result-mappings.json). Tambien traduce el estado de un cultivo.</summary>
    public CodeCatalog Results { get; }

    /// <summary>Microorganismos (settings/organisms.json).</summary>
    public CodeCatalog Organisms { get; }

    /// <summary>Antibioticos (settings/antibiotics.json).</summary>
    public CodeCatalog Antibiotics { get; }

    public CodeCatalog? Find(string name) => name.ToLowerInvariant() switch
    {
        "results" => Results,
        "organisms" => Organisms,
        "antibiotics" => Antibiotics,
        _ => null
    };
}
