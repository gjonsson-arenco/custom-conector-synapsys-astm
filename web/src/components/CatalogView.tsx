import { useEffect, useMemo, useRef, useState } from "react";
import { api, type CatalogName, type CodeMapping } from "../api";

/** Fila en edicion: null = ninguna, "" = alta, otro = codigo de la fila que se edita. */
type Editing = string | null;

const csvField = (value: string) => (/[;"\n\r]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value);

interface CatalogViewProps {
  catalog: CatalogName;
  title: string;
  /** Archivo bajo settings/ donde se guarda. Tambien nombra el CSV exportado. */
  fileName: string;
  hint: string;
}

/** Administracion de un catalogo codigo => descripcion: alta, edicion, baja, importacion y exportacion CSV. */
export function CatalogView({ catalog, title, fileName, hint }: CatalogViewProps) {
  const [rows, setRows] = useState<CodeMapping[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState("");
  const [editing, setEditing] = useState<Editing>(null);
  const [draft, setDraft] = useState<CodeMapping>({ code: "", description: "" });
  const [replace, setReplace] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [importErrors, setImportErrors] = useState<string[]>([]);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    api
      .getCatalog(catalog)
      .then(setRows)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false));
  }, [catalog]);

  const visible = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return term
      ? rows.filter((row) => row.code.toLowerCase().includes(term) || row.description.toLowerCase().includes(term))
      : rows;
  }, [rows, filter]);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    setImportErrors([]);
    try {
      await action();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const startEdit = (row: CodeMapping | null) => {
    setError(null);
    setEditing(row ? row.code : "");
    setDraft(row ? { ...row } : { code: "", description: "" });
  };

  const save = () =>
    run(async () => {
      setRows(await api.saveCatalogEntry(catalog, editing === "" ? null : editing, draft));
      setEditing(null);
    });

  const remove = (row: CodeMapping) =>
    run(async () => {
      setRows(await api.deleteCatalogEntry(catalog, row.code));
    });

  const clearAll = () => {
    if (!confirm(`Borrar los ${rows.length} codigos de ${title}? No se puede deshacer.`)) return;
    run(async () => {
      setRows(await api.clearCatalog(catalog));
      setNotice("Se borraron todos los codigos.");
    });
  };

  const importFile = (file: File) =>
    run(async () => {
      const result = await api.importCatalog(catalog, await file.text(), replace);
      setRows(await api.getCatalog(catalog));
      setNotice(
        `${file.name}: ${result.added} nuevos, ${result.updated} actualizados. Total: ${result.total}.` +
          (replace ? " (se reemplazo el catalogo anterior)" : "")
      );
      setImportErrors(result.errors);
    });

  const exportCsv = () => {
    const lines = ["codigo;descripcion", ...rows.map((row) => `${csvField(row.code)};${csvField(row.description)}`)];
    // BOM para que Excel lo abra como UTF-8 (acentos).
    const blob = new Blob(["﻿" + lines.join("\r\n")], { type: "text/csv;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName.replace(/\.json$/, ".csv");
    link.click();
    URL.revokeObjectURL(link.href);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") save();
    if (e.key === "Escape") setEditing(null);
  };

  const editor = (
    <tr className="editing" key="__editor">
      <td>
        <input className="mono" autoFocus value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.target.value })} onKeyDown={onKey} />
      </td>
      <td>
        <input value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} onKeyDown={onKey} />
      </td>
      <td className="row-actions">
        <button className="link" disabled={busy || !draft.code.trim() || !draft.description.trim()} onClick={save}>
          Guardar
        </button>
        <button className="link" disabled={busy} onClick={() => setEditing(null)}>
          Cancelar
        </button>
      </td>
    </tr>
  );

  return (
    <section className="card">
      <header className="card-head">
        <h2>{title}</h2>
        <span className="badge muted">{rows.length} codigos</span>
      </header>

      <p className="hint">
        {hint} Se guarda en <span className="mono">settings/{fileName}</span>.
      </p>

      <div className="toolbar">
        <input
          className="search"
          placeholder="Buscar codigo o descripcion…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <button className="primary" disabled={busy || editing !== null} onClick={() => startEdit(null)}>
          Agregar
        </button>
        <span className="toolbar-sep" />
        <label className="checkbox" title="Si esta marcado, el CSV reemplaza todo el catalogo actual">
          <input type="checkbox" checked={replace} onChange={(e) => setReplace(e.target.checked)} />
          <span>Reemplazar todo</span>
        </label>
        <button disabled={busy} onClick={() => fileInput.current?.click()}>
          Importar CSV
        </button>
        <input
          ref={fileInput}
          type="file"
          accept=".csv,.txt,text/csv"
          hidden
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = "";
            if (file) importFile(file);
          }}
        />
        <button disabled={busy || rows.length === 0} onClick={exportCsv}>
          Exportar CSV
        </button>
        <button className="danger" disabled={busy || rows.length === 0} onClick={clearAll}>
          Borrar todos
        </button>
      </div>
      <p className="hint">
        CSV: una fila por codigo, <span className="mono">codigo;descripcion</span> (acepta tambien coma o tab, y encabezado
        opcional). Sin "Reemplazar todo" se agregan los codigos nuevos y se actualizan los existentes.
      </p>

      {notice && <p className="success">{notice}</p>}
      {importErrors.length > 0 && (
        <details className="import-errors">
          <summary>{importErrors.length} filas descartadas</summary>
          <ul>
            {importErrors.map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
        </details>
      )}
      {error && <p className="error">{error}</p>}

      <div className="table-wrap tall">
        <table className="table">
          <thead>
            <tr>
              <th style={{ width: "18%" }}>Codigo</th>
              <th>Descripcion</th>
              <th style={{ width: 130 }} />
            </tr>
          </thead>
          <tbody>
            {editing === "" && editor}
            {loading ? (
              <tr>
                <td colSpan={3} className="muted">
                  Cargando…
                </td>
              </tr>
            ) : visible.length === 0 ? (
              <tr>
                <td colSpan={3} className="muted">
                  {rows.length === 0 ? "Sin codigos. Agrega uno o importa un CSV." : "Nada coincide con la busqueda."}
                </td>
              </tr>
            ) : (
              visible.map((row) =>
                editing === row.code ? (
                  editor
                ) : (
                  <tr key={row.code}>
                    <td className="mono">{row.code}</td>
                    <td>{row.description}</td>
                    <td className="row-actions">
                      <button className="link" disabled={busy || editing !== null} onClick={() => startEdit(row)}>
                        Editar
                      </button>
                      <button className="link danger" disabled={busy || editing !== null} onClick={() => remove(row)}>
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
