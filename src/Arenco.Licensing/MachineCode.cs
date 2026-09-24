using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Arenco.Licensing;

/// <summary>
/// Codigo de maquina (request code) que el cliente nos pasa para emitir la licencia. Sale del id
/// que el sistema operativo le asigna a la instalacion (MachineGuid en Windows, machine-id en
/// Linux): no cambia al renombrar el equipo ni al cambiar de placa de red, si al reinstalar el SO.
/// </summary>
public static class MachineCode
{
    // Sin 0/O ni 1/I/L: se dicta por telefono y se copia a mano.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly Lazy<string> Local = new(() => FromId(ReadMachineId()));

    /// <summary>El codigo de esta maquina.</summary>
    public static string Current => Local.Value;

    /// <summary>Formato <c>XXXX-XXXX-XXXX-XXXX</c> derivado del id de la maquina.</summary>
    public static string FromId(string machineId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("arenco-machine|" + machineId.Trim().ToLowerInvariant()));
        var code = new StringBuilder(19);

        for (var i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                code.Append('-');
            }

            code.Append(Alphabet[hash[i] % Alphabet.Length]);
        }

        return code.ToString();
    }

    /// <summary>Normaliza un codigo tipeado (minusculas, espacios, sin guiones) para comparar.</summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string ReadMachineId()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
            {
                return guid;
            }
        }

        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var id = File.ReadAllText(path).Trim();
                if (id.Length > 0)
                {
                    return id;
                }
            }
        }

        // Sin id del SO queda el nombre del equipo: peor, pero no deja al conector sin codigo.
        return Environment.MachineName;
    }
}
