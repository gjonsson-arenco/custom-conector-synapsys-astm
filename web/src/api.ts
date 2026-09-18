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
  testCode: string;
  incomingCode: string | null;
  outgoingCode: string | null;
  factor: number;
  name: string | null;
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

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }
  return (await response.json()) as T;
}

export const api = {
  getStatus: () => fetch("/api/status").then(json<ConnectorStatus>),
  openPort: () => fetch("/api/port/open", { method: "POST" }).then(json<ConnectorStatus>),
  closePort: () => fetch("/api/port/close", { method: "POST" }).then(json<ConnectorStatus>),
  restartPort: () => fetch("/api/port/restart", { method: "POST" }).then(json<ConnectorStatus>),
  getMappings: () => fetch("/api/mappings").then(json<TestMapping[]>)
};
