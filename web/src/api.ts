export type PortState = "Stopped" | "Starting" | "Listening" | "Connected" | "Faulted";

export interface ConnectorStatus {
  state: PortState;
  mode: string;
  host: string;
  port: number;
  remote: string | null;
  startedAt: string | null;
  transmissionsReceived: number;
  responsesSent: number;
  lastError: string | null;
}

export interface TestMapping {
  incomingCode: string | null;
  testCode: string | null;
  testName?: string | null;
  name: string | null;
  outgoingCode: string | null;
  factor: number | null;
  active: boolean;
  suffix: string | null;
  resultIndicator: string | null;
  sampleIndicator: string | null;
  autovalidationEnabled: boolean;
  units?: string | null;
}

export interface InstrumentTestMappings {
  instrumentId: number;
  instrumentName: string | null;
  tests: TestMapping[];
}

export interface InstrumentSettings {
  instrumentId: number;
}

export interface CommunicationSettings {
  transport: {
    mode: "Server" | "Client";
    host: string;
    port: number;
    reconnectSeconds: number;
  };
  astm: {
    level: "LowLevel" | "HighLevel";
    useChecksum: boolean;
    receiveTimeoutSeconds: number;
    maxRetries: number;
    fieldSeparator: string;
    componentSeparator: string;
    repeatSeparator: string;
  };
}

/** settings/petitions.json: pulling de peticiones del LIS. */
export interface PetitionSettings {
  enabled: boolean;
  pollSeconds: number;
}

/** Una regla de autovalidacion: prueba del LIS + tipo de muestra (vacio = cualquiera) + valor del equipo. */
export interface AutoValidationRule {
  testCode: string;
  sampleType: string | null;
  result: string;
}

/** settings/autovalidation.json. */
export interface AutoValidationSettings {
  enabled: boolean;
  rules: AutoValidationRule[];
}

export type PetitionStatus = "Pending" | "Processed" | "Discarded" | "Error";

/** Una peticion de InstrumentPetitionQueue: una muestra que el LIS pide mandar al equipo. */
export interface Petition {
  id: number;
  sampleId: number;
  barcode: string | null;
  orderNumber: string | null;
  createdAt: string | null;
  status: PetitionStatus | string;
  error: string | null;
  petitionType: string | null;
}

export interface PetitionsStatus {
  outbox: {
    enabled: boolean;
    pollSeconds: number;
    lastPollAt: string | null;
    lastSentAt: string | null;
    sent: number;
    lastError: string | null;
  };
  connected: boolean;
  summary: {
    pending: number;
    processed: number;
    discarded: number;
    error: number;
    oldestPendingAt: string | null;
  } | null;
  summaryError: string | null;
}

export interface PetitionFilter {
  status?: PetitionStatus;
  barcode?: string;
  beforeId?: number;
  top?: number;
}

/** Catalogos codigo => descripcion que administra el conector (settings/*.json). */
export type CatalogName = "results" | "organisms" | "antibiotics";

export interface CodeMapping {
  code: string;
  description: string;
}

export interface CatalogImport {
  added: number;
  updated: number;
  total: number;
  errors: string[];
}

export interface CommsMessage {
  direction: "RX" | "TX";
  hex: string;
  text: string;
  timestamp: string;
}

export interface ConnectorEvent {
  level: "info" | "warn" | "error";
  kind: string;
  message: string;
  timestamp: string;
}

/** Error de la API con el detalle del ProblemDetails (titulo/detalle y errores por campo). */
export class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly errors?: Record<string, string[]>) {
    super(message);
  }
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    let problem: { title?: string; detail?: string; errors?: Record<string, string[]> } = {};
    try {
      problem = await response.json();
    } catch {
      // Sin cuerpo JSON: queda el status.
    }
    const fields = problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
    const message = [problem.detail ?? problem.title, fields].filter(Boolean).join(" ") || `HTTP ${response.status}`;
    throw new ApiError(message, response.status, problem.errors);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

const send = <T>(url: string, method: string, body?: unknown) =>
  fetch(url, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  }).then(json<T>);

const q = (name: string, value: string) => `?${name}=${encodeURIComponent(value)}`;

export const api = {
  getStatus: () => fetch("/api/status").then(json<ConnectorStatus>),
  openPort: () => send<ConnectorStatus>("/api/port/open", "POST"),
  closePort: () => send<ConnectorStatus>("/api/port/close", "POST"),
  restartPort: () => send<ConnectorStatus>("/api/port/restart", "POST"),

  getInstrument: () => fetch("/api/settings/instrument").then(json<InstrumentSettings>),
  saveInstrument: (value: InstrumentSettings) => send<InstrumentSettings>("/api/settings/instrument", "PUT", value),

  getCommunication: () => fetch("/api/settings/communication").then(json<CommunicationSettings>),
  saveCommunication: (value: CommunicationSettings) =>
    send<{ settings: CommunicationSettings; restarted: boolean }>("/api/settings/communication", "PUT", value),

  getPetitionSettings: () => fetch("/api/settings/petitions").then(json<PetitionSettings>),
  savePetitionSettings: (value: PetitionSettings) => send<PetitionSettings>("/api/settings/petitions", "PUT", value),
  getAutoValidation: () => fetch("/api/settings/autovalidation").then(json<AutoValidationSettings>),
  saveAutoValidation: (value: AutoValidationSettings) =>
    send<AutoValidationSettings>("/api/settings/autovalidation", "PUT", value),
  getPetitionsStatus: () => fetch("/api/petitions/status").then(json<PetitionsStatus>),
  getPetitions: (filter: PetitionFilter) => {
    const params = new URLSearchParams();
    Object.entries(filter).forEach(([key, value]) => {
      if (value !== undefined && value !== "") params.set(key, String(value));
    });
    const query = params.toString();
    return fetch(`/api/petitions${query ? `?${query}` : ""}`).then(json<Petition[]>);
  },
  reprocessPetitions: (ids: number[]) => send<void>("/api/petitions/reprocess", "POST", { ids }),

  getTestMappings: () => fetch("/api/settings/test-mappings").then(json<InstrumentTestMappings>),
  createTestMapping: (value: TestMapping) => send<void>("/api/settings/test-mappings", "POST", value),
  updateTestMapping: (incomingCode: string, value: TestMapping) =>
    send<void>(`/api/settings/test-mappings${q("incomingCode", incomingCode)}`, "PUT", value),
  deleteTestMapping: (incomingCode: string) =>
    send<void>(`/api/settings/test-mappings${q("incomingCode", incomingCode)}`, "DELETE"),

  getCatalog: (catalog: CatalogName) => fetch(`/api/settings/catalogs/${catalog}`).then(json<CodeMapping[]>),
  saveCatalogEntry: (catalog: CatalogName, originalCode: string | null, value: CodeMapping) =>
    send<CodeMapping[]>(
      `/api/settings/catalogs/${catalog}${originalCode === null ? "" : q("code", originalCode)}`,
      "PUT",
      value
    ),
  deleteCatalogEntry: (catalog: CatalogName, code: string) =>
    send<CodeMapping[]>(`/api/settings/catalogs/${catalog}${q("code", code)}`, "DELETE"),
  clearCatalog: (catalog: CatalogName) => send<CodeMapping[]>(`/api/settings/catalogs/${catalog}/clear`, "POST"),
  importCatalog: (catalog: CatalogName, csv: string, replace: boolean) =>
    fetch(`/api/settings/catalogs/${catalog}/import?replace=${replace}`, {
      method: "POST",
      headers: { "Content-Type": "text/csv" },
      body: csv
    }).then(json<CatalogImport>)
};
