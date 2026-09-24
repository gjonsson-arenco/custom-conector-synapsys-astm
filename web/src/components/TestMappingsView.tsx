import { Fragment, useEffect, useMemo, useState } from "react";
import { api, type InstrumentTestMappings, type TestMapping } from "../api";

const EMPTY: TestMapping = {
  incomingCode: "",
  testCode: "",
  name: "",
  outgoingCode: "",
  factor: 1,
  active: true,
  suffix: "",
  resultIndicator: "",
  sampleIndicator: "",
  autovalidationEnabled: false
};

/** Fila en edicion: null = ninguna, "" = alta, otro = codigo entrante de la fila que se edita. */
type Editing = string | null;

export function TestMappingsView() {
  const [data, setData] = useState<InstrumentTestMappings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState("");
  const [editing, setEditing] = useState<Editing>(null);
  const [draft, setDraft] = useState<TestMapping>(EMPTY);
  const [busy, setBusy] = useState(false);

  const load = async (refresh = false) => {
    setLoading(true);
    try {
      setData(await api.getTestMappings(refresh));
      setError(null);
    } catch (e) {
      setData(null);
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
  }, []);

  const rows = useMemo(() => {
    const term = filter.trim().toLowerCase();
    const tests = data?.tests ?? [];
    if (!term) return tests;
    return tests.filter((row) =>
      [row.incomingCode, row.outgoingCode, row.testCode, row.testName, row.name].some((v) =>
        v?.toLowerCase().includes(term)
      )
    );
  }, [data, filter]);

  const startEdit = (row: TestMapping | null) => {
    setError(null);
    setEditing(row ? row.incomingCode ?? "" : "");
    setDraft(row ? { ...EMPTY, ...row } : EMPTY);
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      if (editing === "") await api.createTestMapping(draft);
      else await api.updateTestMapping(editing!, draft);
      setEditing(null);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const remove = async (row: TestMapping) => {
    if (!row.incomingCode || !confirm(`Quitar el mapeo ${row.incomingCode} → ${row.testCode} del LIS?`)) return;
    setBusy(true);
    setError(null);
    try {
      await api.deleteTestMapping(row.incomingCode);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const editor = (
    <EditorRow draft={draft} setDraft={setDraft} busy={busy} onSave={save} onCancel={() => setEditing(null)} />
  );

  return (
    <section className="card">
      <header className="card-head">
        <h2>Mapeo de tests</h2>
        <div className="head-actions">
          {data && (
            <span className="badge muted">
              {data.instrumentName ?? "Analizador"} #{data.instrumentId}
            </span>
          )}
          <button className="link" onClick={() => load(true)} title="Releer el mapeo del LIS">
            Actualizar
          </button>
        </div>
      </header>

      <p className="hint">
        Vive en el LIS (<span className="mono">AnalizadoresDet</span>) y se edita a traves de labcore-api. El codigo entrante
        identifica la fila.
      </p>

      <div className="toolbar">
        <input
          className="search"
          placeholder="Buscar por codigo o nombre…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <button className="primary" disabled={!data || editing !== null} onClick={() => startEdit(null)}>
          Agregar
        </button>
      </div>

      {error && <p className="error">{error}</p>}

      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Entrante</th>
              <th>Prueba LIS</th>
              <th>Nombre equipo</th>
              <th>Saliente</th>
              <th>Factor</th>
              <th>Sufijo</th>
              <th>Ind. res.</th>
              <th>Ind. muestra</th>
              <th>Autoval.</th>
              <th>Activo</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {editing === "" && editor}
            {loading ? (
              <tr>
                <td colSpan={11} className="muted">
                  Cargando…
                </td>
              </tr>
            ) : rows.length === 0 ? (
              <tr>
                <td colSpan={11} className="muted">
                  {data ? "Sin mapeos." : "No se pudo leer el mapeo del LIS."}
                </td>
              </tr>
            ) : (
              rows.map((row) =>
                editing !== null && editing !== "" && editing === row.incomingCode ? (
                  <Fragment key={row.incomingCode}>{editor}</Fragment>
                ) : (
                  <tr key={row.incomingCode ?? row.testCode} className={row.active ? "" : "inactive"}>
                    <td className="mono">{row.incomingCode ?? "—"}</td>
                    <td>
                      <span className="mono">{row.testCode ?? "—"}</span>
                      {row.testName && <div className="sub">{row.testName}</div>}
                    </td>
                    <td>{row.name ?? "—"}</td>
                    <td className="mono">{row.outgoingCode ?? "—"}</td>
                    <td className="mono">{row.factor ?? 1}</td>
                    <td className="mono">{row.suffix ?? ""}</td>
                    <td className="mono">{row.resultIndicator ?? ""}</td>
                    <td className="mono">{row.sampleIndicator ?? ""}</td>
                    <td>{row.autovalidationEnabled ? "Si" : "No"}</td>
                    <td>{row.active ? "Si" : "No"}</td>
                    <td className="row-actions">
                      <button className="link" disabled={editing !== null} onClick={() => startEdit(row)}>
                        Editar
                      </button>
                      <button className="link danger" disabled={editing !== null || busy} onClick={() => remove(row)}>
                        Quitar
                      </button>
                    </td>
                  </tr>
                )
              )
            )}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function EditorRow({
  draft,
  setDraft,
  busy,
  onSave,
  onCancel
}: {
  draft: TestMapping;
  setDraft: (value: TestMapping) => void;
  busy: boolean;
  onSave: () => void;
  onCancel: () => void;
}) {
  const text = (key: keyof TestMapping, className = "mono") => (
    <input
      className={className}
      value={(draft[key] as string | null) ?? ""}
      onChange={(e) => setDraft({ ...draft, [key]: e.target.value })}
      onKeyDown={(e) => {
        if (e.key === "Enter") onSave();
        if (e.key === "Escape") onCancel();
      }}
    />
  );

  return (
    <tr className="editing">
      <td>{text("incomingCode")}</td>
      <td>{text("testCode")}</td>
      <td>{text("name", "")}</td>
      <td>{text("outgoingCode")}</td>
      <td>
        <input
          className="mono"
          type="number"
          step="any"
          value={draft.factor ?? 1}
          onChange={(e) => setDraft({ ...draft, factor: e.target.value === "" ? null : Number(e.target.value) })}
        />
      </td>
      <td>{text("suffix")}</td>
      <td>{text("resultIndicator")}</td>
      <td>{text("sampleIndicator")}</td>
      <td>
        <input
          type="checkbox"
          checked={draft.autovalidationEnabled}
          onChange={(e) => setDraft({ ...draft, autovalidationEnabled: e.target.checked })}
        />
      </td>
      <td>
        <input type="checkbox" checked={draft.active} onChange={(e) => setDraft({ ...draft, active: e.target.checked })} />
      </td>
      <td className="row-actions">
        <button className="link" disabled={busy || !draft.incomingCode || !draft.testCode} onClick={onSave}>
          Guardar
        </button>
        <button className="link" disabled={busy} onClick={onCancel}>
          Cancelar
        </button>
      </td>
    </tr>
  );
}
