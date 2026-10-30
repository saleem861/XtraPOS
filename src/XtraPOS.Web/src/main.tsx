import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ApplicationRoot } from "./ApplicationRoot.tsx";
import { IdbDebugPanel } from "./components/IdbDebugPanel.tsx";
import { registerServiceWorker } from "./pwa/registerServiceWorker.ts";
import "./styles/app.css";

registerServiceWorker();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ApplicationRoot development={import.meta.env.DEV} pathname={window.location.pathname} />
    <IdbDebugPanel />
  </StrictMode>,
);
