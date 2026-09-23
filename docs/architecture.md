# Synapsys Connector — Arquitectura

Conector entre **Synapsys** (analizador que habla ASTM por socket) y **Labcore**
(LIS, se accede por `labcore-api` HTTP). Synapsys es **mandante**: el conector escucha y
reacciona; solo emite como respuesta a una consulta.

## Vista general

```mermaid
flowchart LR
    S[Synapsys] <-->|ASTM / socket| T[Transport]
    T --> C[Canal ASTM low/high]
    C --> W[ConnectorController]
    W --> R[TransmissionRouter]
    R --> Q[QueryFlow]
    R --> RF[ResultsFlow]
    Q --> G[ILisGateway]
    RF --> G
    G -->|HTTP| L[labcore-api]
    W -.eventos.-> M[ConnectorMonitor]
    C -.bytes.-> M
    M -->|WebSocket| F[Front React]
    W -->|REST| F
```

## Flujos

### Host query (Synapsys pregunta qué hacer con un tubo)

```mermaid
sequenceDiagram
    participant S as Synapsys
    participant C as Connector
    participant L as labcore-api
    S->>C: ENQ / Q|1|^barcode
    C->>L: GET /samples/{barcode}
    L-->>C: pruebas Pending
    C->>C: TestCode → OutgoingCode (mapeo)
    C->>S: H/P/O(pruebas)/L  (o H/L si no hay órdenes / 404)
```

### Resultados (Synapsys envía resultados)

```mermaid
sequenceDiagram
    participant S as Synapsys
    participant C as Connector
    participant L as labcore-api
    S->>C: H/P/O/R(IncomingCode, valor, flags)/L
    C->>C: IncomingCode → TestCode; valor codificado → descripción (o × factor)
    C->>L: POST /samples/{barcode}/results (status 2)
```

## Estructura y responsabilidades

