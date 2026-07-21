import { useEffect, useState } from 'react'
import { host, onHostEvent } from '../lib/host'

/**
 * Compact popover. Parent must be the shell root (position: relative).
 * Uses absolute coords only — never fixed, never flex flow (WebView2 breaks both).
 * Updates download/install in-panel (progress bar) — no native dialog card.
 * Verify lives on the Home dashboard chip — not here.
 */
export function SettingsDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [version, setVersion] = useState('—')
  const [checkOnLaunch, setCheckOnLaunch] = useState(false)
  const [line, setLine] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  /** null = hidden; -1 = indeterminate; 0–100 = determinate */
  const [progress, setProgress] = useState<number | null>(null)

  useEffect(() => {
    if (!open) return
    setLine(null)
    setProgress(null)
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
      if (e.key === 'Escape' && !busy) onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose, busy])

  useEffect(() => {
    return onHostEvent('settings.updateProgress', (data) => {
      const d = data as { status?: string; percent?: number }
      if (typeof d.status === 'string' && d.status.trim()) setLine(d.status)
      if (typeof d.percent === 'number') setProgress(d.percent)
    })
  }, [])

  if (!open) return null

  async function toggleCheckOnLaunch() {
    if (busy) return
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
    setLine('Checking GitHub…')
    setProgress(-1)
    try {
      const r = await host.checkUpdates()
      // Host message already includes "What's new" when a newer build exists.
      let text = r.message
      if (
        r.updateAvailable &&
        r.releaseSummary &&
        text &&
        !text.includes("What's new")
      ) {
        text = `Exo v${r.remoteVersion ?? '?'} is available (you have v${r.localVersion ?? version}).\n\nWhat's new:\n${r.releaseSummary}`
      }
      setLine(text)
      if (r.appVersion) setVersion(r.appVersion)
      if (r.shouldExit) {
        setProgress(100)
        setLine(text || 'Restarting into the new build…')
        return
      }
      if (r.alreadyLatest || !r.updateAvailable) {
        setProgress(null)
      } else if (r.installed) {
        setProgress(100)
      } else {
        setProgress(null)
      }
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Update check failed')
      setProgress(null)
    } finally {
      setBusy(false)
    }
  }

  async function openLogs() {
    if (busy) return
    try {
      const r = await host.openLogs()
      setLine(r.ok ? 'Opened logs.' : r.message || 'Could not open logs.')
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Could not open logs.')
    }
  }

  async function openIssues() {
    if (busy) return
    try {
      await host.openIssues()
      setLine('Opened GitHub.')
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Could not open browser.')
    }
  }

  const barWidth =
    progress == null
      ? 0
      : progress < 0
        ? 32
        : Math.max(4, Math.min(100, progress))

  return (
    <div className="pointer-events-none absolute inset-0 z-50" aria-hidden={false}>
      <button
        type="button"
        aria-label="Close settings"
        className="pointer-events-auto absolute inset-0 bg-page"
        style={{ opacity: 0.72 }}
        onClick={() => {
          if (!busy) onClose()
        }}
      />

      <div
        role="dialog"
        aria-label="Settings"
        className="glass specular pointer-events-auto absolute overflow-hidden rounded-2xl"
        style={{ top: 58, left: 12, width: 288 }}
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
              onClick={() => {
                if (!busy) onClose()
              }}
              disabled={busy}
              className="glass-chip flex h-7 w-7 items-center justify-center rounded-full text-xs text-muted hover:text-text disabled:opacity-40"
              aria-label="Close"
            >
              ✕
            </button>
          </div>

          <button
            type="button"
            onClick={() => void toggleCheckOnLaunch()}
            disabled={busy}
            className="glass-chip mb-2 flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left disabled:opacity-50"
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
            className="mb-2 w-full rounded-xl bg-white py-2.5 text-[13px] font-semibold text-black shadow-[0_0_20px_rgb(255_255_255/0.08)] disabled:opacity-40"
          >
            {busy
              ? progress != null && progress >= 0
                ? `Updating ${Math.round(progress)}%`
                : 'Updating…'
              : 'Check for updates'}
          </button>

          {progress != null && (
            <div className="glass-chip mb-2 rounded-xl px-2.5 py-2">
              <div className="mb-1.5 flex items-center justify-between gap-2">
                <p className="text-[10px] font-semibold tracking-[0.08em] text-muted">
                  {busy ? 'INSTALLING' : 'UPDATE'}
                </p>
                {progress >= 0 && (
                  <p className="text-[11px] tabular font-semibold text-text">
                    {Math.round(progress)}%
                  </p>
                )}
              </div>
              <div className="h-1.5 overflow-hidden rounded-full bg-black/50 ring-1 ring-white/10">
                <div
                  className={`h-full rounded-full bg-white transition-[width] duration-200 ${
                    progress < 0 ? 'animate-pulse' : ''
                  }`}
                  style={{ width: `${barWidth}%` }}
                />
              </div>
            </div>
          )}

          {line && (
            <div className="glass-chip mb-2 max-h-32 overflow-y-auto rounded-xl px-2.5 py-2">
              <p className="whitespace-pre-wrap text-[11px] leading-snug text-secondary">{line}</p>
            </div>
          )}

          <div className="grid grid-cols-2 gap-1.5">
            <button
              type="button"
              disabled={busy}
              onClick={() => void openLogs()}
              className="glass-chip rounded-xl py-2 text-[12px] font-semibold hover:brightness-110 disabled:opacity-40"
            >
              Logs
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => void openIssues()}
              className="glass-chip rounded-xl py-2 text-[12px] font-semibold hover:brightness-110 disabled:opacity-40"
            >
              Report issue
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
