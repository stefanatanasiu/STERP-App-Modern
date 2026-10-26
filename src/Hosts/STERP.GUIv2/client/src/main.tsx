import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from '@/app/App'
import '@/styles/index.css'

// minimal client-error log on the GUIv2 host (message and page only)
window.addEventListener('error', (e) => report(e.message, e.error instanceof Error ? (e.error.stack ?? '') : ''))
window.addEventListener('unhandledrejection', (e) => report(String(e.reason?.message ?? e.reason), String(e.reason?.stack ?? '')))

function report(message: string, detail: string) {
  void fetch('/bff/client-errors', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-STERP-GUIv2': '1' },
    body: JSON.stringify({ message, detail, page: location.pathname }),
  }).catch(() => undefined)
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
