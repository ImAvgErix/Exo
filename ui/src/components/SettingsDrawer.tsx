import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { host, onHostEvent } from '../lib/host'

const FALLBACK_COFFEE = 'https://www.buymeacoffee.com/UhhErix'
const FALLBACK_ISSUES = 'https://github.com/ImAvgErix/Exo/issues'

type ChangelogSection = { version: string; bullets: string[] }

/**
 * Settings popover — glass sheet aligned with exo-ui-craft.
 * Links: Logs · Changelog (in-app) · Report issue · Buy me a coffee.
 */
export function SettingsDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [version, setVersion] = useState('—')
  const [checkOnLaunch, setCheckOnLaunch] = useState(false)
  const [coffeeUrl, setCoffeeUrl] = useState(FALLBACK_COFFEE)
  const [issuesUrl, setIssuesUrl] = useState(FALLBACK_ISSUES)
  const [line, setLine] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState<number | null>(null)
  const [phase, setPhase] = useState<string | null>(null)
  const [changelogOpen, setChangelogOpen] = useState(false)
  const [changelogLoading, setChangelogLoading] = useState(false)
  const [changelogSections, setChangelogSections] = useState<ChangelogSection[]>([])
  const [changelogError, setChangelogError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setChangelogOpen(false)
      return
    }
    setLine(null)
    setProgress(null)
    setPhase(null)
    void host
      .getSettings()
      .then((s) => {
        setVersion(s.appVersion)
        setCheckOnLaunch(!!s.checkForUpdatesOnLaunch)
        if (s.buyMeACoffeeUrl) setCoffeeUrl(s.buyMeACoffeeUrl)
        if (s.issuesUrl) setIssuesUrl(s.issuesUrl)
      })
      .catch(() => setVersion('—'))
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || busy) return
      if (changelogOpen) {
        setChangelogOpen(false)
        return
      }
      onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose, busy, changelogOpen])

  useEffect(() => {
    return onHostEvent('settings.updateProgress', (data) => {
      const d = data as { status?: string; percent?: number }
      if (typeof d.percent === 'number') setProgress(d.percent)
      if (typeof d.status === 'string' && d.status.trim()) {
        const p = phaseFromStatus(d.status)
        setPhase(p)
        if (isMessageStatus(d.status)) setLine(d.status)
      }
    })
  }, [])

  const barWidth = useMemo(() => {
    if (progress == null) return 0
    if (progress < 0) return 32
    return Math.max(4, Math.min(100, progress))
  }, [progress])

  const phaseLabel = phase ?? (busy ? 'Working' : null)

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
    setLine(null)
    setPhase('Checking')
    setProgress(-1)
    try {
      const r = await host.checkUpdates()
      if (r.appVersion) setVersion(r.appVersion)

      if (r.shouldExit) {
        setProgress(100)
        setPhase('Restarting')
        setLine(r.message || 'Exo will close and reopen into the new build.')
        return
      }

      if (r.alreadyLatest || !r.updateAvailable) {
        setProgress(null)
        setPhase(null)
        setLine(r.message || `You're on the latest Exo (v${r.localVersion ?? version}).`)
        return
      }

      if (r.installed) {
        setProgress(100)
        setPhase('Done')
      } else {
        setProgress(null)
        setPhase(null)
      }

      const bits: string[] = []
      if (r.remoteVersion) {
        bits.push(
          `Exo v${r.remoteVersion} is ready` +
            (r.localVersion ? ` (you have v${r.localVersion})` : '') +
            '.',
        )
      }
      if (r.releaseSummary?.trim()) {
        bits.push("What's new")
        bits.push(
          ...r.releaseSummary
            .replace(/\r\n/g, '\n')
            .split('\n')
            .map((l) => l.trim().replace(/^[-•*·]\s*/, ''))
            .filter(Boolean)
            .slice(0, 6)
            .map((l) => `· ${l}`),
        )
      } else if (r.message && !r.message.toLowerCase().includes('download')) {
        bits.push(r.message)
      }
      setLine(bits.length ? bits.join('\n') : r.message || null)
    } catch (e) {
      setLine(e instanceof Error ? e.message : 'Update check failed')
      setProgress(null)
      setPhase(null)
    } finally {
      setBusy(false)
    }
  }

  async function openLink(url: string, okLine: string, failFallback = 'Could not open browser.') {
    if (busy) return
    try {
      const r = await host.openUrl(url)
      setLine(r.ok ? okLine : r.message || failFallback)
    } catch (e) {
      setLine(e instanceof Error ? e.message : failFallback)
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

  async function openChangelog() {
    if (busy) return
    setChangelogOpen(true)
    setChangelogLoading(true)
    setChangelogError(null)
    try {
      const r = await host.getChangelog()
      if (!r.ok || !r.sections?.length) {
        setChangelogSections([])
        setChangelogError(r.message || 'No changelog available.')
      } else {
        setChangelogSections(r.sections)
      }
    } catch (e) {
      setChangelogSections([])
      setChangelogError(e instanceof Error ? e.message : 'Could not load changelog.')
    } finally {
      setChangelogLoading(false)
    }
  }

  return (
    <div className="pointer-events-none absolute inset-0 z-50" aria-hidden={false}>
      <button
        type="button"
        aria-label="Close settings"
        className="pointer-events-auto absolute inset-0 bg-page"
        style={{ opacity: 0.72 }}
        onClick={() => {
          if (busy) return
          if (changelogOpen) setChangelogOpen(false)
          else onClose()
        }}
      />

      {/* Main settings sheet */}
      {!changelogOpen && (
        <div
          role="dialog"
          aria-label="Settings"
          className="glass specular pointer-events-auto absolute overflow-hidden rounded-2xl"
          style={{ top: 58, left: 12, width: 304 }}
          onClick={(e) => e.stopPropagation()}
        >
          <div className="flex flex-col gap-3 p-4">
            <div className="flex h-12 items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="text-[15px] font-semibold leading-none tracking-tight">Settings</p>
                <p className="mt-1.5 text-[11px] leading-none text-muted">
                  Exo <span className="tabular text-secondary">v{version}</span>
                </p>
              </div>
              <button
                type="button"
                onClick={() => {
                  if (!busy) onClose()
                }}
                disabled={busy}
                className="glass-chip flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-[11px] text-muted hover:text-text disabled:opacity-40"
                aria-label="Close"
              >
                ✕
              </button>
            </div>

            <section className="flex flex-col gap-3" aria-label="Updates">
              <p className="px-0.5 text-[10px] font-semibold tracking-[0.1em] text-muted">
                UPDATES
              </p>

              <button
                type="button"
                onClick={() => void toggleCheckOnLaunch()}
                disabled={busy}
                className="glass-chip flex h-12 w-full items-center gap-3 rounded-xl px-3 text-left disabled:opacity-50"
              >
                <span className="min-w-0 flex-1 text-[12px] font-semibold leading-tight">
                  Check on launch
                </span>
                <span
                  className={`relative h-5 w-9 shrink-0 rounded-full transition-colors ${
                    checkOnLaunch ? 'bg-white' : 'bg-sunken ring-1 ring-glass-border'
                  }`}
                  aria-hidden
                >
                  <span
                    className={`absolute top-0.5 h-4 w-4 rounded-full shadow transition-[left] ${
                      checkOnLaunch ? 'left-4 bg-black' : 'left-0.5 bg-secondary'
                    }`}
                  />
                </span>
              </button>

              <button
                type="button"
                disabled={busy}
                onClick={() => void checkUpdates()}
                className="flex h-11 w-full items-center justify-center rounded-xl bg-white text-[13px] font-semibold text-black shadow-[0_0_20px_rgb(255_255_255/0.08)] disabled:opacity-40"
              >
                {busy
                  ? progress != null && progress >= 0
                    ? `${phaseLabel ?? 'Updating'} · ${Math.round(progress)}%`
                    : phaseLabel
                      ? `${phaseLabel}…`
                      : 'Working…'
                  : 'Check for updates'}
              </button>

              {progress != null && (
                <div className="glass-chip rounded-xl px-3 py-2.5">
                  <div className="mb-2 flex items-center justify-between gap-2">
                    <p className="text-[10px] font-semibold tracking-[0.08em] text-muted">
                      {(phaseLabel ?? 'UPDATE').toUpperCase()}
                    </p>
                    {progress >= 0 && (
                      <p className="text-[11px] font-semibold tabular text-text">
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

              {line && !busy && (
                <div className="glass-chip max-h-32 overflow-y-auto rounded-xl px-3 py-2.5">
                  <p className="whitespace-pre-wrap text-[11px] leading-snug text-secondary">
                    {line}
                  </p>
                </div>
              )}
            </section>

            <section className="flex flex-col gap-3" aria-label="More">
              <p className="px-0.5 text-[10px] font-semibold tracking-[0.1em] text-muted">
                MORE
              </p>

              <div className="grid grid-cols-2 gap-3">
                <LinkChip disabled={busy} onClick={() => void openLogs()}>
                  Logs
                </LinkChip>
                <LinkChip disabled={busy} onClick={() => void openChangelog()}>
                  Changelog
                </LinkChip>
                <LinkChip
                  disabled={busy}
                  onClick={() => void openLink(issuesUrl, 'Opened GitHub issues.')}
                >
                  Report issue
                </LinkChip>
                <LinkChip
                  disabled={busy}
                  onClick={() =>
                    void openLink(coffeeUrl, 'Thanks — opened Buy Me a Coffee.')
                  }
                >
                  Buy me a coffee
                </LinkChip>
              </div>
            </section>
          </div>
        </div>
      )}

      {/* In-app changelog sheet */}
      {changelogOpen && (
        <div
          role="dialog"
          aria-label="Changelog"
          className="glass specular pointer-events-auto absolute flex max-h-[min(520px,calc(100%-80px))] w-[min(360px,calc(100%-24px))] flex-col overflow-hidden rounded-2xl"
          style={{ top: 58, left: 12 }}
          onClick={(e) => e.stopPropagation()}
        >
          <div className="flex h-12 shrink-0 items-center justify-between gap-3 border-b border-glass-border px-4">
            <div className="min-w-0">
              <p className="text-[15px] font-semibold leading-none tracking-tight">Changelog</p>
              <p className="mt-1.5 text-[11px] leading-none text-muted">
                What&apos;s new in Exo
              </p>
            </div>
            <button
              type="button"
              onClick={() => setChangelogOpen(false)}
              className="glass-chip flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-[11px] text-muted hover:text-text"
              aria-label="Back to settings"
            >
              ✕
            </button>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
            {changelogLoading && (
              <p className="text-[12px] text-muted">Loading…</p>
            )}
            {!changelogLoading && changelogError && (
              <div className="glass-chip rounded-xl px-3 py-2.5">
                <p className="text-[12px] text-secondary">{changelogError}</p>
              </div>
            )}
            {!changelogLoading &&
              !changelogError &&
              changelogSections.map((sec) => (
                <div key={sec.version} className="mb-4 last:mb-0">
                  <div className="mb-2 flex items-center gap-2">
                    <span className="glass-chip rounded-lg px-2 py-0.5 text-[11px] font-semibold tabular text-text">
                      v{sec.version}
                    </span>
                  </div>
                  {sec.bullets.length === 0 ? (
                    <p className="text-[12px] text-muted">No notes for this release.</p>
                  ) : (
                    <ul className="flex flex-col gap-1.5">
                      {sec.bullets.map((b, i) => (
                        <li
                          key={`${sec.version}-${i}`}
                          className="flex gap-2 text-[12px] leading-snug text-secondary"
                        >
                          <span className="mt-[6px] h-1 w-1 shrink-0 rounded-full bg-muted" />
                          <span>{b}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ))}
          </div>
        </div>
      )}
    </div>
  )
}

function LinkChip({
  children,
  onClick,
  disabled,
}: {
  children: ReactNode
  onClick: () => void
  disabled?: boolean
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="glass-chip flex h-11 items-center justify-center rounded-xl px-2 text-[12px] font-semibold text-secondary hover:text-text hover:brightness-110 disabled:opacity-40"
    >
      {children}
    </button>
  )
}

function phaseFromStatus(status: string): string {
  const t = status.toLowerCase()
  if (t.includes('restart') || t.includes('closing') || t.includes('reopen')) return 'Restarting'
  if (t.includes('apply') || t.includes('install') || t.includes('launch')) return 'Installing'
  if (t.includes('verify') || t.includes('sha') || t.includes('integrity')) return 'Verifying'
  if (t.includes('download')) return 'Downloading'
  if (t.includes('check') || t.includes('github')) return 'Checking'
  if (t.includes('what') && t.includes('new')) return 'Found'
  return 'Working'
}

function isMessageStatus(status: string): boolean {
  const t = status.toLowerCase()
  if (t.includes("what's new") || t.includes('what’s new')) return true
  if (t.includes('failed') || t.includes('could not') || t.includes('blocked')) return true
  if (t.includes('latest') || t.includes('you are on') || t.includes("you're on")) return true
  if (t.includes('download') || t.includes('verif') || t.includes('apply') || t.includes('install'))
    return false
  return status.includes('\n') || status.length > 80
}
