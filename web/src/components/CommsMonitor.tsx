import { useMonitorSocket } from "../useMonitorSocket";
import type { CommsMessage } from "../api";

export function CommsMonitor() {
  const { items, connected, clear } = useMonitorSocket<CommsMessage>("/ws/comms");

  return (
    <section className="card monitor">
      <header className="card-head">
        <h2>Comunicacion ASTM</h2>
        <div className="head-actions">
          <span className={`badge ${connected ? "state-connected" : "state-faulted"}`}>
            {connected ? "en vivo" : "desconectado"}
          </span>
          <button className="link" onClick={clear}>
            Limpiar
          </button>
        </div>
      </header>

      <div className="stream mono">
        {items.length === 0 ? (
          <p className="muted">Sin trafico todavia.</p>
        ) : (
          items.map((msg, i) => (
            <div key={i} className={`line dir-${msg.direction.toLowerCase()}`}>
              <span className="ts">{new Date(msg.timestamp).toLocaleTimeString()}</span>
              <span className="tag">{msg.direction}</span>
              <span className="payload">{msg.text}</span>
            </div>
          ))
        )}
      </div>
    </section>
  );
}
