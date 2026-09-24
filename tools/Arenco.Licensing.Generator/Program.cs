using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Arenco.Licensing;
using Arenco.Licensing.Generator;
using TextCopy;

// Generador de licencias de los conectores custom de Arenco.
//
//   Arenco.LicenseGenerator                      modo interactivo (emitir una licencia)
//   Arenco.LicenseGenerator issue --product synapsys-connector --customer "HUA" --machine XXXX-XXXX-XXXX-XXXX --expires 2027-09-30
//   Arenco.LicenseGenerator inspect <clave | archivo.lic>
//   Arenco.LicenseGenerator public-key           clave publica de la clave privada en uso
//   Arenco.LicenseGenerator keygen [--force]     crea la clave privada (una unica vez)
//
// Opcion comun: --key <ruta de la clave privada> (por defecto %APPDATA%\Arenco\Licensing\signing-key.pem).

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "interactive";
var options = ParseOptions(args.SkipWhile(a => !a.StartsWith("--")).ToArray());
var keyPath = options.GetValueOrDefault("key") ?? SigningKey.DefaultPath;

try
{
    return command switch
    {
        "interactive" => Interactive(keyPath),
        "issue" => Issue(keyPath, options),
        "inspect" => Inspect(args.Length > 1 ? args[1] : null),
        "public-key" => PublicKey(keyPath),
        "keygen" => KeyGen(keyPath, options.ContainsKey("force")),
        "help" or "-h" or "--help" => Help(),
        _ => Fail($"Comando desconocido: {command}. Usa 'help'.")
    };
}
catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or FormatException)
{
    return Fail(ex.Message);
}

static int Interactive(string keyPath)
{
    Banner();
    using var signingKey = SigningKey.Load(keyPath);
    WarnIfNotArencoKey(signingKey);

    while (true)
    {
        Console.WriteLine();
        var product = Ask("Producto", ArencoLicensing.KnownProducts[0], hint: string.Join(", ", ArencoLicensing.KnownProducts));
        var customer = Ask("Cliente / instalacion");
        var machine = AskMachineCode();
        var expires = AskExpiration();

        if (expires < Today())
        {
            Warn("La fecha ya paso: la licencia sale vencida.");
        }

        Emit(signingKey, new LicensePayload(LicensePayload.CurrentVersion, product, customer, machine, Today(), expires));

        Console.WriteLine();
        if (!Ask("Emitir otra? (s/N)", "n").StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }
}

static int Issue(string keyPath, Dictionary<string, string> options)
{
    string Required(string name) =>
        options.GetValueOrDefault(name) is { Length: > 0 } value ? value : throw new ArgumentException($"Falta --{name}.");

    using var signingKey = SigningKey.Load(keyPath);
    WarnIfNotArencoKey(signingKey);

    var machine = FormatMachineCode(Required("machine")) ?? throw new ArgumentException("--machine no es un codigo de maquina valido.");
    var expires = ParseExpiration(Required("expires")) ?? throw new ArgumentException("--expires no es una fecha valida.");

    Emit(signingKey, new LicensePayload(
        LicensePayload.CurrentVersion, Required("product"), Required("customer"), machine, Today(), expires));
    return 0;
}

static int Inspect(string? input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return Fail("Uso: inspect <clave | archivo.lic>");
    }

    var key = File.Exists(input) ? File.ReadAllText(input) : input;
    var payload = LicenseKey.Peek(key);
    if (payload is null)
    {
        return Fail("No es una clave de licencia.");
    }

    using var publicKey = LicenseKey.ImportPublicKey(ArencoLicensing.PublicKey);
    var signed = LicenseKey.TryVerify(key, publicKey, out _);

    Print(payload);
    Console.Write("  Firma           : ");
    Write(signed ? "valida (Arenco)" : "INVALIDA - la clave fue alterada o no la firmo Arenco", signed ? ConsoleColor.Green : ConsoleColor.Red);
    Console.WriteLine();
    return signed ? 0 : 1;
}

static int PublicKey(string keyPath)
{
    using var signingKey = SigningKey.Load(keyPath);
    Console.WriteLine(SigningKey.PublicKeyOf(signingKey));
    WarnIfNotArencoKey(signingKey);
    return 0;
}

