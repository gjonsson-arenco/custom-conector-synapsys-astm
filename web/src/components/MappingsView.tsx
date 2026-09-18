import { useEffect, useState } from "react";
import { api, type TestMapping } from "../api";

export function MappingsView() {
  const [rows, setRows] = useState<TestMapping[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = async () => {
    setLoading(true);
    try {
      setRows(await api.getMappings());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  return (
    <section className="card">
      <header className="card-head">
        <h2>Mapeos de codigos</h2>
        <div className="head-actions">
          <span className="badge muted">mock</span>
          <button className="link" onClick={load}>
            Actualizar
          </button>
        </div>
      </header>

      {error && <p className="error">{error}</p>}

      <table className="table">
        <thead>
          <tr>
            <th>Prueba (LIS)</th>
            <th>Nombre</th>
            <th>Entrante</th>
            <th>Saliente</th>
            <th>Factor</th>
          </tr>
        </thead>
        <tbody>
          {loading ? (
            <tr>
              <td colSpan={5} className="muted">
                Cargando…
              </td>
            </tr>
          ) : rows.length === 0 ? (
            <tr>
              <td colSpan={5} className="muted">
                Sin mapeos.
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr key={row.testCode}>
                <td className="mono">{row.testCode}</td>
                <td>{row.name ?? "—"}</td>
                <td className="mono">{row.incomingCode ?? "—"}</td>
                <td className="mono">{row.outgoingCode ?? "—"}</td>
                <td className="mono">{row.factor}</td>
              </tr>
            ))
          )}
        </tbody>
      </table>
    </section>
  );
}
