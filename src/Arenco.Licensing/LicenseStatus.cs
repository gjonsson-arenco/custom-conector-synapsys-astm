namespace Arenco.Licensing;

/// <summary>En que situacion esta la licencia del conector.</summary>
public enum LicenseState
{
    /// <summary>No hay licencia instalada.</summary>
    Missing,

    /// <summary>La clave esta mal formada o la firma no es valida.</summary>
    Invalid,

    /// <summary>La licencia es de otro conector.</summary>
    WrongProduct,

    /// <summary>La licencia es de otra maquina.</summary>
    WrongMachine,

    /// <summary>Vigente.</summary>
    Valid,

    /// <summary>Vigente, pero vence dentro de la ventana de aviso.</summary>
    ExpiringSoon,

    /// <summary>Vencida, dentro de la tolerancia: el conector sigue funcionando.</summary>
    Grace,

    /// <summary>Vencida y fuera de la tolerancia.</summary>
    Expired
}

/// <summary>Foto de la licencia para el conector y el front.</summary>
/// <param name="State">Situacion de la licencia.</param>
/// <param name="IsUsable">Si el conector puede levantar la conexion (vigente o en tolerancia).</param>
/// <param name="Message">Explicacion para mostrarle al usuario.</param>
/// <param name="Product">Producto que este conector espera en la licencia.</param>
/// <param name="MachineCode">Codigo de esta maquina (el que hay que mandar para pedir la licencia).</param>
/// <param name="License">Lo que dice la licencia instalada, si se pudo leer.</param>
/// <param name="GraceUntil">Ultimo dia de la tolerancia.</param>
/// <param name="DaysRemaining">Dias hasta el vencimiento; negativo si ya vencio.</param>
public sealed record LicenseStatus(
    LicenseState State,
    bool IsUsable,
    string Message,
    string Product,
    string MachineCode,
    LicensePayload? License,
    DateOnly? GraceUntil,
    int? DaysRemaining)
{
    /// <summary>Si hay que avisar: vence pronto, esta en tolerancia o directamente no sirve.</summary>
    public bool NeedsAttention => State != LicenseState.Valid;
}
