import { useEffect, useState, type ReactNode } from "react";
import { StatusPanel } from "./components/StatusPanel";
import { InstrumentSettingsView } from "./components/InstrumentSettingsView";
import { CommunicationSettingsView } from "./components/CommunicationSettingsView";
import { TestMappingsView } from "./components/TestMappingsView";
import { CatalogView } from "./components/CatalogView";
import { CommsMonitor } from "./components/CommsMonitor";
import { EventsMonitor } from "./components/EventsMonitor";
import { PetitionsView } from "./components/PetitionsView";
import { AutoValidationView } from "./components/AutoValidationView";
import { LicenseView } from "./components/LicenseView";
import { LicenseBanner } from "./components/LicenseBanner";

type ViewId =
  | "status"
  | "monitor"
  | "petitions"
  | "instrument"
  | "communication"
  | "test-mappings"
  | "result-mappings"
  | "autovalidation"
  | "organisms"
  | "antibiotics"
  | "license";

interface NavGroup {
  title?: string;
  items: { id: ViewId; label: string }[];
}

const NAV: NavGroup[] = [
  {
    items: [
      { id: "status", label: "Estado" },
      { id: "license", label: "Licencia" }
    ]
  },
  {
    title: "Monitoreo",
    items: [
      { id: "monitor", label: "Comunicacion y eventos" },
      { id: "petitions", label: "Peticiones" }
    ]
  },
  {
    title: "Settings",
    items: [
      { id: "instrument", label: "Instrumento" },
      { id: "communication", label: "Comunicacion" },
      { id: "test-mappings", label: "Mapeo de tests" },
      { id: "result-mappings", label: "Mapeo de resultados" },
      { id: "autovalidation", label: "Autovalidacion" }
    ]
  },
  {
    title: "Microbiologia",
    items: [
      { id: "organisms", label: "Microorganismos" },
      { id: "antibiotics", label: "Antibioticos" }
    ]
  }
];

const VIEW_IDS = NAV.flatMap((g) => g.items.map((i) => i.id));

const readHash = (): ViewId => {
  const id = location.hash.slice(1) as ViewId;
  return VIEW_IDS.includes(id) ? id : "status";
};

export default function App() {
  const [view, setView] = useState<ViewId>(readHash);

  useEffect(() => {
    const onHash = () => setView(readHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  return (
    <div className="app">
      <header className="topbar">
        <h1>Synapsys Connector</h1>
        <span className="subtitle">Panel de control y monitor</span>
      </header>

      <LicenseBanner />

      <div className="shell">
        <nav className="sidebar">
          {NAV.map((group, gi) => (
            <div key={gi} className="nav-group">
              {group.title && <div className="nav-title">{group.title}</div>}
              {group.items.map((item) => (
                <a
                  key={item.id}
                  href={`#${item.id}`}
                  className={`nav-item ${view === item.id ? "active" : ""}`}
                >
                  {item.label}
                </a>
              ))}
            </div>
          ))}
        </nav>

        <main className="content">
          {/* Los monitores quedan montados aunque no se vean para no perder el trafico acumulado. */}
          <View active={view === "status"}>
            <StatusPanel />
          </View>
          <View active={view === "license"}>
            <LicenseView />
          </View>
          <View active={view === "monitor"} keepMounted>
            <div className="monitor-grid">
              <CommsMonitor />
              <EventsMonitor />
            </div>
          </View>
          <View active={view === "petitions"}>
            <PetitionsView />
          </View>
          <View active={view === "instrument"}>
            <InstrumentSettingsView />
          </View>
          <View active={view === "communication"}>
            <CommunicationSettingsView />
          </View>
          <View active={view === "test-mappings"}>
            <TestMappingsView />
          </View>
          <View active={view === "result-mappings"}>
            <CatalogView
              catalog="results"
              title="Mapeo de resultados"
              fileName="result-mappings.json"
              hint="Si el valor que manda el equipo coincide con un codigo, al LIS se informa la descripcion. Tambien traduce el estado de los cultivos (C3, NEGB...)."
            />
          </View>
          <View active={view === "autovalidation"}>
            <AutoValidationView />
          </View>
          <View active={view === "organisms"}>
            <CatalogView
              catalog="organisms"
              title="Microorganismos"
              fileName="organisms.json"
              hint="Nombre con el que sale cada microorganismo en el informe del cultivo. Un codigo que no este aca se informa tal cual."
            />
          </View>
          <View active={view === "antibiotics"}>
            <CatalogView
              catalog="antibiotics"
              title="Antibioticos"
              fileName="antibiotics.json"
              hint="Nombre con el que sale cada antibiotico en el antibiograma. Un codigo que no este aca se informa tal cual."
            />
          </View>
        </main>
      </div>
    </div>
  );
}

function View({ active, keepMounted, children }: { active: boolean; keepMounted?: boolean; children: ReactNode }) {
  if (!active && !keepMounted) return null;
  return <div hidden={!active}>{children}</div>;
}
