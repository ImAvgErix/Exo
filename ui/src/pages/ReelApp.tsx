import { useCallback, useEffect, useRef, useState } from 'react'
import {
  host,
  onHostEvent,
  type DashboardSnapshot,
  type GameHubSnapshot,
  type LiveStats,
  type ModuleId,
  type ModuleStatus,
} from '../lib/host'

/* ────────────────────────────────────────────────────────────────────────
   Exo — single-screen "reel" UI. A vertical 3D carousel of 7 systems on the
   left; the right pane shows the focused system's detail. Everything binds to
   the real host bridge (renders on mock data in a browser too).
   ──────────────────────────────────────────────────────────────────────── */

const MODULE_IDS = ['discord', 'brave', 'steam', 'internet', 'nvidia'] as const
const SLOTS = [...MODULE_IDS, 'games', 'settings'] as const
type SlotId = (typeof SLOTS)[number]

const META: Record<string, { label: string; accent: string }> = {
  discord: { label: 'Discord', accent: '#5865f2' },
  brave: { label: 'Brave', accent: '#fb542b' },
  steam: { label: 'Steam', accent: '#1a9fff' },
  internet: { label: 'Internet', accent: '#22d3ee' },
  nvidia: { label: 'NVIDIA', accent: '#76b900' },
  games: { label: 'Games', accent: '#f0b429' },
  settings: { label: 'Settings', accent: '#93a5c9' },
}

const TONE = { ok: '#34d399', bad: '#f87171', warn: '#fbbf24', neutral: '#8b8d92' }
const GLOW = { ok: 'rgba(52,211,153,0.55)', warn: 'rgba(251,191,36,0.5)', bad: 'rgba(248,113,113,0.5)' }
const L = '/logos/'

const STARS = Array.from({ length: 70 }, (_, i) => {
  const rand = (seed: number) => {
    const v = Math.sin(seed * 127.1 + 311.7) * 43758.5453
    return v - Math.floor(v)
  }
  return {
    left: `${Math.round(rand(i) * 96 + 2)}%`,
    top: `${Math.round(rand(i + 40) * 92 + 3)}%`,
    size: rand(i + 80) > 0.72 ? 2 : 1,
    dur: `${(2.4 + rand(i + 120) * 3.4).toFixed(1)}s`,
    delay: `${(rand(i + 160) * -4).toFixed(1)}s`,
  }
})

function pickText(hex: string) {
  const h = hex.replace('#', '')
  const r = parseInt(h.slice(0, 2), 16),
    g = parseInt(h.slice(2, 4), 16),
    b = parseInt(h.slice(4, 6), 16)
  return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? '#0b0b0d' : '#ffffff'
}

type ToneKey = keyof typeof TONE
function toneForKind(kind?: string): ToneKey {
  switch (kind) {
    case 'applied':
      return 'ok'
    case 'partial':
      return 'warn'
    case 'failed':
      return 'bad'
    default:
      return 'neutral'
  }
}

const MONO = { fontFamily: 'var(--font-mono)' }
const DISPLAY = { fontFamily: 'var(--font-display)' }

