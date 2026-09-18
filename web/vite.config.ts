import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// El build cae directamente en wwwroot del servicio, que Kestrel sirve como SPA.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/Synapsys.Connector/wwwroot",
    emptyOutDir: true
  },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5081",
      "/ws": {
        target: "ws://localhost:5081",
        ws: true
      }
    }
  }
});
