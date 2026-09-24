using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arenco.Licensing;

/// <summary>
/// Formato de la clave: <c>base64url(json del payload).base64url(firma)</c>. La firma es ECDSA
/// P-256/SHA-256 sobre el primer segmento. El conector solo tiene la clave publica: puede
/// verificar una licencia pero no fabricarla. La privada vive unicamente en el generador.
/// </summary>
public static class LicenseKey
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Firma el payload con la clave privada y devuelve la clave de licencia.</summary>
    public static string Sign(LicensePayload payload, ECDsa privateKey)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256);
        return $"{body}.{Base64Url(signature)}";
    }

    /// <summary>
    /// Verifica la firma con la clave publica y lee el payload. <c>false</c> si la clave esta
    /// mal formada, fue alterada o la firmo otra clave privada.
    /// </summary>
    public static bool TryVerify(string key, ECDsa publicKey, out LicensePayload? payload)
    {
        payload = null;

        // Pegada desde un mail o un .lic puede venir partida en lineas o con espacios.
        var compact = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var parts = compact.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var signature = FromBase64Url(parts[1]);
            if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0]), signature, HashAlgorithmName.SHA256))
            {
                return false;
            }

            payload = JsonSerializer.Deserialize<LicensePayload>(FromBase64Url(parts[0]), Json);
            return payload is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Lee el payload sin verificar la firma (para inspeccionar una clave).</summary>
    public static LicensePayload? Peek(string key)
    {
        var body = new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray()).Split('.')[0];
        try
        {
            return JsonSerializer.Deserialize<LicensePayload>(FromBase64Url(body), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Carga una clave publica SubjectPublicKeyInfo en base64.</summary>
    public static ECDsa ImportPublicKey(string base64)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
        return key;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
