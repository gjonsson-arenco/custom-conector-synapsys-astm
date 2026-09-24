import { useEffect, useState } from "react";
import { api, type LicenseState, type LicenseStatus } from "../api";

export const LICENSE_CHANGED = "license-changed";

export const LICENSE_LABEL: Record<LicenseState, string> = {
  Missing: "Sin licencia",
  Invalid: "Invalida",
  WrongProduct: "Otro producto",
  WrongMachine: "Otra maquina",
  Valid: "Vigente",
  ExpiringSoon: "Por vencer",
  Grace: "En tolerancia",
  Expired: "Vencida"
};

export const LICENSE_CLASS: Record<LicenseState, string> = {
  Missing: "state-faulted",
  Invalid: "state-faulted",
  WrongProduct: "state-faulted",
  WrongMachine: "state-faulted",
  Valid: "state-connected",
  ExpiringSoon: "state-starting",
  Grace: "state-starting",
  Expired: "state-faulted"
};

// Las fechas llegan como yyyy-MM-dd (DateOnly): se formatean sin pasar por Date para no correrlas de dia.
const day = (value: string | null | undefined) => (value ? value.split("-").reverse().join("/") : "—");

export function LicenseView() {
  const [status, setStatus] = useState<LicenseStatus | null>(null);
  const [key, setKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    api
      .getLicense()
      .then(setStatus)
      .catch((e) => setError((e as Error).message));
  }, []);

  const install = async () => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const value = await api.installLicense(key);
      setStatus(value);
      setKey("");
      setNotice("Licencia instalada.");
      window.dispatchEvent(new Event(LICENSE_CHANGED));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const copy = async () => {
    if (!status) return;
    await navigator.clipboard.writeText(status.machineCode);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  const license = status?.license;

  return (
    <section className="card settings-card">
      <header className="card-head">
        <h2>Licencia</h2>
        {status && <span className={`badge ${LICENSE_CLASS[status.state]}`}>{LICENSE_LABEL[status.state]}</span>}
      </header>

      {status && (
        <p className={status.state === "Valid" ? "hint" : status.isUsable ? "warning license-message" : "error license-message"}>
          {status.message}
        </p>
      )}

      <div className="form-grid">
        <div className="field-static">
          Codigo de maquina
          <strong className="mono machine-code">
            {status?.machineCode ?? "—"}
            {status && (
              <button className="link" onClick={copy}>
                {copied ? "Copiado" : "Copiar"}
              </button>
            )}
          </strong>
        </div>
        <div className="field-static">
          Producto
          <strong className="mono">{status?.product ?? "—"}</strong>
        </div>
        <div className="field-static">
          Cliente
          <strong>{license?.customer ?? "—"}</strong>
        </div>
        <div className="field-static">
          Emitida
          <strong>{day(license?.issuedAt)}</strong>
        </div>
        <div className="field-static">
          Vence
          <strong>{day(license?.expiresAt)}</strong>
        </div>
        <div className="field-static">
          Tolerancia hasta
          <strong>{day(status?.graceUntil)}</strong>
        </div>
      </div>

      <div className="section-title">Instalar licencia</div>
      <p className="hint">
        Para pedir o renovar la licencia, manda el codigo de maquina a Arenco. Pega aca la clave que te devuelvan: si es
        valida para este conector y esta maquina, reemplaza a la actual y el puerto ASTM se abre solo si estaba frenado.
      </p>
      <textarea
        className="mono license-input"
        rows={4}
        placeholder="Clave de licencia"
        value={key}
        onChange={(e) => setKey(e.target.value)}
        spellCheck={false}
      />
      <div className="actions">
        <button className="primary" disabled={busy || key.trim() === ""} onClick={install}>
          Instalar
        </button>
      </div>

      {error && <p className="error">{error}</p>}
      {notice && <p className="success">{notice}</p>}
    </section>
  );
}
