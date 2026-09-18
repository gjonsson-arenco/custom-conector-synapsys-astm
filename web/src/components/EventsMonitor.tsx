import { useMonitorSocket } from "../useMonitorSocket";
import type { ConnectorEvent } from "../api";

export function EventsMonitor() {
  const { items, connected, clear } = useMonitorSocket<ConnectorEvent>("/ws/events");

  return (
    <section className="card monitor">
      <header className="card-head">
        <h2>Eventos</h2>
        <div className="head-actions">
          <span className={`badge ${connected ? "state-connected" : "state-faulted"}`}>
            {connected ? "en vivo" : "desconectado"}
          </span>
          <button className="link" onClick={clear}>
            Limpiar
          </button>
        </div>
      </header>

      <div className="stream">
        {items.length === 0 ? (
          <p className="muted">Sin eventos todavia.</p>
        ) : (
          items.map((evt, i) => (
            <div key={i} className={`line level-${evt.level}`}>
              <span className="ts">{new Date(evt.timestamp).toLocaleTimeString()}</span>
              <span className="tag">{evt.kind}</span>
              <span className="payload">{evt.message}</span>
            </div>
          ))
        )}
      </div>
    </section>
  );
}
