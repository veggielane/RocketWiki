import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { startBrowserTelemetry } from './telemetry/startBrowserTelemetry'

// No-op unless an OTLP endpoint is configured, and never awaited — see
// `startBrowserTelemetry`.
void startBrowserTelemetry()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
