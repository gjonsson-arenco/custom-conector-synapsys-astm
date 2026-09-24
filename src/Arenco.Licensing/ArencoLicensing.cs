namespace Arenco.Licensing;

/// <summary>Constantes compartidas entre los conectores y el generador de licencias.</summary>
public static class ArencoLicensing
{
    /// <summary>
    /// Clave publica con la que los conectores verifican las licencias (SubjectPublicKeyInfo,
    /// ECDSA P-256). Su privada es la del generador; si se reemplaza, hay que reemitir todo.
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEbdk3Ix5r6ufjU+NegquA8lS2Bf3yY50nUqZrk3baMUDikn4/dvW0K4/jhmtQleqBrTa+kcrW0m7hNGbycudvsg==";

    /// <summary>Productos licenciables. Un conector nuevo se agrega aca con el id que pasa a AddArencoLicensing.</summary>
    public static readonly string[] KnownProducts =
    [
        Products.SynapsysConnector
    ];

    public static class Products
    {
        public const string SynapsysConnector = "synapsys-connector";
    }
}