export function ReelApp() {
  const [slot, setSlot] = useState(0)
  const [paneSettled, setPaneSettled] = useState(true)
  const [live, setLive] = useState<LiveStats | null>(null)
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [details, setDetails] = useState<Record<string, ModuleStatus>>({})
  const [games, setGames] = useState<GameHubSnapshot | null>(null)
  const [selectedGame, setSelectedGame] = useState<string | null>(null)
  const [settings, setSettings] = useState<Awaited<ReturnType<typeof host.getSettings>> | null>(null)
  const [update, setUpdate] = useState<Awaited<ReturnType<typeof host.checkUpdates>> | null>(null)
  const [checkingUpdate, setCheckingUpdate] = useState(false)
  const [verifying, setVerifying] = useState(false)
  const [verifyLabel, setVerifyLabel] = useState('TAP TO RESCAN')
  const [busy, setBusy] = useState<{ scope: 'module' | 'game'; id: string; pct: number; label: string } | null>(null)
  const [welcomeOpen, setWelcomeOpen] = useState(false)
  const [gsync, setGsync] = useState(true)
  const [lowLatency, setLowLatency] = useState(true)
  const [gamePreset, setGamePreset] = useState<'potato' | 'optimized'>('optimized')
  const paneTimer = useRef<number | undefined>(undefined)
  const wheelLock = useRef(false)

  const focusedId = SLOTS[slot] as SlotId

  // ── bootstrap + live telemetry poll ─────────────────────────────────────
  useEffect(() => {
    let cancelled = false
    ;(async () => {
      try {
        const [d, s] = await Promise.all([host.getDashboard(), host.getSettings()])
        if (cancelled) return
        setDash(d)
        setLive(d.live)
        setSettings(s)
        if (s.welcomePromptSeen === false) setWelcomeOpen(true)
      } catch {
        /* keep skeleton */
      }
    })()
    const poll = window.setInterval(async () => {
      try {
        const l = await host.getLive()
        if (!cancelled) setLive(l)
      } catch {
        /* tick */
      }
    }, 1500)
    return () => {
      cancelled = true
      window.clearInterval(poll)
    }
  }, [])

  // ── lazy-load the focused system ─────────────────────────────────────────
  const loadModule = useCallback(async (id: string, force = false) => {
    try {
      const st = await host.detect(id as ModuleId, force ? { force: true } : undefined)
      setDetails((m) => ({ ...m, [id]: st }))
      if (st.options?.useGsync != null) setGsync(!!st.options.useGsync)
      if (st.options?.preferLowestLatency != null) setLowLatency(!!st.options.preferLowestLatency)
    } catch {
      /* detail stays skeleton */
    }
  }, [])

  useEffect(() => {
    if (MODULE_IDS.includes(focusedId as (typeof MODULE_IDS)[number])) {
      if (!details[focusedId]) void loadModule(focusedId)
    } else if (focusedId === 'games' && !games) {
      void host.listGames().then((g) => {
        setGames(g)
        setSelectedGame(g.selectedGameId)
      }).catch(() => {})
    } else if (focusedId === 'settings' && !update && !checkingUpdate) {
      // lightweight: don't auto-hit the network updater; user taps "Check"
    }
  }, [focusedId, details, games, update, checkingUpdate, loadModule])

  // ── progress events during apply ─────────────────────────────────────────
  useEffect(() => {
    return onHostEvent('module.progress', (data) => {
      const d = data as { module?: string; percent?: number; status?: string }
      setBusy((b) => {
        if (!b || (d.module && d.module !== b.id)) return b
        return {
          ...b,
          pct: typeof d.percent === 'number' && d.percent >= 0 ? d.percent : b.pct,
          label: d.status || b.label,
        }
      })
    })
  }, [])

  // ── slot navigation ──────────────────────────────────────────────────────
  const goSlot = useCallback((i: number) => {
    const next = Math.max(0, Math.min(SLOTS.length - 1, i))
    setSlot((cur) => {
      if (next === cur) return cur
      window.clearTimeout(paneTimer.current)
      setPaneSettled(false)
      paneTimer.current = window.setTimeout(() => setPaneSettled(true), 40)
      return next
    })
  }, [])

  const onWheel = useCallback(
    (e: React.WheelEvent) => {
      if (wheelLock.current) return
      wheelLock.current = true
      window.setTimeout(() => (wheelLock.current = false), 170)
      goSlot(slot + (e.deltaY > 0 ? 1 : -1))
    },
    [slot, goSlot],
  )

  // ── actions ──────────────────────────────────────────────────────────────
  async function runVerify() {
    if (verifying) return
    setVerifying(true)
    setVerifyLabel('SCANNING…')
    try {
      const r = await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      setDetails({})
      setVerifyLabel(`${r.applied}/${r.applied + r.partial + r.ready + r.missing} OK`)
    } catch {
      setVerifyLabel('SCAN FAILED')
    } finally {
      setVerifying(false)
      window.setTimeout(() => setVerifyLabel('TAP TO RESCAN'), 2200)
    }
  }

  async function applyModule(id: string) {
    setBusy({ scope: 'module', id, pct: 4, label: 'Starting…' })
    try {
      const st = await host.apply(id as ModuleId, {
        useGsync: gsync,
        preferLowestLatency: lowLatency,
      })
      setDetails((m) => ({ ...m, [id]: st }))
      const d = await host.getDashboard()
      setDash(d)
    } catch {
      await loadModule(id, true)
    } finally {
      setBusy(null)
    }
  }

  async function repairModule(id: string) {
    setBusy({ scope: 'module', id, pct: 4, label: 'Repairing…' })
    try {
      const st = await host.repair(id as ModuleId)
      setDetails((m) => ({ ...m, [id]: st }))
      const d = await host.getDashboard()
      setDash(d)
    } catch {
      await loadModule(id, true)
    } finally {
      setBusy(null)
    }
  }

  async function applyGame(gameId: string) {
    setBusy({ scope: 'game', id: gameId, pct: 4, label: 'Starting…' })
    try {
      const g = await host.applyGame(gameId, gamePreset, 'borderless')
      setGames(g)
    } catch {
      /* ignore */
    } finally {
      setBusy(null)
    }
  }

  async function repairGame(gameId: string) {
    setBusy({ scope: 'game', id: gameId, pct: 4, label: 'Repairing…' })
    try {
      const g = await host.repairGame(gameId)
      setGames(g)
    } catch {
      /* ignore */
    } finally {
      setBusy(null)
    }
  }

  async function checkForUpdate() {
    if (checkingUpdate) return
    setCheckingUpdate(true)
    try {
      setUpdate(await host.checkUpdates())
    } catch {
      /* ignore */
    } finally {
      setCheckingUpdate(false)
    }
  }

  function dismissWelcome() {
    setWelcomeOpen(false)
    void host.setSettings({ welcomePromptSeen: true })
  }

  // ── derived ──────────────────────────────────────────────────────────────
  const appliedCount = dash?.modules.filter((m) => m.applied).length ?? 0
  const moduleTotal = MODULE_IDS.length
  const gamesApplied = games?.games.filter((g) => g.applied).length ?? 0
  const anyGameApplied = gamesApplied > 0
  const optimized = appliedCount + (anyGameApplied ? 1 : 0)
  const ringDenom = moduleTotal + 1 // 5 modules + games
  const ringFrac = optimized / ringDenom
  const ringKey: ToneKey = ringFrac >= 0.83 ? 'ok' : ringFrac >= 0.5 ? 'warn' : 'bad'

  const statusToneFor = (id: string): ToneKey => {
    if (id === 'settings') return 'neutral'
    if (id === 'games') return anyGameApplied ? 'ok' : 'neutral'
    const det = details[id]
    if (det) return toneForKind(det.statusKind)
    return dash?.modules.find((m) => m.id === id)?.applied ? 'ok' : 'neutral'
  }
  const statusSubFor = (id: string): string => {
    if (id === 'settings') return 'App preferences'
    if (id === 'games') return games ? `${gamesApplied}/${games.games.length} optimized` : 'Per-game profiles'
    const det = details[id]
    if (det) return det.statusText || 'Ready'
    return dash?.modules.find((m) => m.id === id)?.applied ? 'Applied' : 'Ready'
  }

  const focusedAccent = META[focusedId]?.accent ?? '#93a5c9'

  const t = (accent: string, value: string) => ({ accent, value })
  const tel = live
    ? {
        cpu: t('#38bdf8', live.hasCpu === false ? '—' : `${Math.round(live.cpuPercent)}%`),
        gpu: t('#84cc16', live.hasGpu === false ? '—' : `${Math.round(live.gpuPercent)}%`),
        mem: t('#c4b5fd', `${Math.round(live.memoryPercent)}%`),
        net: t('#22d3ee', live.netLinkSpeed && live.netLinkSpeed !== '—' ? live.netLinkSpeed : live.netLink.split(' ')[0]),
      }
    : { cpu: t('#38bdf8', '—'), gpu: t('#84cc16', '—'), mem: t('#c4b5fd', '—'), net: t('#22d3ee', '—') }

  return (
    <div
      style={{
        position: 'fixed',
        inset: 0,
        overflow: 'hidden',
        background: '#000',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        color: '#f4f4f6',
      }}
    >
      <div
        style={{
          position: 'relative',
          width: 1200,
          height: 780,
          maxWidth: '100vw',
          maxHeight: '100vh',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          borderRadius: 30,
          background: 'radial-gradient(120% 100% at 50% 0%,#060607 0%,#000 62%)',
          border: '1px solid color-mix(in srgb, var(--exo-accent) 22%, rgba(130,150,210,0.14))',
          boxShadow:
            '0 55px 150px rgba(0,0,0,0.9), inset 0 1px 0 rgba(255,255,255,0.08), 0 0 130px -34px var(--exo-accent)',
          // Signature: the whole window tunes to the focused system's accent.
          ['--exo-accent' as string]: focusedAccent,
          transition: '--exo-accent .55s ease, border-color .55s ease, box-shadow .55s ease',
        } as React.CSSProperties}
      >
        {/* Accent light-wash — bleeds the focused system's color behind the pane */}
        <div
          style={{
            position: 'absolute',
            inset: 0,
            pointerEvents: 'none',
            zIndex: 2,
            background:
              'radial-gradient(72% 62% at 74% 40%, color-mix(in srgb, var(--exo-accent) 11%, transparent), transparent 62%)',
          }}
        />
        <div
          style={{
            position: 'absolute',
            top: '-30%',
            left: 0,
            width: '24%',
            height: '170%',
            background: 'linear-gradient(90deg,transparent,rgba(255,255,255,0.03),transparent)',
            pointerEvents: 'none',
            zIndex: 6,
            animation: 'exo-sweep 14s ease-in-out infinite',
          }}
        />

        {/* Header */}
        <header
          style={{
            position: 'relative',
            zIndex: 30,
            flexShrink: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            height: 54,
          }}
        >
          <div style={{ position: 'absolute', left: 24, top: '50%', transform: 'translateY(-50%)', display: 'flex', alignItems: 'center', gap: 18 }}>
            {(['CPU', 'GPU', 'MEM', 'NET'] as const).map((k) => {
              const v = k === 'CPU' ? tel.cpu : k === 'GPU' ? tel.gpu : k === 'MEM' ? tel.mem : tel.net
              return (
                <span key={k} style={{ display: 'flex', alignItems: 'baseline', gap: 5 }}>
                  <span style={{ ...MONO, fontSize: 12, fontWeight: 600, color: v.accent }}>{v.value}</span>
                  <span style={{ fontSize: 8, fontWeight: 700, letterSpacing: '0.14em', color: '#75767d' }}>{k}</span>
                </span>
              )
            })}
          </div>

          <button
            onClick={() => void runVerify()}
            disabled={verifying}
            aria-label="Verify all optimizers"
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 9,
              padding: '5px 14px 5px 7px',
              borderRadius: 999,
              background: 'transparent',
              border: '1px solid rgba(140,160,220,0.18)',
              cursor: 'pointer',
            }}
          >
            <span style={{ position: 'relative', width: 22, height: 22, borderRadius: '50%', flexShrink: 0 }}>
              <span
                style={{
                  position: 'absolute',
                  inset: 0,
                  borderRadius: '50%',
                  background: `conic-gradient(from -90deg,${TONE[ringKey]} ${Math.round(ringFrac * 360)}deg,rgba(255,255,255,0.1) 0)`,
                  WebkitMask: 'radial-gradient(farthest-side,transparent 58%,#000 59%)',
                  mask: 'radial-gradient(farthest-side,transparent 58%,#000 59%)',
                }}
              />
              <span
                style={{
                  position: 'absolute',
                  inset: 0,
                  borderRadius: '50%',
                  animation: `exo-pulse ${verifying ? '0.9s' : '3.6s'} ease-in-out infinite`,
                  boxShadow: `0 0 10px ${GLOW[ringKey]}`,
                }}
              />
            </span>
            <span style={{ ...MONO, fontSize: 10.5, fontWeight: 600, letterSpacing: '0.12em', color: '#f4f4f6' }}>
              {optimized}/{ringDenom} OPTIMIZED
            </span>
            <span style={{ ...MONO, fontSize: 8.5, letterSpacing: '0.1em', color: verifying ? TONE[ringKey] : '#75767d' }}>{verifyLabel}</span>
          </button>

          <div style={{ position: 'absolute', right: 20, top: '50%', transform: 'translateY(-50%)', display: 'flex', gap: 6 }}>
            <button onClick={() => void host.minimize()} aria-label="Minimize" title="Minimize" style={winBtn}>
              <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M2 6.5h8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>
            </button>
            <button onClick={() => void host.close()} aria-label="Close" title="Close" style={winBtn}>
              <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M3 3l6 6M9 3L3 9" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>
            </button>
          </div>
        </header>

        {/* Main */}
        <main style={{ position: 'relative', zIndex: 10, flex: 1, minHeight: 0, overflow: 'hidden' }}>
          {/* stars */}
          <div style={{ position: 'absolute', inset: 0, pointerEvents: 'none', zIndex: 1 }}>
            {STARS.map((st, i) => (
              <span
                key={i}
                style={{
                  position: 'absolute',
                  left: st.left,
                  top: st.top,
                  width: st.size,
                  height: st.size,
                  borderRadius: '50%',
                  background: '#fff',
                  animation: `exo-twinkle ${st.dur} ease-in-out infinite`,
                  animationDelay: st.delay,
                }}
              />
            ))}
          </div>

          {/* Reel */}
          <div onWheel={onWheel} style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 340, zIndex: 10, perspective: 950 }}>
            <div style={{ position: 'absolute', left: 34, right: 26, top: '50%', height: 1, marginTop: -52, background: 'linear-gradient(90deg,rgba(255,255,255,0.14),transparent)', pointerEvents: 'none' }} />
            <div style={{ position: 'absolute', left: 34, right: 26, top: '50%', height: 1, marginTop: 52, background: 'linear-gradient(90deg,rgba(255,255,255,0.14),transparent)', pointerEvents: 'none' }} />
            <div style={{ position: 'absolute', left: 20, top: '50%', width: 3, height: 56, marginTop: -28, borderRadius: 2, background: 'var(--exo-accent)', boxShadow: '0 0 12px var(--exo-accent)', pointerEvents: 'none' }} />
            <span style={{ position: 'absolute', left: 34, top: 16, ...MONO, fontSize: 9, fontWeight: 700, letterSpacing: '0.18em', color: '#5d5e66', zIndex: 20 }}>SYSTEM {slot + 1}/7</span>
            <button onClick={() => goSlot(slot - 1)} aria-label="Previous" style={{ ...arrowBtn, top: 36 }}>
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none"><path d="M6 15l6-6 6 6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" /></svg>
            </button>
            <button onClick={() => goSlot(slot + 1)} aria-label="Next" style={{ ...arrowBtn, bottom: 16 }}>
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none"><path d="M6 9l6 6 6-6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" /></svg>
            </button>

            {SLOTS.map((id, i) => {
              const off = i - slot
              const abs = Math.abs(off)
              const focused = off === 0
              const accent = META[id].accent
              const toneKey = statusToneFor(id)
              const isSettings = id === 'settings'
              return (
                <button
                  key={id}
                  onClick={() => goSlot(i)}
                  title={META[id].label}
                  style={{
                    position: 'absolute',
                    left: 34,
                    right: 16,
                    top: '50%',
                    height: 88,
                    marginTop: -44,
                    display: 'flex',
                    alignItems: 'center',
                    gap: 15,
                    padding: '0 14px',
                    border: 'none',
                    background: 'transparent',
                    cursor: 'pointer',
                    textAlign: 'left',
                    // Compositor-only: translateY (not margin-top) + spring settle.
                    transform: `translateY(${off * 90}px) perspective(800px) rotateX(${off * -16}deg) scale(${focused ? 1.06 : Math.max(0.8, 1 - abs * 0.08)})`,
                    opacity: abs > 1.6 ? 0 : focused ? 1 : 0.45,
                    zIndex: 40 - abs,
                    pointerEvents: abs > 1.6 ? 'none' : 'auto',
                    willChange: 'transform, opacity',
                    transition: 'transform .6s var(--ease-spring), opacity .4s ease',
                  }}
                >
                  <span style={{ position: 'relative', flexShrink: 0, width: 54, height: 54, borderRadius: '50%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    <span style={{ position: 'absolute', inset: 0, borderRadius: '50%', border: `1px solid color-mix(in srgb, ${accent} 45%, transparent)`, boxShadow: `0 0 16px color-mix(in srgb, ${accent} ${focused ? '45%' : '12%'}, transparent),inset 0 0 12px color-mix(in srgb, ${accent} 14%, transparent)`, transition: 'box-shadow .3s' }} />
                    <span style={{ position: 'absolute', left: '50%', top: '50%', width: 34, height: 34, margin: '-17px 0 0 -17px', borderRadius: '50%', background: `radial-gradient(circle,color-mix(in srgb, ${accent} 30%, transparent) 0%,transparent 72%)` }} />
                    {isSettings ? (
                      <svg width="20" height="20" viewBox="0 0 24 24" fill="none" style={{ position: 'relative' }}>
                        <path d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z" stroke="#c9cfdd" strokeWidth="1.7" />
                        <path d="M19.4 13.5a7.8 7.8 0 0 0 .05-1.5 7.8 7.8 0 0 0-.05-1.5l2.05-1.6-2-3.45-2.45.95a7.6 7.6 0 0 0-2.6-1.5L14 2h-4l-.4 2.9a7.6 7.6 0 0 0-2.6 1.5l-2.45-.95-2 3.45 2.05 1.6a7.8 7.8 0 0 0 0 3l-2.05 1.6 2 3.45 2.45-.95a7.6 7.6 0 0 0 2.6 1.5L10 22h4l.4-2.9a7.6 7.6 0 0 0 2.6-1.5l2.45.95 2-3.45-2.05-1.6Z" stroke="#c9cfdd" strokeWidth="1.4" strokeLinejoin="round" />
                      </svg>
                    ) : (
                      <img src={`${L}${id}.png`} alt="" draggable={false} style={{ position: 'relative', width: 24, height: 24, objectFit: 'contain', filter: `drop-shadow(0 0 6px color-mix(in srgb, ${accent} 60%, transparent))` }} />
                    )}
                    <span style={{ position: 'absolute', top: 1, right: 1, width: 8, height: 8, borderRadius: '50%', background: TONE[toneKey], border: '1.5px solid #000', boxShadow: `0 0 8px ${TONE[toneKey]}` }} />
                  </span>
                  <span style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
                    <span style={{ ...MONO, fontSize: 13, fontWeight: 700, letterSpacing: '0.13em', color: focused ? '#f4f4f6' : '#8a8b91', transition: 'color .3s' }}>{META[id].label.toUpperCase()}</span>
                    <span style={{ fontSize: 10, letterSpacing: '0.06em', color: focused ? accent : '#5d5e66', transition: 'color .3s' }}>{statusSubFor(id)}</span>
                  </span>
                </button>
              )
            })}
          </div>

          {/* Detail pane */}
          <div
            style={{
              position: 'absolute',
              left: 348,
              right: 28,
              top: 16,
              bottom: 20,
              zIndex: 5,
              opacity: paneSettled ? 1 : 0,
              transform: `translateX(${paneSettled ? 0 : 26}px) scale(${paneSettled ? 1 : 0.97})`,
              transition: 'opacity .4s cubic-bezier(0.16,1,0.3,1),transform .4s cubic-bezier(0.16,1,0.3,1)',
            }}
          >
            {MODULE_IDS.includes(focusedId as (typeof MODULE_IDS)[number]) && (
              <ModulePane
                id={focusedId}
                det={details[focusedId] ?? null}
                busy={busy?.scope === 'module' && busy.id === focusedId ? busy : null}
                gsync={gsync}
                lowLatency={lowLatency}
                onGsync={setGsync}
                onLowLatency={setLowLatency}
                onApply={() => void applyModule(focusedId)}
                onRepair={() => void repairModule(focusedId)}
              />
            )}
            {focusedId === 'games' && (
              <GamesPane
                snap={games}
                selectedGame={selectedGame}
                onSelectGame={(gid) => {
                  setSelectedGame(gid)
                  void host.listGames(gid).then(setGames).catch(() => {})
                }}
                preset={gamePreset}
                onPreset={setGamePreset}
                busy={busy?.scope === 'game' ? busy : null}
                onApply={(gid) => void applyGame(gid)}
                onRepair={(gid) => void repairGame(gid)}
              />
            )}
            {focusedId === 'settings' && (
              <SettingsPane
                settings={settings}
                update={update}
                checking={checkingUpdate}
                onCheck={() => void checkForUpdate()}
                onToggleUpdates={(v) => {
                  setSettings((s) => (s ? { ...s, checkForUpdatesOnLaunch: v } : s))
                  void host.setSettings({ checkForUpdatesOnLaunch: v })
                }}
                onLogs={() => void host.openLogs()}
              />
            )}
          </div>

          {welcomeOpen && <Welcome onDismiss={dismissWelcome} onTip={() => void host.openUrl(settings?.buyMeACoffeeUrl)} />}
        </main>
      </div>
    </div>
  )
}

