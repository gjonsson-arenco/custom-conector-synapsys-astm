# Arenco License Generator

Consola para emitir licencias de los conectores custom de Arenco (Synapsys y los que vengan).
Uso interno: solo lo corre personal autorizado de Arenco.

## Cómo se licencia una instalación

1. En el conector del cliente: pantalla **Licencia** → copiar el **código de máquina**
   (`XXXX-XXXX-XXXX-XXXX`).
2. Acá: correr el generador, cargar producto, cliente, código de máquina y vencimiento.
3. Mandar la clave (o el `.lic`) al cliente. Se pega en la pantalla **Licencia** del conector, o se
   copia el `.lic` como `license.lic` junto al ejecutable. El puerto se abre solo.

Para renovar se repite lo mismo con la nueva fecha: la clave nueva reemplaza a la anterior.

## Uso

```bash
# Interactivo
dotnet run --project tools/Arenco.Licensing.Generator

# En una línea (scripts)
dotnet run --project tools/Arenco.Licensing.Generator -- issue --product synapsys-connector --customer "Hospital X" --machine ABCD-EFGH-JKMN-PQRS --expires 2027-09-30

# Ver qué dice una clave y si la firma es válida
dotnet run --project tools/Arenco.Licensing.Generator -- inspect licencias/synapsys-connector_hospital-x_20270930.lic
```

Fechas: `yyyy-MM-dd`, `dd/MM/yyyy` o relativas (`+90d`, `+12m`, `+1a`). Por defecto, un año.

Cada emisión:

- muestra la clave y la copia al portapapeles;
- la guarda como `licencias/<producto>_<cliente>_<vencimiento>.lic` en el directorio actual;
- la anota en `licencias/emitidas.csv` (registro de lo emitido).

## La clave privada

Las licencias se firman con ECDSA P-256. La clave **privada** está en
`%APPDATA%\Arenco\Licensing\signing-key.pem` (o donde indique `--key` o la variable
`ARENCO_LICENSE_SIGNING_KEY`). La **pública** está embebida en `Arenco.Licensing`
(`ArencoLicensing.PublicKey`) y es con la que los conectores verifican.

- **Respaldarla** en un lugar seguro (gestor de secretos, bóveda). Si se pierde no se pueden emitir
  ni renovar licencias: habría que generar otra, cambiar la pública en `Arenco.Licensing`, publicar
  todos los conectores y reemitir todas las licencias.
- **No versionarla ni mandarla por mail.** Quien la tenga puede emitir licencias para cualquier
  conector. El `.gitignore` ignora `*.pem`.
- Otra persona que emita licencias necesita una copia del mismo `.pem`.
- `public-key` muestra la pública de la privada en uso; el generador avisa si no coincide con la
  embebida en `Arenco.Licensing`.
- `keygen` crea una privada nueva. Se usó una sola vez; no volver a correrlo salvo que se decida
  rotar la clave (con `--force`, y con todo lo que implica).

## Agregar un producto

Sumar el id a `ArencoLicensing.Products` y `KnownProducts` (`src/Arenco.Licensing/ArencoLicensing.cs`);
el conector nuevo pasa ese mismo id a `AddArencoLicensing`.

## Ejecutable standalone

```bash
dotnet publish tools/Arenco.Licensing.Generator -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
