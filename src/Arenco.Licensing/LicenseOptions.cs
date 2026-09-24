namespace Arenco.Licensing;

/// <summary>Seccion <c>Licensing</c> del appsettings.json del conector.</summary>
public sealed class LicenseOptions
{
    public const string SectionName = "Licensing";

    /// <summary>Id del producto que tiene que figurar en la licencia. Lo fija cada conector en codigo.</summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>Archivo de la licencia; relativo al content root.</summary>
    public string Path { get; set; } = "license.lic";

    /// <summary>Cuantos dias antes del vencimiento se empieza a avisar.</summary>
    public int WarningDays { get; set; } = 30;

    /// <summary>Cuantos dias despues del vencimiento el conector sigue funcionando.</summary>
    public int GraceDays { get; set; } = 30;

    /// <summary>Clave publica de verificacion (SubjectPublicKeyInfo en base64). Por defecto, la de Arenco.</summary>
    public string PublicKey { get; set; } = ArencoLicensing.PublicKey;
}
