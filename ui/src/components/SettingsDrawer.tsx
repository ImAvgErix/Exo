import { useEffect, useState } from 'react'
import { host } from '../lib/host'

/**
 * Compact popover. Parent must be the shell root (position: relative).
 * Uses absolute coords only — never fixed, never flex flow (WebView2 breaks both).
 */
export function SettingsDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [version, setVersion] = useState('—')
  const [checkOnLaunch, setCheckOnLaunch] = useState(false)
  const [line, setLine] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!open) return
    setLine(null)
    void host
      .getSettings()
      .then((s) => {
        setVersion(s.appVersion)
        setCheckOnLaunch(!!s.checkForUpdatesOnLaunch)
      })
      .catch(() => setVersion('—'))
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  if (!open) return null

  async function toggleCheckOnLaunch() {
    const next = !checkOnLaunch
    setCheckOnLaunch(next)
    try {
      const s = await host.setSettings({ checkForUpdatesOnLaunch: next })
      setCheckOnLaunch(!!s.checkForUpdatesOnLaunch)
    } catch {
      setCheckOnLaunch(!next)
      setLine('Could not save.')
    }
  }

  async function checkUpdates() {
    setBusy(true)
    setLine(null)
    try {
      const r = await host.checkUpdates()
      setLine(r.message)
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Update check failed')
    } finally {
      setBusy(false)
    }
  }

  async function openLogs() {
    try {
      const r = await host.openLogs()
      setLine(r.ok ? 'Opened logs.' : r.message || 'Could not open logs.')
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Could not open logs.')
    }
  }

  async function openIssues() {
    try {
      await host.openIssues()
      setLine('Opened GitHub.')
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Could not open browser.')
    }
  }

  return (
    <div className="pointer-events-none absolute inset-0 z-50" aria-hidden={false}>
      {/* Scrim — absolute fill of shell, out of flex flow */}
      <button
        type="button"
        aria-label="Close settings"
        className="pointer-events-auto absolute inset-0 bg-page"
        style={{ opacity: 0.72 }}
        onClick={onClose}
      />

      {/* Panel — under gear inside the glass nav (pad 12 + bar ~48) */}
      <div
        role="dialog"
        aria-label="Settings"
        className="glass specular pointer-events-auto absolute overflow-hidden rounded-2xl"
        style={{ top: 58, left: 12, width: 272 }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="p-3">
          <div className="mb-2.5 flex items-center justify-between gap-2">
            <div>
              <p className="text-[15px] font-semibold leading-none tracking-tight">Settings</p>
              <p className="mt-1 text-[11px] text-muted">
                Exo <span className="tabular">{version}</span>
              </p>
            </div>
            <button
              type="button"
              onClick={onClose}
              className="glass-chip flex h-7 w-7 items-center justify-center rounded-full text-xs text-muted hover:text-text"
              aria-label="Close"
            >
              ✕
            </button>
          </div>

          <button
            type="button"
            onClick={() => void toggleCheckOnLaunch()}
            className="glass-chip mb-2 flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left"
          >
            <span className="min-w-0 flex-1 text-[12px] font-semibold">Updates on launch</span>
            <span
              className={`relative h-5 w-9 shrink-0 rounded-full ${
                checkOnLaunch ? 'bg-white' : 'bg-sunken ring-1 ring-glass-border'
              }`}
            >
              <span
                className={`absolute top-0.5 h-4 w-4 rounded-full shadow ${
                  checkOnLaunch ? 'left-4 bg-black' : 'left-0.5 bg-secondary'
                }`}
              />
            </span>
          </button>

          <button
            type="button"
            disabled={busy}
            onClick={() => void checkUpdates()}
            className="mb-2 w-full rounded-xl bg-white py-2 text-[13px] font-semibold text-black disabled:opacity-40"
          >
            {busy ? 'Checking…' : 'Check for updates'}
          </button>

          {line && (
            <p className="mb-2 line-clamp-2 text-[11px] leading-snug text-secondary">{line}</p>
          )}

          <div className="grid grid-cols-2 gap-1.5">
            <button
              type="button"
              onClick={() => void openLogs()}
              className="glass-chip rounded-xl py-2 text-[12px] font-semibold hover:brightness-110"
            >
              Logs
            </button>
            <button
              type="button"
              onClick={() => void openIssues()}
              className="glass-chip rounded-xl py-2 text-[12px] font-semibold hover:brightness-110"
            >
              Report issue
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
