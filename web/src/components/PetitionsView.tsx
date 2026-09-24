import { useCallback, useEffect, useState } from "react";
import { api, type Petition, type PetitionsStatus, type PetitionStatus } from "../api";

const PAGE = 100;

const STATUS_LABEL: Record<PetitionStatus, string> = {
  Pending: "Pendiente",
  Processed: "Procesada",
  Discarded: "Descartada",
  Error: "Error"
};

const STATUS_CLASS: Record<PetitionStatus, string> = {
  Pending: "state-listening",
  Processed: "state-connected",
  Discarded: "muted",
  Error: "state-faulted"
};

const FILTERS: { id: PetitionStatus | "all"; label: string }[] = [
  { id: "all", label: "Todas" },
  { id: "Pending", label: "Pendientes" },
  { id: "Error", label: "Con error" },
  { id: "Processed", label: "Procesadas" },
  { id: "Discarded", label: "Descartadas" }
];

const time = (value: string | null | undefined) => (value ? new Date(value).toLocaleString() : "—");

export function PetitionsView() {
  const [status, setStatus] = useState<PetitionsStatus | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [pollSeconds, setPollSeconds] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [filter, setFilter] = useState<PetitionStatus | "all">("all");
  const [barcode, setBarcode] = useState("");
  const [rows, setRows] = useState<Petition[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [listError, setListError] = useState<string | null>(null);

  const refreshStatus = useCallback(async () => {
    try {
      const value = await api.getPetitionsStatus();
      setStatus(value);
      setStatusError(null);
    } catch (e) {
      setStatusError((e as Error).message);
    }
  }, []);

  // Primera pagina con el filtro actual; beforeId pagina hacia atras sumando a lo ya cargado.
  const loadRows = useCallback(
    async (beforeId?: number) => {
      setLoading(true);
      try {
        const page = await api.getPetitions({
          status: filter === "all" ? undefined : filter,
          barcode: barcode.trim() || undefined,
          beforeId,
          top: PAGE
        });
        setRows((current) => (beforeId ? [...current, ...page] : page));
        setHasMore(page.length === PAGE);
        setListError(null);
      } catch (e) {
        setListError((e as Error).message);
      } finally {
        setLoading(false);
      }
    },
    [filter, barcode]
  );

  useEffect(() => {
    refreshStatus();
    api
      .getPetitionSettings()
      .then((value) => setPollSeconds(String(value.pollSeconds)))
      .catch((e) => setError((e as Error).message));
    const timer = setInterval(refreshStatus, 3000);
    return () => clearInterval(timer);
  }, [refreshStatus]);

  useEffect(() => {
    loadRows();
  }, [loadRows]);

  // Solo se refresca solo mientras se mira la primera pagina, para no perder lo paginado.
  useEffect(() => {
    if (rows.length > PAGE) return;
    const timer = setInterval(() => loadRows(), 5000);
    return () => clearInterval(timer);
  }, [loadRows, rows.length]);

  const outbox = status?.outbox;
  const summary = status?.summary;
  const enabled = outbox?.enabled ?? false;

  const saveSettings = async (value: { enabled: boolean; pollSeconds: number }) => {
    setBusy(true);
    setError(null);
    try {
      const saved = await api.savePetitionSettings(value);
      setPollSeconds(String(saved.pollSeconds));
      await refreshStatus();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const reprocess = async (ids: number[]) => {
    if (ids.length === 0) return;
    if (ids.length > 1 && !confirm(`Volver a mandar ${ids.length} peticiones al equipo?`)) return;
    setBusy(true);
    setListError(null);
    try {
      await api.reprocessPetitions(ids);
      await Promise.all([refreshStatus(), loadRows()]);
    } catch (e) {
      setListError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const errorIds = rows.filter((row) => row.status === "Error").map((row) => row.id);
  const count = (id: PetitionStatus | "all") =>
    !summary || id === "all"
      ? null
      : { Pending: summary.pending, Error: summary.error, Processed: summary.processed, Discarded: summary.discarded }[id];

  return (
    <div className="stack">
      <section className="card">
        <header className="card-head">
          <h2>Pulling de peticiones</h2>
          <div className="head-actions">
            <span className={`badge ${enabled ? "state-connected" : "state-stopped"}`}>{enabled ? "Activo" : "Apagado"}</span>
            <button
              className={enabled ? "" : "primary"}
              disabled={busy || !status}
              onClick={() => saveSettings({ enabled: !enabled, pollSeconds: Number(pollSeconds) || 10 })}
            >
              {enabled ? "Desactivar" : "Activar"}
            </button>
          </div>
        </header>

        <p className="hint">
          El LIS deja en <span className="mono">InstrumentPetitionQueue</span> las muestras que hay que mandar al equipo. Con
          la linea libre el conector toma la mas vieja, arma las ordenes y la marca procesada recien cuando el equipo la
          acepto. El equipo siempre tiene prioridad.
        </p>

        <dl className="status-grid">
          <div>
            <dt>Pendientes</dt>
            <dd className={summary?.pending ? "accent" : ""}>{summary?.pending ?? "—"}</dd>
          </div>
          <div>
            <dt>Con error</dt>
            <dd className={summary?.error ? "danger-text" : ""}>{summary?.error ?? "—"}</dd>
          </div>
          <div>
            <dt>Pendiente mas vieja</dt>
            <dd>{time(summary?.oldestPendingAt)}</dd>
          </div>
          <div>
            <dt>Procesadas / descartadas</dt>
            <dd>{summary ? `${summary.processed} / ${summary.discarded}` : "—"}</dd>
          </div>
          <div>
            <dt>Ultimo envio</dt>
            <dd>{time(outbox?.lastSentAt)}</dd>
          </div>
          <div>
            <dt>Enviadas desde el arranque</dt>
            <dd>{outbox?.sent ?? 0}</dd>
          </div>
        </dl>

        <div className="toolbar">
          <label className="inline-field">
            <span>Consultar la tabla cada</span>
            <input
              className="short"
              type="number"
              min={1}
              max={3600}
              value={pollSeconds}
              onChange={(e) => setPollSeconds(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && saveSettings({ enabled, pollSeconds: Number(pollSeconds) })}
            />
            <span>segundos</span>
          </label>
          <button
            disabled={busy || !outbox || !pollSeconds || Number(pollSeconds) === outbox.pollSeconds}
            onClick={() => saveSettings({ enabled, pollSeconds: Number(pollSeconds) })}
          >
            Guardar
          </button>
          <span className="muted small">Ultima consulta: {time(outbox?.lastPollAt)}</span>
        </div>

        {enabled && status && !status.connected && (
          <p className="warning">Synapsys no esta conectado: las peticiones quedan pendientes hasta que se conecte.</p>
        )}
        {outbox?.lastError && <p className="error">{outbox.lastError}</p>}
        {(statusError ?? status?.summaryError) && (
          <p className="error">No se pudo leer el resumen del LIS: {statusError ?? status?.summaryError}</p>
        )}
        {error && <p className="error">{error}</p>}
      </section>

      <section className="card">
        <header className="card-head">
          <h2>Peticiones</h2>
          <div className="head-actions">
            <button className="link" onClick={() => loadRows()}>
              Actualizar
            </button>
          </div>
        </header>

        <div className="toolbar">
          <div className="segmented">
            {FILTERS.map((item) => {
              const n = count(item.id);
              return (
                <button key={item.id} className={filter === item.id ? "active" : ""} onClick={() => setFilter(item.id)}>
                  {item.label}
                  {n !== null && <span className="count">{n}</span>}
                </button>
              );
            })}
          </div>
          <input
            className="search"
            placeholder="Codigo de barras…"
            value={barcode}
            onChange={(e) => setBarcode(e.target.value)}
          />
          {filter === "Error" && (
            <button disabled={busy || errorIds.length === 0} onClick={() => reprocess(errorIds)}>
              Reprocesar las {errorIds.length} con error
            </button>
          )}
        </div>

        {listError && <p className="error">{listError}</p>}

        <div className="table-wrap tall">
          <table className="table">
            <thead>
              <tr>
                <th>Id</th>
                <th>Fecha</th>
                <th>Muestra</th>
                <th>Orden</th>
                <th>Estado</th>
                <th>Motivo</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 ? (
                <tr>
                  <td colSpan={7} className="muted">
                    {loading ? "Cargando…" : "Sin peticiones."}
                  </td>
                </tr>
              ) : (
                rows.map((row) => (
                  <tr key={row.id}>
                    <td className="mono">{row.id}</td>
                    <td>{time(row.createdAt)}</td>
                    <td>
                      <span className="mono">{row.barcode ?? "—"}</span>
                      <div className="sub">mo_id {row.sampleId}</div>
                    </td>
                    <td className="mono">{row.orderNumber ?? "—"}</td>
                    <td>
                      <span className={`badge ${STATUS_CLASS[row.status as PetitionStatus] ?? "state-faulted"}`}>
                        {STATUS_LABEL[row.status as PetitionStatus] ?? row.status}
                      </span>
                    </td>
                    <td className="reason">{row.error ?? ""}</td>
                    <td className="row-actions">
                      {row.status !== "Pending" && (
                        <button className="link" disabled={busy} onClick={() => reprocess([row.id])}>
                          Reprocesar
                        </button>
                      )}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {hasMore && (
          <div className="actions load-more">
            <button disabled={loading} onClick={() => loadRows(rows[rows.length - 1].id)}>
              Cargar mas
            </button>
          </div>
        )}
      </section>
    </div>
  );
}
