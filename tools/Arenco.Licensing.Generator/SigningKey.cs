using System.Security.Cryptography;

namespace Arenco.Licensing.Generator;

/// <summary>
/// La clave privada con la que se firman las licencias. Vive fuera del repo (por defecto en
/// <c>%APPDATA%\Arenco\Licensing\signing-key.pem</c>). Quien la tiene puede emitir licencias para
/// cualquier conector: no se versiona, no se manda por mail y se respalda en un lugar seguro.
/// </summary>
internal static class SigningKey
{
    public const string EnvironmentVariable = "ARENCO_LICENSE_SIGNING_KEY";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } path
            ? path
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Arenco", "Licensing", "signing-key.pem");

    public static ECDsa Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No se encontro la clave privada en {path}. Copiala desde el respaldo, indicala con --key o con la variable {EnvironmentVariable}. " +
                "Solo si es la primera vez, creala con 'keygen'.");
        }

        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path));
        return key;
    }

    public static ECDsa Create(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem() + Environment.NewLine);
        return key;
    }

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
}