static int KeyGen(string keyPath, bool force)
{
    if (File.Exists(keyPath) && !force)
    {
        return Fail($"Ya existe una clave privada en {keyPath}. Reemplazarla invalida todas las licencias emitidas; si es lo que queres, usa --force.");
    }

    using var key = SigningKey.Create(keyPath);
    Write($"Clave privada creada en {keyPath}", ConsoleColor.Green);
    Console.WriteLine();
    Console.WriteLine("Respaldala en un lugar seguro: sin ella no se pueden emitir ni renovar licencias.");
    Console.WriteLine();
    Console.WriteLine("Clave publica (va en ArencoLicensing.PublicKey de Arenco.Licensing):");
    Write(SigningKey.PublicKeyOf(key), ConsoleColor.Cyan);
    Console.WriteLine();
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        Arenco License Generator

          (sin argumentos)   Modo interactivo: emite una licencia.
          issue              --product <id> --customer <nombre> --machine <codigo> --expires <fecha>
          inspect <clave|archivo.lic>
          public-key         Muestra la clave publica de la clave privada en uso.
          keygen [--force]   Crea la clave privada (una unica vez).

          --key <ruta>       Clave privada (por defecto %APPDATA%\Arenco\Licensing\signing-key.pem
                             o la variable ARENCO_LICENSE_SIGNING_KEY).

        Fechas: yyyy-MM-dd, dd/MM/yyyy o relativas (+90d, +12m, +1a).
        """);
    return 0;
}

// Firma, muestra, copia al portapapeles y deja el .lic y el registro en licencias/.
static void Emit(System.Security.Cryptography.ECDsa signingKey, LicensePayload payload)
{
    var key = LicenseKey.Sign(payload, signingKey);

    Console.WriteLine();
    Write("================ LICENCIA GENERADA ================", ConsoleColor.Green);
    Console.WriteLine();
    Print(payload);
    Console.WriteLine();
    Write("  CLAVE:", ConsoleColor.Green);
    Console.WriteLine();
    Console.WriteLine($"  {key}");
    Console.WriteLine();

    var directory = Path.Combine(Environment.CurrentDirectory, "licencias");
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, $"{payload.Product}_{Slug(payload.Customer)}_{payload.ExpiresAt:yyyyMMdd}.lic");
    File.WriteAllText(file, key + Environment.NewLine);

    var log = Path.Combine(directory, "emitidas.csv");
    if (!File.Exists(log))
    {
        File.WriteAllText(log, "emitida;producto;cliente;maquina;vence;archivo" + Environment.NewLine);
    }

    File.AppendAllText(log, string.Join(';',
        payload.IssuedAt.ToString("yyyy-MM-dd"), payload.Product, payload.Customer.Replace(';', ','),
        payload.MachineCode, payload.ExpiresAt.ToString("yyyy-MM-dd"), Path.GetFileName(file)) + Environment.NewLine);

    Console.WriteLine($"  Archivo         : {file}");

    try
    {
        ClipboardService.SetText(key);
        Console.WriteLine("  Copiada al portapapeles.");
    }
    catch (Exception)
    {
        // Sin portapapeles (por ejemplo, por SSH) queda la clave en pantalla y en el archivo.
    }
}

static void Print(LicensePayload payload)
{
    var days = payload.ExpiresAt.DayNumber - Today().DayNumber;
    Console.WriteLine($"  Producto        : {payload.Product}");
    Console.WriteLine($"  Cliente         : {payload.Customer}");
    Console.WriteLine($"  Codigo maquina  : {payload.MachineCode}");
    Console.WriteLine($"  Emitida         : {payload.IssuedAt:yyyy-MM-dd}");
    Console.WriteLine($"  Vence           : {payload.ExpiresAt:yyyy-MM-dd} ({(days >= 0 ? $"{days} dias" : $"vencida hace {-days} dias")})");
}

static string Ask(string label, string? defaultValue = null, string? hint = null)
{
    while (true)
    {
        Write($"{label}{(hint is null ? "" : $" ({hint})")}{(defaultValue is null ? "" : $" [{defaultValue}]")}: ", ConsoleColor.Yellow);
        var value = Console.ReadLine()?.Trim();

        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (defaultValue is not null)
        {
            return defaultValue;
        }

        Warn("No puede quedar vacio.");
    }
}

static string AskMachineCode()
{
    while (true)
    {
        if (FormatMachineCode(Ask("Codigo de maquina (request code)")) is { } code)
        {
            return code;
        }

        Warn("Tiene que tener 16 caracteres: XXXX-XXXX-XXXX-XXXX (se ve en el conector, pantalla Licencia).");
    }
}

static DateOnly AskExpiration()
{
    var suggested = Today().AddYears(1).ToString("yyyy-MM-dd");
    while (true)
    {
        if (ParseExpiration(Ask("Vence", suggested, hint: "yyyy-MM-dd, +90d, +12m, +1a")) is { } date)
        {
            return date;
        }

        Warn("Fecha invalida.");
    }
}

static string? FormatMachineCode(string input)
{
    var code = MachineCode.Normalize(input);
    return code.Length == 16 ? string.Join('-', code.Chunk(4).Select(c => new string(c))) : null;
}

static DateOnly? ParseExpiration(string input)
{
    var relative = Regex.Match(input.Trim().ToLowerInvariant(), @"^\+(\d+)\s*([dma])$");
    if (relative.Success)
    {
        var n = int.Parse(relative.Groups[1].Value);
        return relative.Groups[2].Value switch
        {
            "d" => Today().AddDays(n),
            "m" => Today().AddMonths(n),
            _ => Today().AddYears(n)
        };
    }

    return DateOnly.TryParseExact(input.Trim(), ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date
        : null;
}

static void WarnIfNotArencoKey(System.Security.Cryptography.ECDsa signingKey)
{
    if (SigningKey.PublicKeyOf(signingKey) != ArencoLicensing.PublicKey)
    {
        Warn("Esta clave privada NO corresponde a la clave publica embebida en Arenco.Licensing: los conectores van a rechazar lo que firmes.");
    }
}

static string Slug(string text)
{
    var slug = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    return slug.Length == 0 ? "cliente" : slug;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
        {
            continue;
        }

        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        options[args[i][2..]] = hasValue ? args[++i] : "true";
    }

    return options;
}

static void Banner()
{
    Write("================================================", ConsoleColor.Cyan);
    Console.WriteLine();
    Write("   ARENCO LICENSE GENERATOR - conectores custom", ConsoleColor.Cyan);
    Console.WriteLine();
    Write("================================================", ConsoleColor.Cyan);
    Console.WriteLine();
}

static void Write(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ResetColor();
}

static void Warn(string message)
{
    Write($"  ! {message}", ConsoleColor.Red);
    Console.WriteLine();
}

static int Fail(string message)
{
    Write(message, ConsoleColor.Red);
    Console.WriteLine();
    return 1;
}

static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);
