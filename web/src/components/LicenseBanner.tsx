import { useEffect, useState } from "react";
import { api, type LicenseStatus } from "../api";
import { LICENSE_CHANGED } from "./LicenseView";

/** Aviso fijo arriba del panel cuando la licencia vence pronto, esta en tolerancia o no sirve. */
export function LicenseBanner() {
  const [status, setStatus] = useState<LicenseStatus | null>(null);

  useEffect(() => {
    const refresh = () =>
      api
        .getLicense()
        .then(setStatus)
        .catch(() => {
          // Sin respuesta del servicio ya lo muestra cada vista; el banner no suma nada.
        });

    refresh();
    const timer = setInterval(refresh, 60_000);
    window.addEventListener(LICENSE_CHANGED, refresh);
    return () => {
      clearInterval(timer);
      window.removeEventListener(LICENSE_CHANGED, refresh);
    };
  }, []);

  if (!status?.needsAttention) return null;

  return (
    <div className={`license-banner ${status.isUsable ? "warn" : "blocked"}`}>
      <span>{status.message}</span>
      <a href="#license">Ver licencia</a>
    </div>
  );
}
