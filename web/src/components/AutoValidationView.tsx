import { useEffect, useMemo, useState } from "react";
import { api, type AutoValidationRule, type AutoValidationSettings, type CodeMapping, type TestMapping } from "../api";

const EMPTY_RULE: AutoValidationRule = { testCode: "", sampleType: "", result: "" };

const same = (a: string | null | undefined, b: string | null | undefined) =>
  (a ?? "").trim().toLowerCase() === (b ?? "").trim().toLowerCase();

/** Reglas de autovalidacion: que resultados simples se guardan en el LIS ya validados. */
export function AutoValidationView() {
  const [settings, setSettings] = useState<AutoValidationSettings | null>(null);
  const [tests, setTests] = useState<TestMapping[]>([]);
  const [results, setResults] = useState<CodeMapping[]>([]);
  const [draft, setDraft] = useState<AutoValidationRule | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .getAutoValidation()
      .then(setSettings)
      .catch((e) => setError((e as Error).message));
    // Solo para sugerir codigos y mostrar nombres: si el LIS no contesta, se tipean a mano.
    api
      .getTestMappings()
      .then((mapping) => setTests(mapping.tests))
      .catch(() => setTests([]));
    api
      .getCatalog("results")
      .then(setResults)
      .catch(() => setResults([]));
  }, []);

  const testCodes = useMemo(
    () => [...new Map(tests.filter((t) => t.testCode).map((t) => [t.testCode!.trim(), t])).values()],
    [tests]
  );

  const testName = (code: string) => {
    const test = testCodes.find((t) => same(t.testCode, code));
    return test ? test.testName ?? test.name : null;
  };

  // null = no se sabe (el LIS no contesto o la prueba no esta en el mapeo).
  const enabledInLis = (code: string) => testCodes.find((t) => same(t.testCode, code))?.autovalidationEnabled ?? null;

  const resultName = (code: string) => results.find((r) => same(r.code, code))?.description ?? null;

  const save = async (value: AutoValidationSettings) => {
    setBusy(true);
    setError(null);
    try {
      setSettings(await api.saveAutoValidation(value));
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  };

  const addRule = async () => {
    if (!settings || !draft) return;
    const rule = { testCode: draft.testCode.trim(), sampleType: draft.sampleType?.trim() || null, result: draft.result.trim() };
    if (await save({ ...settings, rules: [...settings.rules, rule] })) setDraft(null);
  };

  const removeRule = (index: number) =>
    settings && save({ ...settings, rules: settings.rules.filter((_, i) => i !== index) });

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") addRule();
    if (e.key === "Escape") setDraft(null);
  };

  const enabled = settings?.enabled ?? false;
  const rules = settings?.rules ?? [];

  return (
    <section className="card">
      <header className="card-head">
        <h2>Autovalidacion</h2>
        <div className="head-actions">
          <span className={`badge ${enabled ? "state-connected" : "state-stopped"}`}>{enabled ? "Activa" : "Apagada"}</span>
          <button
            className={enabled ? "" : "primary"}
            disabled={busy || !settings}
            onClick={() => settings && save({ ...settings, enabled: !enabled })}
          >
            {enabled ? "Desactivar" : "Activar"}
          </button>
        </div>
      </header>

      <p className="hint">
        Si la prueba tiene la autovalidacion habilitada en el mapeo de tests del LIS y, en ese tipo de muestra, trae ese
        resultado, se guarda en el LIS validada (
        <span className="mono">l_estado = 4</span>, con fecha y usuario de validacion) y queda registrada en{" "}
        <span className="mono">AppLog</span> como autovalidacion. El resultado es el valor tal cual lo manda el equipo, antes
        del mapeo de resultados (<span className="mono">G8</span>, <span className="mono">NEGB</span>). En un cultivo es el
        estado, y solo aplica si todavia no llegaron aislados. Un resultado con flags del equipo nunca se autovalida. Tipo de
        muestra vacio = cualquiera. Se guarda en <span className="mono">settings/autovalidation.json</span>.
      </p>

      <div className="toolbar">
        <button className="primary" disabled={busy || !settings || draft !== null} onClick={() => setDraft({ ...EMPTY_RULE })}>
          Agregar regla
        </button>
      </div>

      {error && <p className="error">{error}</p>}

      <datalist id="autoval-tests">
        {testCodes.map((t) => (
          <option key={t.testCode} value={t.testCode!.trim()}>
            {t.testName ?? t.name ?? ""}
          </option>
        ))}
      </datalist>
      <datalist id="autoval-results">
        {results.map((r) => (
          <option key={r.code} value={r.code}>
            {r.description}
          </option>
        ))}
      </datalist>

      <div className="table-wrap tall">
        <table className="table">
          <thead>
            <tr>
              <th style={{ width: "30%" }}>Prueba (LIS)</th>
              <th style={{ width: "18%" }}>Tipo de muestra</th>
              <th>Resultado del equipo</th>
              <th style={{ width: 130 }} />
            </tr>
          </thead>
          <tbody>
            {draft && (
              <tr className="editing">
                <td>
                  <input
                    className="mono"
                    autoFocus
                    list="autoval-tests"
                    placeholder="CGR"
                    value={draft.testCode}
                    onChange={(e) => setDraft({ ...draft, testCode: e.target.value })}
                    onKeyDown={onKey}
                  />
                </td>
                <td>
                  <input
                    className="mono"
                    placeholder="Cualquiera"
                    value={draft.sampleType ?? ""}
                    onChange={(e) => setDraft({ ...draft, sampleType: e.target.value })}
                    onKeyDown={onKey}
                  />
                </td>
                <td>
                  <input
                    className="mono"
                    list="autoval-results"
                    placeholder="NEGB"
                    value={draft.result}
                    onChange={(e) => setDraft({ ...draft, result: e.target.value })}
                    onKeyDown={onKey}
                  />
                </td>
                <td className="row-actions">
                  <button className="link" disabled={busy || !draft.testCode.trim() || !draft.result.trim()} onClick={addRule}>
                    Guardar
                  </button>
                  <button className="link" disabled={busy} onClick={() => setDraft(null)}>
                    Cancelar
                  </button>
                </td>
              </tr>
            )}
            {!settings ? (
              <tr>
                <td colSpan={4} className="muted">
                  Cargando…
                </td>
              </tr>
            ) : rules.length === 0 ? (
              <tr>
                <td colSpan={4} className="muted">
                  Sin reglas: todos los resultados se guardan como cargados, pendientes de validacion.
                </td>
              </tr>
            ) : (
              rules.map((rule, index) => (
                <tr key={`${rule.testCode}|${rule.sampleType ?? ""}|${rule.result}`}>
                  <td>
                    <span className="mono">{rule.testCode}</span>
                    {testName(rule.testCode) && <span className="muted"> · {testName(rule.testCode)}</span>}
                    {enabledInLis(rule.testCode) === false && (
                      <div className="danger-text small">Sin autovalidacion en el mapeo del LIS: no aplica.</div>
                    )}
                  </td>
                  <td className="mono">{rule.sampleType || <span className="muted">Cualquiera</span>}</td>
                  <td>
                    <span className="mono">{rule.result}</span>
                    {resultName(rule.result) && <span className="muted"> · {resultName(rule.result)}</span>}
                  </td>
                  <td className="row-actions">
                    <button className="link danger" disabled={busy || draft !== null} onClick={() => removeRule(index)}>
                      Quitar
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </section>
  );
}
