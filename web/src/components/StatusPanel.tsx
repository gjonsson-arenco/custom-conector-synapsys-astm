import { useEffect, useState } from "react";
import { api, type ConnectorStatus, type PortState } from "../api";

const STATE_LABEL: Record<PortState, string> = {
  Stopped: "Cerrado",
  Starting: "Abriendo",
  Listening: "Escuchando",
  Connected: "Conectado",
  Faulted: "Error"
};

export function StatusPanel() {
  const [status, setStatus] = useState<ConnectorStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = async () => {
    try {
      setStatus(await api.getStatus());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  useEffect(() => {
    refresh();
    const timer = setInterval(refresh, 2000);
    return () => clearInterval(timer);
  }, []);

  const run = async (action: () => Promise<ConnectorStatus>) => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await action());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const state = status?.state ?? "Stopped";
  const isOpen = state === "Listening" || state === "Connected";

  return (
    <section className="card">
      <header className="card-head">
        <h2>Puerto ASTM</h2>
        <span className={`badge state-${state.toLowerCase()}`}>{STATE_LABEL[state]}</span>
      </header>

      <dl className="status-grid">
        <div>
          <dt>Modo</dt>
          <dd>{status?.mode ?? "—"}</dd>
        </div>
        <div>
          <dt>Escucha</dt>
          <dd>
            {status ? `${status.host}:${status.port}` : "—"}
          </dd>
        </div>
        <div>
          <dt>Remoto</dt>
          <dd>{status?.remote ?? "—"}</dd>
        </div>
        <div>
          <dt>Recibidas</dt>
          <dd>{status?.transmissionsReceived ?? 0}</dd>
        </div>
        <div>
          <dt>Respondidas</dt>
          <dd>{status?.responsesSent ?? 0}</dd>
        </div>
        <div>
          <dt>Desde</dt>
          <dd>{status?.startedAt ? new Date(status.startedAt).toLocaleTimeString() : "—"}</dd>
        </div>
      </dl>

      <div className="actions">
        <button disabled={busy || isOpen} onClick={() => run(api.openPort)}>
          Abrir
        </button>
        <button disabled={busy || !isOpen} onClick={() => run(api.closePort)}>
          Cerrar
        </button>
        <button disabled={busy} onClick={() => run(api.restartPort)}>
          Reiniciar
        </button>
      </div>

      {(error ?? status?.lastError) && (
        <p className="error">{error ?? status?.lastError}</p>
      )}
    </section>
  );
}
