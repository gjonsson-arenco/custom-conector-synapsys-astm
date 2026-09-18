namespace Synapsys.Connector.Lis;

/// <summary>Una fila del mapeo de codigos entre el instrumento y el LIS, para mostrar en el front.</summary>
public sealed record TestMapping(
    string TestCode,
    string? IncomingCode,
    string? OutgoingCode,
    double Factor,
    string? Name);

/// <summary>Origen del mapeo de codigos que ve el front.</summary>
public interface IMappingCatalog
{
    Task<IReadOnlyList<TestMapping>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Mapeo mockeado para el front mientras se cablea la lectura real desde labcore-api
/// (GET /instruments/{id}/tests). Al reemplazar por la implementacion real solo cambia el registro
/// en el contenedor; el endpoint y el front no se enteran.
/// </summary>
public sealed class MockMappingCatalog : IMappingCatalog
{
    private static readonly IReadOnlyList<TestMapping> Sample =
    [
        new("GLU", "301", "GLU", 1d, "Glucosa"),
        new("UREA", "302", "URE", 1d, "Urea"),
        new("CREA", "303", "CRE", 0.0113d, "Creatinina"),
        new("CHOL", "304", "COL", 1d, "Colesterol"),
        new("TRIG", "305", "TRG", 1d, "Trigliceridos"),
        new("NA", "306", "NA", 1d, "Sodio"),
        new("K", "307", "K", 1d, "Potasio"),
        new("CL", "308", "CL", 1d, "Cloro")
    ];

    public Task<IReadOnlyList<TestMapping>> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Sample);
}