| Carpeta / archivo | Responsabilidad |
| --- | --- |
| `Program.cs` | Composición de dependencias (DI), configuración web (Kestrel) y arranque del host. |
| `Runtime/ConnectorController.cs` | Ciclo de vida del puerto ASTM: abrir/cerrar/reiniciar y estado. Corre el bucle de sesiones (recibir → rutear → responder) y alimenta el monitor. |
| `Runtime/ConnectorBootstrap.cs` | Hosted service: abre el puerto al iniciar el servicio y lo cierra al apagarse. |
| `Runtime/TransportFactory.cs` | Crea un transporte nuevo (Server/Client) en cada apertura del puerto. |
| **`Monitoring/`** | Observabilidad realtime que consume el front. |
| `Monitoring/ConnectorMonitor.cs` | Bus en memoria que difunde comunicación cruda y eventos a los WebSockets suscritos. |
| `Monitoring/MonitoredConnection.cs` | Decora la conexión ASTM para publicar los bytes RX/TX al monitor. |
| **`Web/`** | Plano de control HTTP. |
| `Web/ConnectorEndpoints.cs` | REST (`/api/status`, `/api/port/*`) y WebSockets (`/ws/comms`, `/ws/events`). |
| `Web/SettingsEndpoints.cs` | REST de settings (`/api/settings/*`): instrumento, comunicacion, mapeo de tests y catálogos. |
| **`Configuration/`** | Opciones de despliegue (appsettings) y settings editables (settings/*.json). |
| `Configuration/SettingsFile.cs` | Un archivo JSON de settings: lectura al arrancar, guardado atomico y aviso de cambio. |
| `Configuration/ConnectorSettings.cs` | Modelos y validacion de `instrument.json`, `communication.json` y de los catálogos. |
| `Configuration/CodeCatalog.cs` | Catálogo código => descripción (resultados, microorganismos, antibióticos): traducción, edición e importación CSV. |
| **`web/`** (raíz) | Front React (Vite + TS). Se compila a `wwwroot` y Kestrel lo sirve como SPA. |
| **`Transport/`** | Establecimiento del socket, independiente del protocolo. |
| `Transport/ITransport.cs` | Contrato que entrega conexiones (`IAstmConnection`) una por vez; `TcpConnection` ve el socket como flujo de bytes. |
| `Transport/SocketTransports.cs` | `SocketServerTransport` (escucha) y `SocketClientTransport` (conecta + reconecta). El modo se elige por config. |
| **`Astm/`** | Protocolo ASTM puro, sin saber de LIS ni de negocio. |
| `Astm/ControlChars.cs` | Bytes de control (ENQ/ACK/NAK/EOT/STX/ETX/…) y codec Latin1. |
| `Astm/Frame.cs` | Arma/parsea un frame low-level con checksum y numeración 0–7. |
| `Astm/AstmRecord.cs` | Un registro (H/P/O/R/Q/C/L): parseo y armado por campos/componentes. |
| `Astm/IAstmChannel.cs` | Contrato de conversación: `ReceiveAsync` / `SendAsync` en términos de registros. |
| `Astm/LowLevelChannel.cs` | ENQ/ACK/NAK/EOT + frames con checksum. Ante colisión de ENQ **cede** (Synapsys es mandante). |
| `Astm/HighLevelChannel.cs` | Transmisión completa envuelta en `VT … FS CR`, sin handshake. |
| `Astm/AstmChannelFactory.cs` | Construye el canal (low/high) según configuración y expone los separadores. |
| **`Flows/`** | Orquestación de negocio sobre los registros. |
| `Flows/TransmissionRouter.cs` | Decide el flow: hay `Q` → consulta; hay `R` → resultados. |
| `Flows/QueryFlow.cs` | Extrae el código de barras del `Q`, consulta el LIS y arma la respuesta `H/P/O/L` (o query negativa). |
| `Flows/ResultsFlow.cs` | Agrupa los `R` por tubo (según el `O` previo) y los guarda en el LIS. |
| `Flows/AstmValues.cs` | Helpers para leer identificadores y armar los registros de respuesta. |
| **`Lis/`** | Frontera con el LIS (mapeo + HTTP encapsulados). |
| `Lis/ILisGateway.cs` | Contrato en términos de dominio: `GetOrdersAsync` / `SaveResultsAsync`. Los flows no saben de HTTP ni de mapeo. |
| `Lis/LabcoreGateway.cs` | Implementación contra `labcore-api`: cachea el mapeo de códigos (`GET /instruments/{id}/tests`), traduce resultados codificados o aplica factor, y traduce en ambos sentidos. |
| `Lis/LabcoreTestMappings.cs` | Lectura y edición del mapeo de pruebas del LIS vía `labcore-api` (`GET/POST/PUT/DELETE /instruments/{id}/tests`). Cada escritura invalida la caché del gateway. |

## Decisiones de diseño

- **Synapsys es mandante.** El conector queda a la escucha; solo inicia un envío para
  responder una consulta y, si hay colisión de `ENQ`, cede.
- **Nivel ASTM configurable.** `LowLevel` (framing + checksum + handshake) o `HighLevel`
  (mensaje completo). El resto del código no cambia: ambos implementan `IAstmChannel`.
- **Transporte configurable.** `Server` o `Client` detrás de `ITransport`; el protocolo no
  se entera de quién abrió el socket.
- **Mapeo y consumo de API aislados.** Viven solo en `LabcoreGateway`, detrás de
  `ILisGateway`. Los flows ASTM trabajan con código de barras y códigos de instrumento.
- **Códigos de prueba mapeados en el LIS.** El mapeo `IncomingCode ↔ TestCode ↔ OutgoingCode`
  y el factor de conversión se traen de `labcore-api` y se cachean.

## Configuración

Se separa lo que es **de despliegue** (lo toca quien instala) de lo que es **operativo**
(se edita desde el front). Cada archivo operativo es un tema con su propio ciclo de vida.

| Archivo | Contenido | Quién lo edita | Cuándo aplica |
| --- | --- | --- | --- |
| `appsettings.json` | `Urls`, `Labcore` (`BaseUrl`, `ApiKey`, `UserId`, `MappingRefreshMinutes`), `Settings:Directory`, `Logging` | Instalador | Al reiniciar el servicio |
| `settings/instrument.json` | `instrumentId` (Analizadores.a_id) | Front › Instrumento | En el acto (invalida la caché de mapeo) |
| `settings/communication.json` | `transport` (`mode`, `host`, `port`, `reconnectSeconds`) y `astm` (`level`, checksum, timeouts, separadores) | Front › Comunicación | Al guardar se reinicia el puerto si estaba abierto |
| `settings/result-mappings.json` | `mappings: [{ code, description }]` | Front › Mapeo de resultados (alta, edición, baja, borrar todo, CSV) | En el acto |
| `settings/organisms.json` · `antibiotics.json` | `mappings: [{ code, description }]` | Front › Microbiología (igual que el mapeo de resultados) | En el acto |
| *(LIS)* `AnalizadoresDet` | Mapeo de pruebas: entrante/saliente, prueba del LIS, factor, sufijo… | Front › Mapeo de tests, vía `labcore-api` | En el acto (invalida la caché de mapeo) |

- `settings/` vive junto a `appsettings.json` (o donde diga `Settings:Directory`). Está fuera de
  git y del publish: es de cada instalación, un redeploy no la pisa.
- Si un archivo no existe se crea al arrancar. `instrument.json` y `communication.json` toman el
  valor inicial de las secciones viejas `Labcore:InstrumentId`, `Transport` y `Astm` si todavía
  están en `appsettings.json` (migración sin pasos manuales); `result-mappings.json` arranca con el
  mapeo del adapter de Epicenter (`Configuration/Defaults/result-mappings.json`).
- El guardado es atómico (temporal + reemplazo): un corte a mitad no deja un JSON roto.
- `instrumentId` es obligatorio: sin él no se consulta el mapeo ni se cargan resultados.

## Plano de control y front

El servicio ahora es un host web (Kestrel) además del worker ASTM: el mismo ejecutable atiende
Synapsys por socket y expone un front React para operarlo. El puerto ASTM se abre solo al iniciar
(comportamiento histórico) pero puede abrirse/cerrarse/reiniciarse en caliente desde el front.

### Endpoints

| Método | Ruta | Para qué |
| --- | --- | --- |
| GET | `/api/status` | Estado del puerto (estado, modo, remoto, contadores, último error). |
| POST | `/api/port/open` · `/close` · `/restart` | Controlan el puerto ASTM sin reiniciar el servicio. |
| GET · PUT | `/api/settings/instrument` | Instrumento configurado. |
| GET · PUT | `/api/settings/communication` | Transporte + ASTM. El PUT reinicia el puerto si estaba abierto. |
| GET · POST · PUT · DELETE | `/api/settings/test-mappings` | Mapeo de pruebas del LIS (PUT/DELETE con `?incomingCode=`). |
| GET · PUT · DELETE | `/api/settings/catalogs/{catalog}` | Catálogo `results`, `organisms` o `antibiotics` (PUT/DELETE con `?code=`; PUT sin `code` = alta). |
| POST | `/api/settings/catalogs/{catalog}/clear` · `/import?replace=` | Borrar todos · importar CSV (`codigo;descripcion`). |
| WS | `/ws/comms` | Comunicación ASTM cruda (RX/TX, hex + texto) en tiempo real. |
| WS | `/ws/events` | Eventos semánticos (conexión, consulta, resultados, errores). |

Ambos WebSockets envían primero un buffer reciente al conectarse y luego el stream en vivo.

### Front (`web/`)

Vite + React + TypeScript. `npm run build` compila a `src/Synapsys.Connector/wwwroot`, que Kestrel
sirve como SPA. En desarrollo, `npm run dev` (puerto 5173) hace proxy de `/api` y `/ws` al servicio
(`http://localhost:5081`). La UI tiene cuatro secciones: estado + controles del puerto, los dos monitores
realtime, Settings (instrumento, comunicación, mapeo de tests y mapeo de resultados) y Microbiología
(microorganismos y antibióticos).

## Puntos abiertos

- El layout exacto de los registros `O`/`R`/`Q` sigue ASTM E1394 estándar; cuando esté la
  especificación puntual de Synapsys puede requerir ajuste fino (centralizado en
  `AstmValues.cs` y los flows).