/* ── Module detail pane ──────────────────────────────────────────────────── */
function ModulePane({
  id,
  det,
  busy,
  gsync,
  lowLatency,
  onGsync,
  onLowLatency,
  onApply,
  onRepair,
}: {
  id: string
  det: ModuleStatus | null
  busy: { pct: number; label: string } | null
  gsync: boolean
  lowLatency: boolean
  onGsync: (v: boolean) => void
  onLowLatency: (v: boolean) => void
  onApply: () => void
  onRepair: () => void
}) {
  const accent = META[id].accent
  const toneKey = busy ? 'neutral' : toneForKind(det?.statusKind)
  const headline = busy ? 'Working' : det?.statusText || 'Ready'
  const detail = busy ? busy.label : det?.detail || 'Apply to optimize this app.'
  const features = det?.features ?? []
  const hasProfile = id === 'internet' || id === 'nvidia'
  const isApplied = det?.statusKind === 'applied' || det?.statusKind === 'partial'
  const applyLabel = busy ? 'Working…' : isApplied ? 'Reapply' : 'Apply'
  const profileOpts = id === 'nvidia'
    ? [{ id: 'raw', label: 'Raw latency', on: !gsync, set: () => onGsync(false) }, { id: 'gsync', label: 'G-SYNC / VRR', on: gsync, set: () => onGsync(true) }]
    : [{ id: 'lat', label: 'Lowest latency', on: lowLatency, set: () => onLowLatency(true) }, { id: 'tp', label: 'High throughput', on: !lowLatency, set: () => onLowLatency(false) }]
  const highlightRight = id === 'nvidia' ? gsync : !lowLatency

  return (
    <section style={{ position: 'relative', height: '100%', display: 'flex', flexDirection: 'column', gap: 16, padding: '22px 26px' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 18 }}>
        <div style={{ position: 'relative', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', width: 62, height: 62, borderRadius: '50%' }}>
          <span style={{ position: 'absolute', inset: 0, borderRadius: '50%', border: `1px solid color-mix(in srgb, ${accent} 50%, transparent)`, boxShadow: `0 0 20px color-mix(in srgb, ${accent} 30%, transparent),inset 0 0 14px color-mix(in srgb, ${accent} 16%, transparent)` }} />
          <img src={`${L}${id}.png`} alt="" draggable={false} style={{ position: 'relative', width: 32, height: 32, objectFit: 'contain', filter: `drop-shadow(0 0 7px color-mix(in srgb, ${accent} 60%, transparent))` }} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
            <p style={{ margin: 0, ...DISPLAY, fontSize: 21, fontWeight: 600, letterSpacing: '-0.01em' }}>{META[id].label}</p>
            <StatusPill tone={TONE[toneKey]} label={headline} />
          </div>
          <p style={{ margin: '5px 0 0', fontSize: 12, lineHeight: 1.5, color: '#8a8b91', overflow: 'hidden', textOverflow: 'ellipsis', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' }}>{detail}</p>
        </div>
        {hasProfile && (
          <div style={{ flexShrink: 0, position: 'relative', display: 'flex', background: 'rgba(0,0,0,0.5)', border: '1px solid rgba(255,255,255,0.1)', borderRadius: 13, padding: 4, width: 220 }}>
            <span style={{ position: 'absolute', top: 4, bottom: 4, left: 4, width: 'calc(50% - 4px)', borderRadius: 9, background: accent, boxShadow: `0 2px 14px color-mix(in srgb, ${accent} 55%, transparent)`, transition: 'transform .5s cubic-bezier(0.16,1,0.3,1)', transform: `translateX(${highlightRight ? '100%' : '0%'})` }} />
            {profileOpts.map((o) => (
              <button key={o.id} onClick={o.set} style={{ position: 'relative', zIndex: 1, flex: 1, padding: '9px 8px', borderRadius: 9, fontSize: 11, fontWeight: 600, color: o.on ? pickText(accent) : '#c4c4ca', transition: 'color .3s', border: 'none', background: 'transparent', cursor: 'pointer' }}>{o.label}</button>
            ))}
          </div>
        )}
      </div>

      <div key={id} style={{ flex: 1, minHeight: 0, display: 'grid', gridTemplateColumns: 'repeat(2,minmax(0,1fr))', gridAutoRows: '1fr', gap: '12px 16px', alignContent: 'stretch' }}>
        {(features.length ? features : Array.from({ length: 6 }, () => null)).map((f, i) => (
          <FeatureChip key={i} index={i} title={f?.title ?? '—'} active={!!f?.active} />
        ))}
      </div>

      {busy && <ProgressBar pct={busy.pct} accent={accent} />}

      <div style={{ marginTop: 'auto', display: 'flex', alignItems: 'center', gap: 10 }}>
        <button
          onClick={onApply}
          disabled={!!busy}
          style={{ flex: 1, padding: 14, borderRadius: 14, background: `linear-gradient(180deg, ${accent}, color-mix(in srgb, ${accent} 74%, #000))`, color: pickText(accent), ...DISPLAY, fontSize: 13.5, fontWeight: 600, boxShadow: `0 8px 26px color-mix(in srgb, ${accent} 34%, transparent)`, opacity: busy ? 0.6 : 1, border: 'none', cursor: 'pointer' }}
        >
          {applyLabel}
        </button>
        <button onClick={onRepair} disabled={!!busy} style={repairBtn(busy)}>Repair</button>
      </div>
    </section>
  )
}

/* ── Games pane ──────────────────────────────────────────────────────────── */
function GamesPane({
  snap,
  selectedGame,
  onSelectGame,
  preset,
  onPreset,
  busy,
  onApply,
  onRepair,
}: {
  snap: GameHubSnapshot | null
  selectedGame: string | null
  onSelectGame: (gid: string) => void
  preset: 'potato' | 'optimized'
  onPreset: (p: 'potato' | 'optimized') => void
  busy: { id: string; pct: number; label: string } | null
  onApply: (gid: string) => void
  onRepair: (gid: string) => void
}) {
  if (!snap) return <PaneSkeleton label="Reading installed games…" />
  const list = snap.games
  const selIdx = Math.max(0, list.findIndex((g) => g.id === (selectedGame ?? snap.selectedGameId)))
  const sel = list[selIdx]
  if (!sel) return <PaneSkeleton label="No games detected" />
  const accent = '#f0b429'
  const det = snap.selected
  const toneKey = busy && busy.id === sel.id ? 'neutral' : toneForKind(det?.statusKind)
  const headline = busy && busy.id === sel.id ? 'Working' : sel.applied ? 'Applied' : sel.statusText || 'Ready'
  const detail = busy && busy.id === sel.id ? busy.label : sel.detail || 'Apply a per-game profile.'
  const features = det?.features ?? []

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div style={{ position: 'relative', height: 200, flexShrink: 0 }}>
        {list.map((g, i) => {
          const offset = i - selIdx
          const abs = Math.abs(offset)
          return (
            <button
              key={g.id}
              onClick={() => onSelectGame(g.id)}
              title={g.title}
              style={{
                position: 'absolute',
                left: '50%',
                bottom: 8,
                width: 102,
                height: 138,
                marginLeft: -51,
                borderRadius: 13,
                overflow: 'hidden',
                padding: 0,
                background: '#000',
                border: `1.5px solid ${offset === 0 ? accent : 'rgba(255,255,255,0.14)'}`,
                boxShadow: offset === 0 ? `0 14px 34px rgba(0,0,0,0.5), 0 0 26px color-mix(in srgb, ${accent} 45%, transparent)` : '0 8px 20px rgba(0,0,0,0.4)',
                transform: `translate(${offset * 56}px, ${offset === 0 ? -12 : Math.min(abs * 5, 10)}px) rotate(${Math.max(-24, Math.min(24, offset * 5))}deg) scale(${offset === 0 ? 1.06 : Math.max(0.85, 1 - abs * 0.05)})`,
                zIndex: 100 - abs,
                opacity: offset === 0 ? 1 : Math.max(0.55, 1 - abs * 0.1),
                transition: 'transform .45s cubic-bezier(0.16,1,0.3,1),opacity .3s,border-color .3s',
                cursor: 'pointer',
              }}
            >
              {g.icon && <img src={g.icon} alt="" draggable={false} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'cover' }} />}
              <span style={{ position: 'absolute', inset: 0, background: 'linear-gradient(180deg,rgba(0,0,0,0.02) 55%,rgba(0,0,0,0.85) 100%)' }} />
              <span style={{ position: 'absolute', left: 9, right: 9, bottom: 8, textAlign: 'left', fontSize: 10, fontWeight: 600, lineHeight: 1.2, color: '#fff', textShadow: '0 1px 6px rgba(0,0,0,0.7)' }}>{g.title}</span>
            </button>
          )
        })}
      </div>

      <section style={{ position: 'relative', flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 13, padding: '16px 22px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
              <p style={{ margin: 0, ...DISPLAY, fontSize: 18, fontWeight: 600, letterSpacing: '-0.01em' }}>{sel.title}</p>
              <StatusPill tone={TONE[toneKey]} label={headline} small />
            </div>
            <p style={{ margin: '4px 0 0', fontSize: 12, lineHeight: 1.5, color: '#8a8b91', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{detail}</p>
          </div>
          <div style={{ flexShrink: 0, position: 'relative', display: 'flex', background: 'rgba(0,0,0,0.5)', border: '1px solid rgba(255,255,255,0.1)', borderRadius: 13, padding: 4, width: 196 }}>
            <span style={{ position: 'absolute', top: 4, bottom: 4, left: 4, width: 'calc(50% - 4px)', borderRadius: 9, background: accent, boxShadow: `0 2px 14px color-mix(in srgb, ${accent} 55%, transparent)`, transition: 'transform .5s cubic-bezier(0.16,1,0.3,1)', transform: `translateX(${preset === 'optimized' ? '100%' : '0%'})` }} />
            {(['potato', 'optimized'] as const).map((p) => (
              <button key={p} onClick={() => onPreset(p)} style={{ position: 'relative', zIndex: 1, flex: 1, padding: '8px 8px', borderRadius: 9, fontSize: 11, fontWeight: 600, textTransform: 'capitalize', color: preset === p ? pickText(accent) : '#c4c4ca', transition: 'color .3s', border: 'none', background: 'transparent', cursor: 'pointer' }}>{p}</button>
            ))}
          </div>
        </div>

        <div key={sel.id} style={{ flex: 1, minHeight: 0, display: 'grid', gridTemplateColumns: 'repeat(3,minmax(0,1fr))', gridAutoRows: 'minmax(0,1fr)', gap: '9px 10px', alignContent: 'stretch' }}>
          {(features.length ? features : Array.from({ length: 6 }, () => null)).map((f, i) => (
            <FeatureChip key={i} index={i} title={f?.title ?? '—'} active={!!f?.active} small />
          ))}
        </div>

        {busy && busy.id === sel.id && <ProgressBar pct={busy.pct} accent={accent} />}

        <div style={{ marginTop: 'auto', display: 'flex', alignItems: 'center', gap: 10 }}>
          {sel.installed ? (
            <>
              <button
                onClick={() => onApply(sel.id)}
                disabled={!!busy}
                style={{ flex: 1, padding: 12, borderRadius: 13, background: `linear-gradient(180deg, ${accent}, color-mix(in srgb, ${accent} 74%, #000))`, color: pickText(accent), ...DISPLAY, fontSize: 13, fontWeight: 600, boxShadow: `0 8px 26px color-mix(in srgb, ${accent} 34%, transparent)`, opacity: busy ? 0.6 : 1, border: 'none', cursor: 'pointer' }}
              >
                {busy && busy.id === sel.id ? 'Working…' : sel.applied ? 'Reapply' : 'Apply'}
              </button>
              <button onClick={() => onRepair(sel.id)} disabled={!!busy} style={repairBtn(!!busy)}>Repair</button>
            </>
          ) : (
            <button onClick={() => void host.openInstall(sel.id)} style={{ flex: 1, padding: 12, borderRadius: 13, background: 'rgba(255,255,255,0.06)', border: '1px solid rgba(255,255,255,0.12)', color: '#f4f4f6', ...DISPLAY, fontSize: 13, fontWeight: 600, cursor: 'pointer' }}>{sel.installLabel || 'Get game'}</button>
          )}
        </div>
      </section>
    </div>
  )
}

/* ── Settings pane ───────────────────────────────────────────────────────── */
function SettingsPane({
  settings,
  update,
  checking,
  onCheck,
  onToggleUpdates,
  onLogs,
}: {
  settings: Awaited<ReturnType<typeof host.getSettings>> | null
  update: Awaited<ReturnType<typeof host.checkUpdates>> | null
  checking: boolean
  onCheck: () => void
  onToggleUpdates: (v: boolean) => void
  onLogs: () => void
}) {
  const available = !!update?.updateAvailable
  const title = checking
    ? 'Checking for updates…'
    : available
      ? `${update?.remoteVersion ?? 'A new version'} is ready`
      : update
        ? 'You’re up to date'
        : `Exo ${settings?.appVersion ?? ''}`
  const note = checking
    ? 'Contacting the update server'
    : available
      ? update?.releaseSummary || 'Tap to install the latest release'
      : update
        ? `Exo ${update?.localVersion ?? settings?.appVersion ?? ''} · latest`
        : 'Check for the latest release'
  const border = available ? 'rgba(52,211,153,0.5)' : 'rgba(140,160,220,0.13)'
  const bg = available ? 'rgba(52,211,153,0.05)' : 'rgba(255,255,255,0.02)'

  return (
    <section style={{ position: 'relative', height: '100%', display: 'flex', flexDirection: 'column', gap: 16, padding: '10px 4px' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 18, padding: '16px 20px', borderRadius: 16, background: bg, border: `1.5px solid ${border}`, transition: 'border-color .3s,background .3s' }}>
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', width: 44, height: 44, borderRadius: '50%', border: `1px solid ${border}` }}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none"><path d="M12 3v12m0 0l-4.5-4.5M12 15l4.5-4.5M4 19h16" stroke={available ? '#34d399' : '#8a8b91'} strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round" /></svg>
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <p style={{ margin: 0, fontSize: 14, fontWeight: 600 }}>{title}</p>
          <p style={{ margin: '3px 0 0', fontSize: 11.5, color: '#8a8b91', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{note}</p>
        </div>
        <button onClick={onCheck} disabled={checking} style={{ flexShrink: 0, padding: '11px 22px', borderRadius: 12, background: available ? 'linear-gradient(180deg,#34d399,color-mix(in srgb, #34d399 72%, #000))' : 'rgba(255,255,255,0.06)', color: available ? '#052b1e' : '#f4f4f6', ...DISPLAY, fontSize: 12.5, fontWeight: 700, border: available ? 'none' : '1px solid rgba(255,255,255,0.12)', cursor: 'pointer', opacity: checking ? 0.6 : 1 }}>
          {checking ? 'Checking…' : available ? 'Update now' : 'Check'}
        </button>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: 'grid', gridTemplateColumns: 'repeat(2,minmax(0,1fr))', gridAutoRows: '1fr', gap: 14, alignContent: 'stretch' }}>
        <div style={settingsRow}>
          <div style={{ minWidth: 0 }}>
            <p style={{ margin: 0, fontSize: 13.5, fontWeight: 600 }}>Check for updates on startup</p>
            <p style={{ margin: '3px 0 0', fontSize: 11, color: '#8a8b91' }}>Look for new versions when Exo opens</p>
          </div>
          <button onClick={() => onToggleUpdates(!settings?.checkForUpdatesOnLaunch)} style={{ display: 'flex', alignItems: 'center', width: 46, height: 27, borderRadius: 999, padding: 2, flexShrink: 0, background: settings?.checkForUpdatesOnLaunch ? '#30d158' : 'rgba(255,255,255,0.14)', transition: 'background .3s', border: 'none', cursor: 'pointer' }}>
            <span style={{ width: 23, height: 23, borderRadius: '50%', background: '#fff', boxShadow: '0 2px 6px rgba(0,0,0,0.3)', transform: `translateX(${settings?.checkForUpdatesOnLaunch ? '19px' : '0px'})`, transition: 'transform .35s cubic-bezier(0.16,1,0.3,1)' }} />
          </button>
        </div>
        <SettingsLink label="View logs" hint="Every change Exo made, timestamped" onClick={onLogs} arrow="chevron" />
        <SettingsLink label="GitHub" hint="Source, issues & releases" onClick={() => void host.openUrl(settings?.issuesUrl || 'https://github.com/ImAvgErix/Exo')} arrow="ext" />
        <SettingsLink label="Buy me a coffee" hint="Keeps Exo free · even $1 helps" onClick={() => void host.openUrl(settings?.buyMeACoffeeUrl)} arrow="ext" />
      </div>

      <p style={{ margin: 0, fontSize: 10.5, color: '#5d5e66' }}>Exo {settings?.appVersion ?? ''} · Free forever</p>
    </section>
  )
}

/* ── small shared pieces ─────────────────────────────────────────────────── */
function StatusPill({ tone, label, small }: { tone: string; label: string; small?: boolean }) {
  return (
    <span style={{ display: 'flex', alignItems: 'center', gap: 6, padding: small ? '3px 11px' : '4px 12px', borderRadius: 999, background: `color-mix(in srgb, ${tone} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${tone} 42%, transparent)` }}>
      <span style={{ width: small ? 6 : 7, height: small ? 6 : 7, borderRadius: '50%', background: tone, boxShadow: `0 0 8px ${tone}` }} />
      <span style={{ fontSize: 11, fontWeight: 700, color: tone, whiteSpace: 'nowrap' }}>{label}</span>
    </span>
  )
}

function FeatureChip({ title, active, small, index = 0 }: { title: string; active: boolean; small?: boolean; index?: number }) {
  return (
    <span className="exo-chip-stagger" style={{ display: 'flex', alignItems: 'center', gap: small ? 8 : 11, padding: small ? '6px 12px' : '12px 15px', minHeight: 0, overflow: 'hidden', borderRadius: small ? 11 : 12, background: active ? 'rgba(52,211,153,0.08)' : 'rgba(255,255,255,0.03)', border: `1px solid ${active ? 'rgba(52,211,153,0.28)' : 'rgba(255,255,255,0.09)'}`, animation: 'exo-chip-in .45s var(--ease-spring) both', animationDelay: `${index * 0.04}s` }}>
      <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: small ? 15 : 17, height: small ? 15 : 17, borderRadius: '50%', flexShrink: 0, fontSize: small ? 8 : 9, fontWeight: 800, background: active ? 'rgba(52,211,153,0.2)' : 'rgba(255,255,255,0.05)', color: active ? '#34d399' : '#75767d', border: `1px solid ${active ? 'rgba(52,211,153,0.45)' : 'rgba(255,255,255,0.12)'}` }}>{active ? '✓' : ''}</span>
      <span style={{ fontSize: small ? 11 : 12.5, fontWeight: 500, color: active ? '#f4f4f6' : '#75767d', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{title}</span>
    </span>
  )
}

function ProgressBar({ pct, accent }: { pct: number; accent: string }) {
  return (
    <div style={{ height: 3, borderRadius: 999, background: 'rgba(0,0,0,0.55)', overflow: 'hidden' }}>
      <div style={{ height: '100%', background: accent, width: `${Math.max(0, Math.min(100, pct))}%`, transition: 'width .25s ease-out' }} />
    </div>
  )
}

function SettingsLink({ label, hint, onClick, arrow }: { label: string; hint: string; onClick: () => void; arrow: 'chevron' | 'ext' }) {
  return (
    <button onClick={onClick} style={{ ...settingsRow, textAlign: 'left', cursor: 'pointer' }}>
      <span style={{ minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 13.5, fontWeight: 600, color: '#f4f4f6' }}>{label}</span>
        <span style={{ display: 'block', marginTop: 3, fontSize: 11, color: '#8a8b91' }}>{hint}</span>
      </span>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" style={{ flexShrink: 0, opacity: 0.5 }}>
        {arrow === 'chevron' ? <path d="M9 6l6 6-6 6" stroke="#f4f4f6" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" /> : <path d="M7 17L17 7M9 7h8v8" stroke="#f4f4f6" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />}
      </svg>
    </button>
  )
}

function PaneSkeleton({ label }: { label: string }) {
  return <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 13, color: '#75767d' }}>{label}</div>
}

function Welcome({ onDismiss, onTip }: { onDismiss: () => void; onTip: () => void }) {
  return (
    <div style={{ position: 'absolute', inset: 0, zIndex: 90 }}>
      <button onClick={onDismiss} aria-label="Dismiss" style={{ position: 'absolute', inset: 0, background: 'rgba(0,0,0,0.62)', border: 'none', cursor: 'pointer' }} />
      <div role="dialog" style={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%,-50%)', width: 'min(392px,90%)', padding: 24, borderRadius: 22, background: '#0b0d15', border: '1px solid rgba(140,160,220,0.22)', boxShadow: '0 34px 90px rgba(0,0,0,0.75),inset 0 1px 0 rgba(255,255,255,0.08)', animation: 'exo-scalein .45s cubic-bezier(0.16,1,0.3,1)' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 14 }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: 44, height: 44, borderRadius: 14, background: 'radial-gradient(circle at 30% 25%,rgba(255,255,255,0.14),rgba(255,255,255,0.03))', border: '1px solid rgba(255,255,255,0.14)' }}>
            <img src={`${L}exo.png`} alt="Exo" draggable={false} style={{ width: 26, height: 26, objectFit: 'contain' }} />
          </div>
          <p style={{ margin: 0, ...MONO, fontSize: 10, fontWeight: 700, letterSpacing: '0.16em', color: '#75767d' }}>WELCOME TO EXO</p>
        </div>
        <h2 style={{ margin: 0, ...DISPLAY, fontSize: 21, fontWeight: 600, letterSpacing: '-0.01em' }}>Free forever. No catch.</h2>
        <div style={{ marginTop: 12, display: 'flex', flexDirection: 'column', gap: 9, fontSize: 12.5, lineHeight: 1.65, color: '#b7b8bd' }}>
          <p style={{ margin: 0 }}>No ads, no account, no paywall. Exo is built by one person — servers, code signing, and release testing come out of pocket.</p>
          <p style={{ margin: 0 }}>Tips are what keep updates coming. Even <span style={{ fontWeight: 600, color: '#34d399' }}>$1</span> makes a real difference.</p>
        </div>
        <div style={{ marginTop: 20, display: 'flex', flexDirection: 'column', gap: 10 }}>
          <button onClick={() => { onTip(); onDismiss() }} style={{ height: 46, borderRadius: 13, background: '#f4f4f6', color: '#0b0b0d', ...DISPLAY, fontSize: 13, fontWeight: 700, boxShadow: '0 0 26px rgba(255,255,255,0.12)', border: 'none', cursor: 'pointer' }}>Tip on Buy Me a Coffee</button>
          <button onClick={onDismiss} style={{ height: 44, borderRadius: 13, background: 'transparent', border: '1px solid rgba(140,160,220,0.16)', color: '#b7b8bd', ...DISPLAY, fontSize: 13, fontWeight: 600, cursor: 'pointer' }}>Continue</button>
        </div>
      </div>
    </div>
  )
}

const winBtn: React.CSSProperties = { display: 'flex', alignItems: 'center', justifyContent: 'center', width: 38, height: 28, borderRadius: 10, background: 'rgba(255,255,255,0.05)', border: '1px solid rgba(140,160,220,0.16)', color: '#c4c4ca', cursor: 'pointer' }
const arrowBtn: React.CSSProperties = { position: 'absolute', left: '50%', transform: 'translateX(-50%)', width: 36, height: 26, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: 9, border: '1px solid rgba(140,160,220,0.14)', background: 'transparent', color: '#75767d', cursor: 'pointer', zIndex: 20 }
const settingsRow: React.CSSProperties = { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: '16px 18px', borderRadius: 14, background: 'rgba(255,255,255,0.02)', border: '1px solid rgba(140,160,220,0.13)' }
function repairBtn(busy: unknown): React.CSSProperties {
  return { padding: '14px 24px', borderRadius: 14, background: 'rgba(255,255,255,0.06)', border: '1px solid rgba(255,255,255,0.12)', color: '#f4f4f6', ...DISPLAY, fontSize: 13.5, fontWeight: 600, opacity: busy ? 0.6 : 1, cursor: 'pointer' }
}
