/**
 * AMOLED tweaks UI — Grok Build design, wired to the 4.8 host bridge.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Check,
  Coffee,
  ExternalLink,
  FileText,
  Heart,
  Home,
  Settings,
  Shield,
  X,
} from 'lucide-react'
import { cn } from '../lib/utils'
import {
  host,
  onHostEvent,
  type DashboardSnapshot,
  type LiveStats,
  type ModuleId,
  type ModuleStatus,
  type TextColour,
  type TextSize,
} from '../lib/host'
import { applyTextTheme, readCachedTextTheme, textThemeFrom, type TextTheme } from '../lib/exoTheme'

const LOGO = './assets/logos/'

type ModuleState = 'applied' | 'ready' | 'blocked' | 'missing' | 'partial' | 'checking'
type MarkFit = 'default' | 'wide' | 'tight'

type ModuleDef = {
  id: ModuleId
  label: string
  summary: string
  does: string[]
  logo: string
  invert: boolean
  fit: MarkFit
  plate: string
  accent: string
  options?: [string, string][]
  optionKey?: 'useGsync' | 'preferLowestLatency'
}

const MODULES: ModuleDef[] = [
  {
    id: 'nvidia',
    label: 'NVIDIA',
    summary: 'GPU control panel, latency mode, and power.',
    does: [
      'Sets low-latency / ultra-low latency mode',
      'Locks power management to prefer maximum performance',
      'Applies VRR / G-SYNC friendly frame-cap defaults',
      'Disables image-sharpening and extra post filters in the driver',
      'Writes stable 3D profile defaults for competitive games',
    ],
    logo: LOGO + 'nvidia.png',
    invert: true,
    fit: 'default',
    plate: 'linear-gradient(160deg,#8fd400,#3a5c00)',
    accent: '#76b900',
    options: [
      ['G-SYNC / VRR', 'Cap under refresh. Smoothest feel.'],
      ['Raw latency', 'No sync. Lowest input lag.'],
    ],
    optionKey: 'useGsync',
  },
  {
    id: 'amd',
    label: 'AMD',
    summary: 'AMD Chipset Software (Ryzen) or Radeon Software debloat when a Radeon GPU is present.',
    does: [
      'Ryzen + no Radeon: tracks AMD Chipset Software vs the newest package',
      'Applied only when the newest chipset package is installed',
      'With a Radeon GPU: turns off auto-start, updater and crash-reporter tasks',
      'With a Radeon GPU: turns off analytics / telemetry when those keys exist',
      'Repair restores a saved Radeon task/telemetry baseline when one was recorded',
    ],
    logo: LOGO + 'amd-mark.png',
    invert: false,
    fit: 'default',
    plate: 'linear-gradient(160deg,#ed1c24,#8b0a10)',
    accent: '#ed1c24',
  },
  {
    id: 'system',
    label: 'Windows',
    summary: 'Power plan, timers, and startup noise.',
    does: [
      'Switches to a high-performance power plan',
      'Tightens system timer resolution for steadier frame pacing',
      'Trims noisy startup apps that steal CPU at launch',
      'Prefers ultimate performance scheduling where available',
      'Keeps Game Mode friendly without bloat services',
    ],
    logo: LOGO + 'windows.svg',
    invert: true,
    fit: 'tight',
    plate: 'linear-gradient(145deg,#00a4ef,#0078d4 60%,#005a9e)',
    accent: '#0078d4',
  },
  {
    id: 'internet',
    label: 'Internet',
    summary: 'NIC offloads and stack prefs for ping.',
    does: [
      'Tunes TCP/IP stack for lower latency',
      'Strips or keeps NIC offloads based on profile',
      'Disables interrupt moderation when chasing ping',
      'Applies adapter power settings that stop link sleep',
      'Leaves a one-click path back to throughput mode',
    ],
    logo: LOGO + 'internet.png',
    invert: false,
    fit: 'default',
    plate: 'linear-gradient(160deg,#818cf8,#4f46e5 55%,#312e81)',
    accent: '#6366f1',
    options: [
      ['Lowest latency', 'Strip offloads. Best for FPS.'],
      ['High throughput', 'Keep offloads. Best for bulk.'],
    ],
    optionKey: 'preferLowestLatency',
  },
  {
    id: 'steam',
    label: 'Steam',
    summary: 'Overlay cost and launch weight.',
    does: [
      'Cuts Steam overlay cost in-game',
      'Keeps GPU acceleration where it helps the client',
      'Trims launch bloat without breaking the library',
      'Stops optional browser helpers from lingering',
      'Leaves friends list and downloads intact',
    ],
    logo: LOGO + 'steam.png',
    invert: true,
    fit: 'default',
    plate: 'linear-gradient(160deg,#2a475e,#0e141b)',
    accent: '#66c0f4',
  },
  {
    id: 'discord',
    label: 'Discord',
    summary: 'Hardware acceleration and auto-start.',
    does: [
      'Toggles hardware acceleration cleanly',
      'Disables auto-start on login when you want a quiet boot',
      'Keeps overlay off unless you turn it back on',
      'Reduces background CPU when Discord sits in the tray',
      'Does not touch servers, friends, or installs',
    ],
    logo: LOGO + 'discord.svg',
    invert: true,
    fit: 'tight',
    plate: 'linear-gradient(160deg,#5865f2,#3c45a5)',
    accent: '#5865f2',
  },
  {
    id: 'spotify',
    label: 'Spotify',
    summary: 'Startup and hardware acceleration.',
    does: [
      'Applies hardware acceleration for a lighter UI',
      'Stops Spotify from auto-starting with Windows',
      'Leaves playlists and cache structure alone',
      'Ready to write on first apply',
      'Easy reapply after Spotify updates reset settings',
    ],
    logo: LOGO + 'spotify.svg',
    invert: true,
    fit: 'tight',
    plate: 'linear-gradient(160deg,#1ed760,#0d8a38)',
    accent: '#1ed760',
  },
  {
    id: 'brave',
    label: 'Brave',
    summary: 'Hardware acceleration and background apps.',
    does: [
      'Tunes hardware acceleration',
      'Sets shields-friendly defaults',
      'Stops background apps when closed',
      'Leaves bookmarks and profiles alone',
    ],
    logo: LOGO + 'brave.svg',
    invert: false,
    fit: 'default',
    plate: 'linear-gradient(160deg,#ff6b3d,#a32a0c)',
    accent: '#fb542b',
  },
]

const STATUS: Record<ModuleState, string> = {
  applied: 'Applied',
  ready: 'Ready',
  blocked: 'Blocked',
  missing: 'Missing',
  partial: 'Partial',
  checking: 'Checking…',
}

const ACTION: Record<ModuleState, string> = {
  applied: 'Reapply',
  ready: 'Apply',
  blocked: 'Retry',
  missing: '',
  partial: 'Apply',
  checking: 'Apply',
}

function stateFromStatus(s: ModuleStatus | undefined | null): ModuleState {
  if (!s) return 'ready'
  const kind = (s.statusKind || '').toLowerCase()
  if (kind === 'missing' || /not installed/i.test(`${s.statusText} ${s.detail}`)) return 'missing'
  if (kind === 'blocked') return 'blocked'
  if (kind === 'partial') return 'partial'
  if (kind === 'applied' || s.isApplied) return 'applied'
  return 'ready'
}

function stateFromDash(m: DashboardSnapshot['modules'][number] | undefined): ModuleState {
  if (!m) return 'ready'
  if (m.state === 'missing') return 'missing'
  if (m.state === 'blocked') return 'blocked'
  if (m.state === 'applied' || m.applied) return 'applied'
  return m.state === 'ready' ? 'ready' : 'ready'
}

export function ExoApp() {
  const [states, setStates] = useState<Record<string, ModuleState>>(() =>
    Object.fromEntries(MODULES.map((m) => [m.id, 'ready' as ModuleState])),
  )
  const [picks, setPicks] = useState<Record<string, number>>({})
  const [selected, setSelected] = useState<string | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [progress, setProgress] = useState(0)
  const [moduleStatus, setModuleStatus] = useState<ModuleStatus | null>(null)
  const [detecting, setDetecting] = useState(false)
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [live, setLive] = useState<LiveStats | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [version, setVersion] = useState('—')
  const [theme, setTheme] = useState<TextTheme>(readCachedTextTheme)
  const [updateBusy, setUpdateBusy] = useState(false)
  const [updatePercent, setUpdatePercent] = useState(0)
  const [updateDone, setUpdateDone] = useState<string | null>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  // Default ready so icons stay clickable before the first dashboard tick.
  // Missing is only applied after a real host answer — never as a loading placeholder.
  const stateOf = useCallback((id: string) => states[id] ?? 'ready', [states])
  const active = selected ? MODULES.find((m) => m.id === selected) : null
  const activeState = active ? stateOf(active.id) : null

  useEffect(() => {
    applyTextTheme(theme)
  }, [theme])

  useEffect(() => {
    let cancelled = false
    void host.getSettings().then((s) => {
      if (cancelled) return
      if (s.appVersion) setVersion(s.appVersion)
      setTheme(textThemeFrom(s, readCachedTextTheme()))
    }).catch(() => {})
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const boot = async () => {
      for (let attempt = 0; attempt < 4 && !cancelled; attempt++) {
        try {
          const next = await host.getDashboard()
          if (cancelled) return
          setDash(next)
          const map: Record<string, ModuleState> = {}
          for (const row of next.modules) map[row.id] = stateFromDash(row)
          setStates((prev) => ({ ...prev, ...map }))
          return
        } catch (e) {
          if (attempt === 3 && !cancelled) {
            setError(e instanceof Error ? e.message : 'Failed to load dashboard')
          } else {
            await new Promise((r) => window.setTimeout(r, 250 * (attempt + 1)))
          }
        }
      }
    }
    void boot()
    const tick = async () => {
      try {
        const l = await host.getLive()
        if (!cancelled) setLive(l)
      } catch {
        /* best effort */
      }
    }
    const liveBoot = window.setTimeout(() => void tick(), 400)
    const interval = window.setInterval(() => void tick(), 1500)
    return () => {
      cancelled = true
      window.clearTimeout(liveBoot)
      window.clearInterval(interval)
    }
  }, [])

  useEffect(() => {
    if (!selected) {
      setModuleStatus(null)
      setError(null)
      return
    }
    let cancelled = false
    const id = selected as ModuleId
    setDetecting(true)
    setError(null)
    setModuleStatus(null)
    setStates((s) => ({ ...s, [id]: 'checking' }))
    ;(async () => {
      try {
        const next = await host.detect(id, { force: true })
        if (cancelled) return
        setModuleStatus(next)
        setStates((s) => ({ ...s, [id]: stateFromStatus(next) }))
        if (next.options?.useGsync != null) {
          setPicks((p) => ({ ...p, nvidia: next.options?.useGsync ? 0 : 1 }))
        }
        if (next.options?.preferLowestLatency != null) {
          setPicks((p) => ({
            ...p,
            internet: next.options?.preferLowestLatency ? 0 : 1,
          }))
        }
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Detect failed')
          setStates((s) => ({ ...s, [id]: 'ready' }))
        }
      } finally {
        if (!cancelled) setDetecting(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [selected])

  useEffect(() => {
    return onHostEvent('module.progress', (data) => {
      const event = data as { module?: string; percent?: number; status?: string }
      if (event.module && busyId && event.module !== busyId) return
      // UI only shows a percent — step text stays in logs, not on the button.
      if (typeof event.percent === 'number' && event.percent >= 0) {
        setProgress(Math.min(99, event.percent))
      }
    })
  }, [busyId])

  useEffect(() => {
    if (!menuOpen) return
    const onDown = (e: MouseEvent) => {
      if (!menuRef.current?.contains(e.target as Node)) setMenuOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setMenuOpen(false)
    }
    window.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
    }
  }, [menuOpen])

  const stop = useCallback(async () => {
    if (!busyId) return

    try {
      await host.cancel(busyId as ModuleId)
    } catch {
      /* settling */
    }
  }, [busyId])

  const run = useCallback(
    async (id: string, mode: 'apply' | 'repair' = 'apply') => {
      if (busyId) return
      setBusyId(id)
      setProgress(4)

      setError(null)
      const def = MODULES.find((m) => m.id === id)
      const pick = picks[id] ?? 0
      const options: Record<string, unknown> = { experimental: true }
      if (def?.optionKey === 'useGsync') options.useGsync = pick === 0
      if (def?.optionKey === 'preferLowestLatency') options.preferLowestLatency = pick === 0

      try {
        let next: ModuleStatus
        if (mode === 'repair') next = await host.repair(id as ModuleId)
        else next = await host.apply(id as ModuleId, options)
        setProgress(92)
        next = await host.detect(id as ModuleId, { force: true })
        setModuleStatus(next)
        setStates((s) => ({ ...s, [id]: stateFromStatus(next) }))
        setProgress(100)
        try {
          setDash(await host.getDashboard())
        } catch {
          /* ignore */
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : `${mode} failed`)
      } finally {
        setBusyId(null)
        setProgress(0)

      }
    },
    [busyId, picks],
  )

  const runVerify = useCallback(
    async (id: string) => {
      if (busyId) return
      setBusyId(id)
      setProgress(20)

      setDetecting(true)
      try {
        const next = await host.detect(id as ModuleId, { force: true })
        setModuleStatus(next)
        setStates((s) => ({ ...s, [id]: stateFromStatus(next) }))
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Verify failed')
      } finally {
        setBusyId(null)
        setProgress(0)

        setDetecting(false)
      }
    },
    [busyId],
  )

  const pick = (id: string) => {
    // Missing apps stay on the rail greyed-out; no detail page until installed.
    if (stateOf(id) === 'missing') return
    setSelected(id)
    setMenuOpen(false)
  }

  // One-shot Update: check → download → install. UI only shows a percent.
  useEffect(() => {
    return onHostEvent('settings.updateProgress', (data) => {
      const event = data as { percent?: number }
      if (typeof event.percent === 'number' && event.percent >= 0) {
        setUpdatePercent(Math.min(100, Math.round(event.percent)))
      }
    })
  }, [])

  const runUpdate = useCallback(async () => {
    if (updateBusy) return
    setUpdateBusy(true)
    setUpdatePercent(0)
    setUpdateDone(null)
    try {
      const result = await host.installUpdate()
      setUpdatePercent(100)
      if (result.alreadyLatest) {
        setUpdateDone('Up to date')
      } else if (result.installed || result.shouldExit) {
        setUpdateDone('Done')
      } else if (!result.updateAvailable) {
        setUpdateDone('Up to date')
      } else {
        setUpdateDone('Done')
      }
    } catch {
      setUpdateDone('Failed')
    } finally {
      setUpdateBusy(false)
    }
  }, [updateBusy])

  const setColour = (textColour: TextColour) => {
    const next = { ...theme, textColour }
    applyTextTheme(next)
    setTheme(next)
    void host.setSettings({ textColour }).catch(() => {})
  }

  const setSize = (textSize: TextSize) => {
    const next = { ...theme, textSize }
    applyTextTheme(next)
    setTheme(next)
    void host.setSettings({ textSize }).catch(() => {})
  }

  const doesLines = useMemo(() => {
    if (!active)
      return [] as Array<{
        key: string
        text: string
        active?: boolean
        /** Identity / advisory — never a red fail dot. */
        info?: boolean
      }>
    // Live detect rows carry real numbers (driver 610.88, BIOS rev, chipset package…).
    // Always prefer title + detail — title-only hid chipset/driver versions.
    // Prioritize checkable rows that are still off so Partial honesty is never
    // truncated behind a wall of green info lines (slice used to hide the miss).
    if (moduleStatus?.features?.length) {
      const isInfoTitle = (title: string) =>
        /\(info\)$/i.test(title) ||
        /\(firmware\)$/i.test(title) ||
        /^(CPU|Radeon GPU|AMD platform|Control Panel access|Matched to your display)\b/i.test(
          title,
        )
      const ranked = [...moduleStatus.features].sort((a, b) => {
        const aTitle = (a.title || '').trim()
        const bTitle = (b.title || '').trim()
        const aInfo = isInfoTitle(aTitle)
        const bInfo = isInfoTitle(bTitle)
        const aOff = a.active === false && !aInfo
        const bOff = b.active === false && !bInfo
        if (aOff !== bOff) return aOff ? -1 : 1
        if (aInfo !== bInfo) return aInfo ? 1 : -1
        return 0
      })
      return ranked.slice(0, 10).map((f, i) => {
        const title = (f.title || '').trim()
        const detail = (f.detail || '').trim()
        const text =
          title && detail && !detail.toLowerCase().startsWith(title.toLowerCase())
            ? `${title}: ${detail}`
            : title || detail
        const info = isInfoTitle(title)
        return { key: `${title}-${i}`, text, active: f.active, info }
      }).filter((x) => x.text)
    }
    return active.does.slice(0, 5).map((t, i) => ({
      key: `static-${i}`,
      text: t,
      active: undefined as boolean | undefined,
      info: true,
    }))
  }, [active, moduleStatus])

  const specs = dash?.specs
  const cpuPct = live?.hasCpu === false ? 0 : Math.round(live?.cpuPercent ?? 0)
  const gpuPct = live?.hasGpu === false ? 0 : Math.round(live?.gpuPercent ?? 0)
  const memPct = Math.round(live?.memoryPercent ?? 0)
  const diskPct =
    live?.hasDisk === false ? 0 : Math.round(live?.diskPercent ?? 0)

  return (
    <div className="exo-app flex h-dvh flex-col overflow-hidden bg-bg text-fg">
      <header className="exo-titlebar relative z-30 flex h-14 shrink-0 items-center border-b border-line-soft px-4">
        {/* Left: Home */}
        <div className="relative z-10 flex w-[4.5rem] shrink-0 items-center justify-start">
          <button
            type="button"
            onClick={() => setSelected(null)}
            className={cn(
              'icon-btn chrome exo-no-drag',
              !selected ? 'bg-elevated text-fg' : 'text-muted hover:bg-hover hover:text-fg',
            )}
            aria-label="Home"
          >
            <Home className="size-[18px]" strokeWidth={1.75} />
          </button>
        </div>

        {/* Center: module icons truly centered in the titlebar */}
        <nav
          className="exo-no-drag pointer-events-none absolute inset-x-0 flex items-center justify-center gap-2"
          aria-label="Modules"
        >
          <div className="pointer-events-auto flex items-center justify-center gap-2">
            {MODULES.map((m) => {
              const missing = stateOf(m.id) === 'missing'
              return (
                <button
                  key={m.id}
                  type="button"
                  disabled={missing}
                  title={missing ? `${m.label} — not installed` : m.label}
                  aria-label={m.label}
                  aria-disabled={missing || undefined}
                  onClick={() => pick(m.id)}
                  className={cn('icon-btn', missing && 'is-missing')}
                  data-on={!missing && selected === m.id ? 'true' : undefined}
                >
                  <span className="icon-plate" style={{ background: m.plate }}>
                    <LogoImg m={m} />
                  </span>
                </button>
              )
            })}
          </div>
        </nav>

        {/* Right: settings + close */}
        <div className="relative z-10 ml-auto flex shrink-0 items-center gap-0.5" ref={menuRef}>
          <button
            type="button"
            onClick={() => setMenuOpen((v) => !v)}
            className={cn(
              'icon-btn chrome',
              menuOpen ? 'bg-elevated text-fg' : 'text-muted hover:bg-hover hover:text-fg',
            )}
            aria-label="Settings"
            aria-expanded={menuOpen}
          >
            <Settings className="size-[18px]" strokeWidth={1.75} />
          </button>
          <button
            type="button"
            className="icon-btn chrome text-muted hover:bg-bad/20 hover:text-bad"
            aria-label="Close"
            title="Close"
            onClick={() => void host.close()}
          >
            <X className="size-[18px]" strokeWidth={1.75} />
          </button>

          {menuOpen && (
            <div
              role="menu"
              className="drop absolute top-[calc(100%+6px)] right-0 z-50 w-64 overflow-hidden border border-line bg-elevated shadow-[0_16px_48px_rgba(0,0,0,0.55)]"
              style={{ borderRadius: 'var(--radius-ui)' }}
            >
              <div className="flex items-center justify-between gap-2 px-3.5 py-2">
                <span className="text-[11px] font-semibold tracking-[0.14em] text-muted uppercase">
                  Settings
                </span>
                <span className="tabular text-[11px] text-faint">{version}</span>
              </div>
              <div className="h-px bg-line" />
              <MenuRow
                icon={<FileText className="size-4" />}
                label="View logs"
                onClick={() => {
                  void host.openLogs().catch(() => setError('Could not open logs folder'))
                }}
              />
              <div className="px-3.5 py-2.5">
                <button
                  type="button"
                  role="menuitem"
                  disabled={updateBusy}
                  onClick={() => void runUpdate()}
                  className="relative isolate flex h-9 w-full items-center justify-center overflow-hidden rounded-full bg-fg text-[13px] font-semibold tabular text-bg hover:opacity-90 disabled:opacity-80"
                  aria-label="Update Exo"
                >
                  {updateBusy && (
                    <span
                      className="absolute inset-y-0 left-0 bg-bg/15"
                      style={{ width: `${updatePercent}%` }}
                    />
                  )}
                  <span className="relative z-[1]">
                    {updateBusy
                      ? `${updatePercent}%`
                      : updateDone === 'Failed'
                        ? 'Retry update'
                        : updateDone === 'Up to date'
                          ? 'Up to date'
                          : updateDone === 'Done'
                            ? 'Updated'
                            : 'Update'}
                  </span>
                </button>
              </div>
              <div className="h-px bg-line" />
              <div className="px-3.5 py-2">
                <p className="mb-1.5 text-[11px] font-medium text-muted">Text color</p>
                <div className="grid grid-cols-2 gap-1.5">
                  {(
                    [
                      ['white', 'White'],
                      ['grey', 'Gray'],
                    ] as const
                  ).map(([id, label]) => (
                    <button
                      key={id}
                      type="button"
                      onClick={() => setColour(id)}
                      className={cn(
                        'h-7 rounded-lg border text-[12px] font-semibold transition-colors',
                        theme.textColour === id
                          ? 'border-fg/40 bg-fg text-bg'
                          : 'border-line text-fg hover:bg-hover',
                      )}
                    >
                      {label}
                    </button>
                  ))}
                </div>
                <p className="mt-2 mb-1.5 text-[11px] font-medium text-muted">Text size</p>
                <div className="grid grid-cols-3 gap-1.5">
                  {(
                    [
                      ['small', 'S'],
                      ['normal', 'M'],
                      ['large', 'L'],
                    ] as const
                  ).map(([id, label]) => (
                    <button
                      key={id}
                      type="button"
                      onClick={() => setSize(id)}
                      className={cn(
                        'h-7 rounded-lg border text-[12px] font-semibold transition-colors',
                        theme.textSize === id
                          ? 'border-fg/40 bg-fg text-bg'
                          : 'border-line text-fg hover:bg-hover',
                      )}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </div>
              <div className="h-px bg-line" />
              <MenuRow
                icon={<Coffee className="size-4" />}
                label="Buy me a coffee"
                href="https://www.buymeacoffee.com/UhhErix"
              />
              <MenuRow
                icon={<Heart className="size-4" />}
                label="Sponsor on GitHub"
                href="https://github.com/sponsors/ImAvgErix"
              />
              <MenuRow
                icon={<ExternalLink className="size-4" />}
                label="Documentation"
                href="https://github.com/ImAvgErix/ExoHub"
              />
              <MenuRow
                icon={<Shield className="size-4" />}
                label="Changelog"
                href="https://github.com/ImAvgErix/ExoHub/releases"
              />
            </div>
          )}
        </div>
      </header>

      <main className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {!active && (
          <div className="fade flex min-h-0 flex-1 items-center justify-center overflow-hidden p-6">
            {/* Content-sized meters — never flex-stretch to fill the canvas. */}
            <div className="w-full max-w-3xl space-y-3" aria-label="This PC">
              {error && !dash && (
                <div className="card p-4 text-[13px] text-bad">{error}</div>
              )}
              <div className="grid grid-cols-4 gap-3">
                <Res label="CPU" value={cpuPct} detail={specs?.cpu || '—'} />
                <Res label="GPU" value={gpuPct} detail={specs?.gpu || '—'} />
                <Res
                  label="Memory"
                  value={memPct}
                  detail={live?.memorySecondary || specs?.ram || '—'}
                />
                <Res
                  label="Disk"
                  value={diskPct}
                  detail={live?.diskSecondary || live?.diskUsed || 'Storage'}
                />
              </div>

              <div className="grid grid-cols-3 gap-3">
                <div className="card col-span-2 flex items-center justify-between gap-4 px-5 py-4">
                  <div className="flex items-center gap-6">
                    <div>
                      <p className="text-[12px] text-muted">Down</p>
                      <p className="tabular text-[1.5rem] font-semibold tracking-tight">
                        {live?.netDownMbps != null ? Math.round(live.netDownMbps) : '—'}
                        <span className="ml-1 text-[12px] font-medium text-muted">Mb/s</span>
                      </p>
                    </div>
                    <div>
                      <p className="text-[12px] text-muted">Up</p>
                      <p className="tabular text-[1.5rem] font-semibold tracking-tight">
                        {live?.netUpMbps != null ? Math.round(live.netUpMbps) : '—'}
                        <span className="ml-1 text-[12px] font-medium text-muted">Mb/s</span>
                      </p>
                    </div>
                  </div>
                  <p className="truncate text-right text-[12px] text-faint">
                    {live?.netLink || '—'}
                  </p>
                </div>

                <div className="card flex flex-col justify-center px-5 py-4">
                  <p className="truncate text-[14px] font-semibold">{specs?.os || 'Windows'}</p>
                </div>
              </div>
            </div>
          </div>
        )}

        {active && activeState && (
          <div className="fade flex min-h-0 flex-1 flex-col overflow-hidden px-8 py-6">
            <div className="mx-auto flex h-full w-full max-w-lg min-h-0 flex-col">
              <div className="flex shrink-0 flex-col items-center text-center">
                <div
                  className="grid size-16 place-items-center overflow-hidden"
                  style={{ borderRadius: 'var(--radius-ui)', background: active.plate }}
                >
                  <span className="icon-plate">
                    <LogoImg m={active} />
                  </span>
                </div>
                <div className="mt-3 flex flex-wrap items-center justify-center gap-2">
                  <h1 className="text-[26px] font-semibold tracking-tight">
                    {active.label}
                  </h1>
                  <span
                    className={cn(
                      'rounded-full px-2.5 py-0.5 text-[11px] font-semibold',
                      activeState === 'applied' && 'bg-good/15 text-good',
                      (activeState === 'ready' || activeState === 'partial') && 'bg-fg/10 text-fg',
                      activeState === 'blocked' && 'bg-bad/15 text-bad',
                      activeState === 'missing' && 'bg-faint/20 text-muted',
                      activeState === 'checking' && 'bg-fg/10 text-muted',
                    )}
                  >
                    {detecting || activeState === 'checking' ? 'Checking…' : STATUS[activeState]}
                  </span>
                </div>
                <p className="mt-2 line-clamp-2 max-w-md text-[13px] text-muted">
                  {moduleStatus?.detail || active.summary}
                </p>
              </div>

              <div className="mt-5 flex min-h-0 flex-1 flex-col gap-3 overflow-hidden">
                <div className="card min-h-0 flex-1 overflow-hidden p-4">
                  <p className="text-[12px] font-medium text-muted">
                    {moduleStatus?.features?.length ? 'Status' : 'What this does'}
                  </p>
                  <ul className="mt-3 space-y-2">
                    {doesLines.map((line) => {
                      // Good = green, miss = red. Never use module brand accent (AMD red)
                      // for status dots — that made CPU look like a failure.
                      const off = line.active === false && !line.info
                      const ok = line.active !== false || line.info
                      return (
                        <li
                          key={line.key}
                          className="flex items-start gap-2.5 text-[13px] leading-snug text-fg/90"
                        >
                          <span
                            className="mt-1.5 size-1.5 shrink-0 rounded-full"
                            style={{
                              background: off
                                ? 'var(--color-bad)'
                                : ok
                                  ? 'var(--color-good)'
                                  : 'var(--color-faint)',
                            }}
                          />
                          <span className="line-clamp-3">{line.text}</span>
                        </li>
                      )
                    })}
                  </ul>
                </div>

                {active.options && (
                  <div className="shrink-0">
                    <p className="mb-2 text-[12px] font-medium text-muted">Profile</p>
                    <div className="grid grid-cols-2 gap-2">
                      {active.options.map((opt, i) => {
                        const sel = (picks[active.id] ?? 0) === i
                        return (
                          <button
                            key={opt[0]}
                            type="button"
                            role="radio"
                            aria-checked={sel}
                            disabled={!!busyId || detecting}
                            onClick={() => setPicks((p) => ({ ...p, [active.id]: i }))}
                            className={cn(
                              'card flex items-start gap-3 p-3.5 text-left transition-colors',
                              sel ? 'border-fg/40 bg-elevated' : 'hover:bg-hover',
                            )}
                          >
                            <span
                              className={cn(
                                'mt-0.5 grid size-5 shrink-0 place-items-center rounded-full border',
                                sel ? 'border-fg bg-fg text-bg' : 'border-faint',
                              )}
                            >
                              {sel && <Check className="size-3" strokeWidth={3} />}
                            </span>
                            <span>
                              <span className="block text-[13px] font-semibold">{opt[0]}</span>
                              <span className="mt-0.5 block text-[11px] leading-snug text-muted">
                                {opt[1]}
                              </span>
                            </span>
                          </button>
                        )
                      })}
                    </div>
                  </div>
                )}

                {error && (
                  <div className="card shrink-0 border-bad/30 p-3 text-[12px] text-bad">
                    {error.split('\n')[0]}
                    <button
                      type="button"
                      className="mt-1.5 block text-[12px] font-semibold text-fg underline"
                      onClick={() => void host.openLogs()}
                    >
                      Open logs
                    </button>
                  </div>
                )}

                {activeState !== 'missing' && (
                  <div className="mt-auto flex shrink-0 flex-col items-center gap-2.5 pt-1">
                    <div className="flex w-full max-w-sm items-center justify-center gap-2">
                      <button
                        type="button"
                        disabled={!!busyId || detecting}
                        onClick={() => void runVerify(active.id)}
                        className="h-10 min-w-[6.5rem] flex-1 rounded-full border border-line px-5 text-[13px] font-semibold transition-colors hover:bg-hover disabled:opacity-40"
                      >
                        Verify
                      </button>
                      {(activeState === 'applied' ||
                        activeState === 'blocked' ||
                        activeState === 'partial') && (
                        <button
                          type="button"
                          disabled={!!busyId || detecting}
                          onClick={() => void run(active.id, 'repair')}
                          className="h-10 min-w-[6.5rem] flex-1 rounded-full border border-line px-5 text-[13px] font-semibold transition-colors hover:bg-hover disabled:opacity-40"
                        >
                          Repair
                        </button>
                      )}
                    </div>
                    <button
                      type="button"
                      disabled={detecting && busyId !== active.id}
                      onClick={() =>
                        busyId === active.id ? void stop() : void run(active.id, 'apply')
                      }
                      className="relative isolate flex h-11 w-full max-w-sm items-center justify-center overflow-hidden rounded-full bg-fg px-8 text-[15px] font-semibold tabular text-bg hover:opacity-90 disabled:opacity-50"
                    >
                      {busyId === active.id && (
                        <span
                          className="absolute inset-y-0 left-0 bg-bg/15"
                          style={{ width: `${Math.round(progress)}%` }}
                        />
                      )}
                      <span className="relative z-[1]">
                        {busyId === active.id
                          ? `${Math.round(progress)}%`
                          : ACTION[activeState] || 'Apply'}
                      </span>
                    </button>
                  </div>
                )}
              </div>
            </div>
          </div>
        )}
      </main>

    </div>
  )
}

function LogoImg({ m }: { m: ModuleDef }) {
  const [broken, setBroken] = useState(false)
  // Prefer the declared mark; if the asset 404s, fall back to legacy ./logos/
  // then to a letter plate so the rail never shows an empty broken-image icon.
  const primary = m.logo
  const legacy =
    primary.includes('/assets/logos/')
      ? primary.replace('/assets/logos/', '/logos/')
      : primary.startsWith('./assets/logos/')
        ? primary.replace('./assets/logos/', './logos/')
        : null
  const src = broken && legacy ? legacy : primary
  if (broken && !legacy) {
    return (
      <span className="grid size-full place-items-center text-[13px] font-bold text-fg/80" aria-hidden>
        {(m.label || '?').slice(0, 1)}
      </span>
    )
  }
  return (
    <img
      src={src}
      alt=""
      draggable={false}
      data-wide={m.fit === 'wide' ? 'true' : undefined}
      data-tight={m.fit === 'tight' ? 'true' : undefined}
      style={m.invert ? { filter: 'brightness(0) invert(1)' } : undefined}
      onError={() => setBroken(true)}
    />
  )
}

function Res({
  label,
  value,
  detail,
}: {
  label: string
  value: number
  detail: string
}) {
  const v = Math.round(value)
  const hot = v >= 85
  return (
    <div className="card flex flex-col p-4">
      <p className="text-[12px] font-medium text-muted">{label}</p>
      <p
        className={cn(
          'mt-2 tabular text-[2.25rem] font-semibold tracking-tight leading-none',
          hot ? 'text-bad' : 'text-fg',
        )}
      >
        {v}
        <span className="ml-0.5 text-[1rem] font-medium text-muted">%</span>
      </p>
      <div className="mt-3 h-1 overflow-hidden rounded-full bg-elevated">
        <div
          className={cn(
            'h-full rounded-full transition-[width] duration-700 ease-out',
            hot ? 'bg-bad' : 'bg-fg/70',
          )}
          style={{ width: `${v}%` }}
        />
      </div>
      <p className="mt-2 truncate text-[12px] text-faint">{detail}</p>
    </div>
  )
}

function MenuRow({
  icon,
  label,
  href,
  onClick,
}: {
  icon: React.ReactNode
  label: string
  href?: string
  onClick?: () => void
}) {
  const className =
    'flex w-full items-center gap-3 px-3.5 py-2.5 text-left text-[13px] font-medium transition-colors hover:bg-hover'
  const body = (
    <>
      <span className="text-muted">{icon}</span>
      <span className="flex-1">{label}</span>
      {href && <ExternalLink className="size-3 text-faint" />}
    </>
  )

  if (href) {
    return (
      <a
        href={href}
        className={className}
        target="_blank"
        rel="noreferrer"
        role="menuitem"
        onClick={(e) => {
          e.preventDefault()
          void host.openUrl(href).catch(() => {
            window.open(href, '_blank', 'noopener,noreferrer')
          })
          onClick?.()
        }}
      >
        {body}
      </a>
    )
  }

  return (
    <button type="button" className={className} role="menuitem" onClick={onClick}>
      {body}
    </button>
  )
}
