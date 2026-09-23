import { useEffect, useState } from "react";
import { api, type CommunicationSettings } from "../api";

type Transport = CommunicationSettings["transport"];
type Astm = CommunicationSettings["astm"];

export function CommunicationSettingsView() {
  const [value, setValue] = useState<CommunicationSettings | null>(null);
  const [original, setOriginal] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    api
      .getCommunication()
      .then((settings) => {
        setValue(settings);
        setOriginal(JSON.stringify(settings));
      })
      .catch((e) => setError((e as Error).message));
  }, []);

  if (!value) {
    return (
      <section className="card settings-card">
        <header className="card-head">
          <h2>Comunicacion</h2>
        </header>
        {error ? <p className="error">{error}</p> : <p className="muted">Cargando…</p>}
      </section>
    );
  }

  const transport = <K extends keyof Transport>(key: K, v: Transport[K]) =>
    setValue({ ...value, transport: { ...value.transport, [key]: v } });
  const astm = <K extends keyof Astm>(key: K, v: Astm[K]) => setValue({ ...value, astm: { ...value.astm, [key]: v } });

  const dirty = JSON.stringify(value) !== original;
  const isClient = value.transport.mode === "Client";

  const save = async () => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await api.saveCommunication(value);
      setValue(result.settings);
      setOriginal(JSON.stringify(result.settings));
      setNotice(
        result.restarted ? "Guardado. El puerto se reinicio con la configuracion nueva." : "Guardado. Aplica al abrir el puerto."
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card settings-card">
      <header className="card-head">
        <h2>Comunicacion</h2>
        {dirty && <span className="badge state-starting">Sin guardar</span>}
      </header>

      <h3 className="section-title">Socket</h3>
      <div className="form-grid">
        <label>
          <span>Modo</span>
          <select value={value.transport.mode} onChange={(e) => transport("mode", e.target.value as Transport["mode"])}>
            <option value="Server">Server (escucha)</option>
            <option value="Client">Client (conecta)</option>
          </select>
        </label>
        <label>
          <span>{isClient ? "Host de Synapsys" : "Interfaz de escucha"}</span>
          <input value={value.transport.host} onChange={(e) => transport("host", e.target.value)} />
        </label>
        <label>
          <span>Puerto</span>
          <input
            type="number"
            min={1}
            max={65535}
            value={value.transport.port}
            onChange={(e) => transport("port", Number(e.target.value))}
          />
        </label>
        <label>
          <span>Reintento (seg)</span>
          <input
            type="number"
            min={1}
            value={value.transport.reconnectSeconds}
            onChange={(e) => transport("reconnectSeconds", Number(e.target.value))}
          />
        </label>
      </div>

      <h3 className="section-title">ASTM</h3>
      <div className="form-grid">
        <label>
          <span>Nivel</span>
          <select value={value.astm.level} onChange={(e) => astm("level", e.target.value as Astm["level"])}>
            <option value="LowLevel">LowLevel (ENQ/ACK + frames)</option>
            <option value="HighLevel">HighLevel (mensaje completo)</option>
          </select>
        </label>
        <label>
          <span>Timeout de recepcion (seg)</span>
          <input
            type="number"
            min={1}
            value={value.astm.receiveTimeoutSeconds}
            onChange={(e) => astm("receiveTimeoutSeconds", Number(e.target.value))}
          />
        </label>
        <label>
          <span>Reintentos ante NAK</span>
          <input
            type="number"
            min={0}
            value={value.astm.maxRetries}
            onChange={(e) => astm("maxRetries", Number(e.target.value))}
          />
        </label>
        <label className="checkbox">
          <input
            type="checkbox"
            checked={value.astm.useChecksum}
            disabled={value.astm.level === "HighLevel"}
            onChange={(e) => astm("useChecksum", e.target.checked)}
          />
          <span>Validar checksum</span>
        </label>
        <label>
          <span>Separador de campo</span>
          <input
            className="mono short"
            maxLength={1}
            value={value.astm.fieldSeparator}
            onChange={(e) => astm("fieldSeparator", e.target.value)}
          />
        </label>
        <label>
          <span>Separador de componente</span>
          <input
            className="mono short"
            maxLength={1}
            value={value.astm.componentSeparator}
            onChange={(e) => astm("componentSeparator", e.target.value)}
          />
        </label>
        <label>
          <span>Separador de repeticion</span>
          <input
            className="mono short"
            maxLength={1}
            value={value.astm.repeatSeparator}
            onChange={(e) => astm("repeatSeparator", e.target.value)}
          />
        </label>
      </div>

      <div className="actions">
        <button className="primary" disabled={busy || !dirty} onClick={save}>
          Guardar
        </button>
        <button disabled={busy || !dirty} onClick={() => setValue(JSON.parse(original))}>
          Descartar
        </button>
      </div>
      <p className="hint">Si el puerto esta abierto, al guardar se reinicia y corta la sesion en curso con Synapsys.</p>

      {notice && <p className="success">{notice}</p>}
      {error && <p className="error">{error}</p>}
    </section>
  );
}
