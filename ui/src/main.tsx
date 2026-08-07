import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/geist'
import '@fontsource-variable/geist-mono'
import './tweaks.css'
import './tokens.css'
import App from './App.tsx'
import { applyTextTheme, readCachedTextTheme } from './lib/exoTheme'

// Before the first render, not after: the host's settings arrive over an async bridge, so
// without the cached pair the window paints one frame of default white at scale 1.
applyTextTheme(readCachedTextTheme())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
