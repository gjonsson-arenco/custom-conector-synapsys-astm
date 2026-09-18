import { StatusPanel } from "./components/StatusPanel";
import { MappingsView } from "./components/MappingsView";
import { CommsMonitor } from "./components/CommsMonitor";
import { EventsMonitor } from "./components/EventsMonitor";

export default function App() {
  return (
    <div className="app">
      <header className="topbar">
        <h1>Synapsys Connector</h1>
        <span className="subtitle">Panel de control y monitor</span>
      </header>

      <main className="layout">
        <div className="col">
          <StatusPanel />
          <MappingsView />
        </div>
        <div className="col">
          <CommsMonitor />
          <EventsMonitor />
        </div>
      </main>
    </div>
  );
}
