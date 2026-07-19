import { useCallback, useEffect, useState } from 'react'
import { host, type DashboardSnapshot, type LiveStats } from '../lib/host'

export function HomePage() {
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refreshLive = useCallback(async () => {
    try {
      const live = await host.getLive()
      setDash((d) => (d ? { ...d, live } : d))
    } catch {
      /* tick */
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

  if (error) {
    return <div className="flex h-full items-center justify-center text-sm text-error">{error}</div>
  }
  if (!dash) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-muted">
        Reading this PC…
      </div>
    )
  }

  const { specs, live, modules, overview } = dash
  const applied = modules.filter((m) => m.applied).length

  return (
    <section className="glass specular flex h-full min-h-0 flex-col overflow-hidden rounded-2xl">
      <div className="shrink-0 border-b border-glass-border px-4 py-3.5">
        <div className="flex items-center justify-between gap-3">
          <p className="text-[10px] font-semibold tracking-[0.16em] text-muted">THIS PC</p>
          <p className="rounded-lg bg-raised px-2.5 py-1 text-[11px] font-semibold tabular ring-1 ring-glass-border">
            {overview || `${applied} / ${modules.length} verified`}
          </p>
        </div>
        <div className="mt-3 grid grid-cols-2 gap-x-6 gap-y-2.5 sm:grid-cols-4">
          <Spec label="CPU" value={specs.cpu} />
          <Spec label="GPU" value={specs.gpu} />
          <Spec label="RAM" value={specs.ram} />
          <Spec label="WINDOWS" value={specs.os} />
        </div>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-2 grid-rows-2">
        <StatCell
          title="Memory"
          value={fmtPct(live.memoryPercent)}
          bar={live.memoryPercent}
          barClass="bg-white"
          className="border-b border-r border-glass-border"
        />
        <StatCell
          title="CPU"
          value={live.hasCpu === false ? '—' : fmtPct(live.cpuPercent)}
          bar={live.hasCpu === false ? null : live.cpuPercent}
          barClass="bg-steam"
          className="border-b border-glass-border"
        />
        <StatCell
          title="GPU"
          value={live.hasGpu === false ? '—' : fmtPct(live.gpuPercent)}
          bar={live.hasGpu === false ? null : live.gpuPercent}
          barClass="bg-nvidia"
          className="border-r border-glass-border"
        />
        <NetCell live={live} />
      </div>
    </section>
  )
}

function Spec({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <p className="text-[10px] font-semibold tracking-[0.12em] text-muted">{label}</p>
      <p className="truncate text-[13px] font-semibold leading-snug">{value || '—'}</p>
    </div>
  )
}

function StatCell({
  title,
  value,
  bar,
  barClass,
  className = '',
}: {
  title: string
  value: string
  bar: number | null
  barClass: string
  className?: string
}) {
  return (
    <div className={`flex min-h-0 flex-col px-4 py-3.5 ${className}`}>
      <p className="text-[10px] font-semibold tracking-[0.12em] text-muted">{title}</p>
      <p className="tabular mt-2 text-[2.25rem] font-semibold leading-none tracking-tight">{value}</p>
      <div className="mt-auto pt-4">
        {bar != null ? (
          <div className="h-1.5 overflow-hidden rounded-full bg-sunken ring-1 ring-glass-border">
            <div
              className={`h-full rounded-full transition-[width] duration-500 ${barClass}`}
              style={{ width: `${Math.max(0, Math.min(100, bar))}%` }}
            />
          </div>
        ) : (
          <div className="h-1.5 rounded-full bg-sunken ring-1 ring-glass-border" />
        )}
      </div>
    </div>
  )
}

function NetCell({ live }: { live: LiveStats }) {
  const speed = live.netLinkSpeed && live.netLinkSpeed !== '—' ? live.netLinkSpeed : null
  const hero = speed ?? (live.netLink !== 'No link' ? live.netLink.split(' ')[0] : '—')
  const idle = live.netIdleMs && live.netIdleMs !== '—' ? live.netIdleMs : '—'
  const loadDown =
    live.netLoadedDownMs != null ? `${fmtMs(live.netLoadedDownMs)}` : '—'
  const loadUp = live.netLoadedUpMs != null ? `${fmtMs(live.netLoadedUpMs)}` : '—'
  const loss = live.netLoss ?? '—'
  const dns = live.netDns ?? '—'
  const rating = live.netRating && live.netRating !== '—' ? live.netRating : '—'

  return (
    <div className="flex min-h-0 flex-col overflow-hidden px-3.5 py-3">
      <p className="text-[10px] font-semibold tracking-[0.12em] text-muted">Network</p>
      {/* Hero = max link rate (e.g. 2.5G) */}
      <p className="tabular mt-1.5 text-[2.1rem] font-semibold leading-none tracking-tight">{hero}</p>

      <div className="mt-auto grid grid-cols-3 gap-1.5 pt-2.5">
        <MiniStat label="Idle" value={idle} />
        <MiniStat label="Load ↓" value={loadDown} />
        <MiniStat label="Load ↑" value={loadUp} />
        <MiniStat label="Loss" value={loss} />
        <MiniStat label="DNS" value={dns === '—' ? '—' : shortDns(dns)} />
        <MiniStat label="Rating" value={rating} accent={ratingColor(rating)} />
      </div>
    </div>
  )
}

function MiniStat({
  label,
  value,
  accent,
}: {
  label: string
  value: string
  accent?: string
}) {
  return (
    <div className="rounded-lg bg-raised px-1.5 py-1.5 ring-1 ring-glass-border">
      <p className="text-[8px] font-semibold tracking-wide text-muted">{label}</p>
      <p
        className={`tabular truncate text-[11px] font-semibold leading-tight ${accent ?? 'text-text'}`}
        title={value}
      >
        {value}
      </p>
    </div>
  )
}

function fmtPct(n: number | undefined | null) {
  if (n == null || Number.isNaN(n)) return '—'
  return `${Math.round(n)}%`
}

function fmtMs(n: number) {
  if (n >= 100) return `${Math.round(n)} ms`
  return `${n.toFixed(n >= 10 ? 0 : 1)} ms`
}

function shortDns(dns: string) {
  // "Cloudflare DNS + automatic DoH" → "Cloudflare"
  return dns.split(/[\s·+]/)[0] || dns
}

function ratingColor(rating: string) {
  switch (rating) {
    case 'Excellent':
      return 'text-success'
    case 'Good':
      return 'text-steam'
    case 'Fair':
      return 'text-secondary'
    case 'Poor':
      return 'text-error'
    default:
      return 'text-muted'
  }
}
