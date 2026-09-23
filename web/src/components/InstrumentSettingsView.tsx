import { useEffect, useState } from "react";
import { api } from "../api";

export function InstrumentSettingsView() {
  const [instrumentId, setInstrumentId] = useState("");
  const [saved, setSaved] = useState<number | null>(null);
  const [instrumentName, setInstrumentName] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // El nombre sale del LIS: confirma que el id corresponde al analizador correcto.
  const lookupName = async () => {
    try {
      setInstrumentName((await api.getTestMappings()).instrumentName ?? "(sin nombre)");
    } catch (e) {
      setInstrumentName(null);
      setError(`El LIS no confirmo el analizador: ${(e as Error).message}`);
    }
  };

  useEffect(() => {
    api
      .getInstrument()
      .then((value) => {
        setInstrumentId(value.instrumentId > 0 ? String(value.instrumentId) : "");
        setSaved(value.instrumentId);
        if (value.instrumentId > 0) lookupName();
      })
      .catch((e) => setError((e as Error).message));
  }, []);

  const save = async () => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const value = await api.saveInstrument({ instrumentId: Number(instrumentId) });
      setSaved(value.instrumentId);
      setNotice("Guardado.");
      await lookupName();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card settings-card">
      <header className="card-head">
        <h2>Instrumento</h2>
        {saved !== null && saved <= 0 && <span className="badge state-faulted">Sin configurar</span>}
      </header>

      <p className="hint">
        Id del analizador en el LIS (<span className="mono">Analizadores.a_id</span>). Es la clave con la que labcore-api
        resuelve el mapeo de pruebas y con la que se cargan los resultados.
      </p>

      <div className="form-grid">
        <label>
          <span>Instrument ID</span>
          <input
            type="number"
            min={1}
            value={instrumentId}
            onChange={(e) => setInstrumentId(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && save()}
          />
        </label>
        <div className="field-static">
          <span>Analizador en el LIS</span>
          <strong>{instrumentName ?? "—"}</strong>
        </div>
      </div>

      <div className="actions">
        <button className="primary" disabled={busy || !instrumentId || Number(instrumentId) === saved} onClick={save}>
          Guardar
        </button>
      </div>

      {notice && <p className="success">{notice}</p>}
      {error && <p className="error">{error}</p>}
    </section>
  );
}
