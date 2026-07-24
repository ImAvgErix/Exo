import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { motion, useReducedMotion } from 'framer-motion'
import { host, onHostEvent, type DashboardSnapshot } from '../lib/host'
import { AmbientBackground } from '../components/AmbientBackground'

const easeSoft = [0.16, 1, 0.3, 1] as const

const MODULE_ACCENT: Record<string, string> = {
  discord: '#5865f2',
  brave: '#fb542b',
  steam: '#1a9fff',
  games: '#f0b429',
  internet: '#22d3ee',
  nvidia: '#76b900',
}

/** Redesign Overview: system-status hero + integrity ring + live telemetry grid. */
export function OverviewHome() {
  const reduce = useReducedMotion()
  const nav = useNavigate()
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [verifying, setVerifying] = useState(false)
  const [verifyLabel, setVerifyLabel] = useState('Verify all')

  const refreshLive = useCallback(async () => {
    try {
      const live = await host.getLive()
      setDash((d) => (d ? { ...d, live } : d))
    } catch {
      /* transient tick failure — keep last good frame */
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    ;(async () => {
      try {
        const d = await host.getDashboard()
        if (!cancelled) setDash(d)
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load')
      }
    })()
    const t = window.setInterval(() => void refreshLive(), 1500)
    const boot = window.setTimeout(() => void refreshLive(), 400)
    return () => {
      cancelled = true
      window.clearInterval(t)
      window.clearTimeout(boot)
    }
  }, [refreshLive])

  useEffect(() => {
    return onHostEvent('settings.verifyProgress', (data) => {
      const d = data as { percent?: number }
      if (typeof d.percent === 'number' && d.percent >= 0 && d.percent < 100) {
        setVerifyLabel(`Scanning ${Math.round(d.percent)}%`)
      }
    })
  }, [])

  async function runVerify() {
    if (verifying) return
    setVerifying(true)
    setVerifyLabel('Scanning…')
    try {
      const r = await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      const total =
        (r.applied ?? 0) + (r.partial ?? 0) + (r.ready ?? 0) + (r.missing ?? 0)
      setVerifyLabel(total > 0 ? `${r.applied}/${total} verified` : 'Done')
      window.setTimeout(() => setVerifyLabel('Verify all'), 2400)
    } catch {
      setVerifyLabel('Scan failed')
      window.setTimeout(() => setVerifyLabel('Verify all'), 2400)
    } finally {
      setVerifying(false)
    }
  }

  if (error) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-error">{error}</div>
    )
  }
  if (!dash) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-muted">
        Reading this PC…
      </div>
    )
  }

  const { modules, live } = dash
  const total = modules.length
  const applied = modules.filter((m) => m.applied).length
  const frac = total > 0 ? applied / total : 0
  const pct = Math.round(frac * 100)
  const tone = frac >= 0.83 ? 'ok' : frac >= 0.5 ? 'warn' : 'bad'
  const ringColor = tone === 'ok' ? '#34d399' : tone === 'warn' ? '#fbbf24' : '#f87171'
  const headline =
    applied === total && total > 0
      ? 'All systems optimized'
      : applied > 0
        ? `${applied} of ${total} optimized`
        : 'Nothing optimized yet'
  const sub =
    applied === total && total > 0
      ? 'Every detected module is applied and verified from live checks.'
      : 'Run a live scan, then apply the modules that still need it.'

  const netRating = live.netRating && live.netRating !== '—' ? live.netRating : null
  const telemetry = [
    { label: 'CPU', value: live.hasCpu === false ? '—' : `${Math.round(live.cpuPercent)}%`, pct: live.hasCpu === false ? 0 : live.cpuPercent, accent: '#38bdf8', caption: 'load' },
    { label: 'GPU', value: live.hasGpu === false ? '—' : `${Math.round(live.gpuPercent)}%`, pct: live.hasGpu === false ? 0 : live.gpuPercent, accent: '#84cc16', caption: 'load' },
    { label: 'MEM', value: `${Math.round(live.memoryPercent)}%`, pct: live.memoryPercent, accent: '#c4b5fd', caption: live.memorySecondary || 'in use' },
    { label: 'NET', value: live.netLinkSpeed && live.netLinkSpeed !== '—' ? live.netLinkSpeed : live.netLink.split(' ')[0], pct: ratingPct(netRating), accent: '#22d3ee', caption: netRating ?? live.netLinkMedia ?? 'link' },
  ]

  return (
    <section className="relative flex h-full min-h-0 flex-col overflow-hidden">
      <AmbientBackground />
      <motion.div
        className="relative z-10 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto"
        initial={reduce ? false : { opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={reduce ? { duration: 0 } : { duration: 0.35, ease: easeSoft }}
      >
        {/* Hero: system status + integrity ring */}
        <section className="glass specular flex items-center gap-6 rounded-3xl px-7 py-6">
          <div className="min-w-0 flex-1">
            <p className="text-[11px] font-bold tracking-[0.24em] text-muted">SYSTEM STATUS</p>
            <h1
              className="mt-2.5 text-[2.4rem] font-semibold leading-none tracking-tight"
              style={{ fontFamily: 'var(--font-display)' }}
            >
              {headline}
            </h1>
            <p className="mt-3 max-w-[380px] text-[13px] leading-relaxed text-secondary">{sub}</p>
            <div className="mt-5 flex items-center gap-3.5">
              <button
                type="button"
                disabled={verifying}
                onClick={() => void runVerify()}
                className="rounded-xl bg-text px-6 py-3 text-[13px] font-semibold text-page transition-transform disabled:opacity-60"
                style={{ fontFamily: 'var(--font-display)', boxShadow: '0 0 26px rgba(255,255,255,0.12)' }}
              >
                {verifyLabel}
              </button>
              <span className="tabular text-[11px] text-muted" style={{ fontFamily: 'var(--font-mono)' }}>
                live re-check · no changes applied
              </span>
            </div>
          </div>
          <IntegrityRing pct={pct} color={ringColor} spinning={verifying} reduce={!!reduce} />
        </section>

        {/* Live telemetry */}
        <section className="glass specular rounded-3xl px-6 py-5">
          <p className="mb-4 text-[10px] font-bold tracking-[0.2em] text-muted">LIVE TELEMETRY</p>
          <div className="grid grid-cols-2 gap-6 sm:grid-cols-4">
            {telemetry.map((t) => (
              <div key={t.label} className="min-w-0">
                <span className="text-[10px] font-bold tracking-[0.14em] text-secondary">{t.label}</span>
                <p
                  className="tabular mt-2 text-[1.75rem] font-semibold leading-none"
                  style={{ fontFamily: 'var(--font-mono)', color: t.accent }}
                >
                  {t.value}
                </p>
                <div className="mt-3 h-[5px] overflow-hidden rounded-full bg-sunken ring-1 ring-glass-border">
                  <motion.div
                    className="h-full rounded-full"
                    style={{ background: t.accent }}
                    initial={false}
                    animate={{ width: `${Math.max(0, Math.min(100, t.pct))}%` }}
                    transition={{ duration: 0.6, ease: easeSoft }}
                  />
                </div>
                <p className="mt-2 truncate text-[10px] text-muted" title={t.caption}>
                  {t.caption}
                </p>
              </div>
            ))}
          </div>
        </section>

        {/* Modules at a glance — launcher until the tab bar lands (increment 3) */}
        <section className="glass specular rounded-3xl px-6 py-5">
          <p className="mb-4 text-[10px] font-bold tracking-[0.2em] text-muted">MODULES</p>
          <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-3">
            {modules.map((m) => {
              const accent = MODULE_ACCENT[m.id] ?? '#8b8d92'
              return (
                <button
                  key={m.id}
                  type="button"
                  onClick={() => nav(m.id === 'games' ? '/module/games' : `/module/${m.id}`)}
                  className="glass-chip flex items-center gap-3 rounded-2xl px-3.5 py-3 text-left transition-transform hover:brightness-125"
                >
                  <img
                    src={`/logos/${m.id}.png`}
                    alt=""
                    className="h-6 w-6 shrink-0 object-contain"
                    style={{ filter: `drop-shadow(0 0 6px ${accent}88)` }}
                    draggable={false}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[13px] font-semibold">{m.title}</span>
                    <span
                      className="block text-[10px] font-semibold"
                      style={{ color: m.applied ? '#34d399' : '#8b8d92' }}
                    >
                      {m.applied ? 'Optimized' : 'Ready'}
                    </span>
                  </span>
                  <span
                    className="h-2 w-2 shrink-0 rounded-full"
                    style={{
                      background: m.applied ? '#34d399' : 'transparent',
                      boxShadow: m.applied ? '0 0 8px #34d399' : 'none',
                      border: m.applied ? 'none' : '1.5px solid #3a3a46',
                    }}
                  />
                </button>
              )
            })}
          </div>
        </section>
      </motion.div>
    </section>
  )
}

function IntegrityRing({
  pct,
  color,
  spinning,
  reduce,
}: {
  pct: number
  color: string
  spinning: boolean
  reduce: boolean
}) {
  const deg = Math.round((pct / 100) * 360)
  return (
    <div className="relative h-[168px] w-[168px] shrink-0">
      <motion.div
        className="absolute inset-0 rounded-full"
        style={{
          background: `conic-gradient(from -90deg, ${color} ${deg}deg, rgba(255,255,255,0.06) 0)`,
          WebkitMask: 'radial-gradient(farthest-side, transparent 72%, #000 73%)',
          mask: 'radial-gradient(farthest-side, transparent 72%, #000 73%)',
          filter: `drop-shadow(0 0 9px ${color}88)`,
        }}
        initial={false}
        animate={reduce ? {} : { opacity: spinning ? [1, 0.55, 1] : 1 }}
        transition={spinning ? { duration: 0.9, repeat: Infinity } : { duration: 0.3 }}
      />
      <div className="absolute inset-0 flex flex-col items-center justify-center">
        <span
          className="tabular text-[2.6rem] font-semibold leading-none tracking-tight"
          style={{ fontFamily: 'var(--font-mono)' }}
        >
          {pct}
          <span className="text-[1.4rem] text-muted">%</span>
        </span>
        <span className="mt-1.5 text-[9px] font-bold tracking-[0.22em] text-muted">INTEGRITY</span>
      </div>
    </div>
  )
}

function ratingPct(rating: string | null): number {
  switch (rating) {
    case 'Excellent':
      return 100
    case 'Good':
      return 75
    case 'Fair':
      return 50
    case 'Poor':
      return 28
    default:
      return 0
  }
}
