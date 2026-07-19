import { useEffect, useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { motion } from 'framer-motion'
import { host, type ModuleId, type ModuleStatus, onHostEvent } from '../lib/host'
import { featuresForSelection, statusDetailForSelection } from '../lib/featurePreview'

const titles: Record<ModuleId, string> = {
  discord: 'Discord',
  steam: 'Steam',
  internet: 'Internet',
  nvidia: 'NVIDIA',
  riot: 'Riot',
  epic: 'Epic',
}

const logos: Record<ModuleId, string> = {
  discord: '/logos/discord.png',
  steam: '/logos/steam.png',
  internet: '/logos/internet.png',
  nvidia: '/logos/nvidia.png',
  riot: '/logos/riot.png',
  epic: '/logos/epic.png',
}

const ids: ModuleId[] = ['discord', 'steam', 'internet', 'nvidia', 'riot', 'epic']

function isModule(id: string | undefined): id is ModuleId {
  return !!id && (ids as string[]).includes(id)
}

export function ModulePage() {
  const { id } = useParams()
  const moduleId = isModule(id) ? id : 'discord'
  const [status, setStatus] = useState<ModuleStatus | null>(null)
  /** True until first detect for this module returns — keeps features blank (no half-list flash). */
  const [detecting, setDetecting] = useState(true)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(0)
  const [progressText, setProgressText] = useState('')
  const [experimental, setExperimental] = useState(false)
  const [useGsync, setUseGsync] = useState(true)
  const [preferLowestLatency, setPreferLowestLatency] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    // Hard reset so previous module's rows never flash under a new title.
    setStatus(null)
    setDetecting(true)
    setError(null)
    setBusy(false)
    setProgress(0)
    setProgressText('')
    ;(async () => {
      try {
        const s = await host.detect(moduleId)
        if (cancelled) return
        setStatus(s)
        if (s.options?.experimental != null) setExperimental(!!s.options.experimental)
        if (s.options?.useGsync != null) setUseGsync(!!s.options.useGsync)
        if (s.options?.preferLowestLatency != null)
          setPreferLowestLatency(!!s.options.preferLowestLatency)
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Detect failed')
      } finally {
        if (!cancelled) setDetecting(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [moduleId])

  useEffect(() => {
    const off = onHostEvent('module.progress', (data) => {
      const d = data as { module?: string; percent?: number; status?: string }
      if (d.module && d.module !== moduleId) return
      if (typeof d.percent === 'number') setProgress(d.percent)
      if (d.status) setProgressText(d.status)
    })
    return () => {
      off()
    }
  }, [moduleId])

  async function runApply() {
    setBusy(true)
    setError(null)
    setProgress(0)
    setProgressText('Starting…')
    try {
      const s = await host.apply(moduleId, {
        experimental,
        useGsync,
        preferLowestLatency,
      })
      setStatus(s)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Apply failed')
    } finally {
      setBusy(false)
      setProgressText('')
    }
  }

  async function runRepair() {
    setBusy(true)
    setError(null)
    try {
      const s = await host.repair(moduleId)
      setStatus(s)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Repair failed')
    } finally {
      setBusy(false)
    }
  }

  async function openNvidiaCpl() {
    try {
      const r = await host.openNvidiaControlPanel()
      if (!r.ok) setError(r.message || 'Could not open NVIDIA Control Panel')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not open NVIDIA Control Panel')
    }
  }

  const selection = useMemo(
    () => ({ experimental, useGsync, preferLowestLatency }),
    [experimental, useGsync, preferLowestLatency],
  )

  // Never project toggle-only rows before detect lands — that was the half-list flash.
  const features = useMemo(
    () =>
      status
        ? featuresForSelection(moduleId, status.features, selection)
        : [],
    [moduleId, status, selection],
  )

  const detailLine = useMemo(() => {
    if (detecting || !status) return 'Checking this PC…'
    return statusDetailForSelection(moduleId, status.detail, selection)
  }, [detecting, moduleId, status, selection])

  const uiLocked = detecting || busy

  return (
    <div className="flex h-full min-h-0 flex-col gap-2 overflow-hidden">
      <div className="flex shrink-0 items-center gap-3">
        <img src={logos[moduleId]} alt="" className="h-7 w-7 object-contain" draggable={false} />
        <div className="min-w-0 flex-1">
          <h1 className="text-lg font-semibold tracking-tight">{titles[moduleId]}</h1>
          <p className="truncate text-[12px] text-muted">
            {detecting
              ? 'Checking this PC…'
              : (status?.statusText ?? (busy ? progressText || 'Working…' : '—'))}
          </p>
        </div>
      </div>

      {/* Module-specific options */}
      <section className="glass specular grid shrink-0 gap-2 rounded-2xl p-2.5 sm:grid-cols-2">
        <OptionCard
          title="Apply mode"
          hint="Stable applies every safe reversible tweak. Experimental only force-rebuilds / re-imports / tighter loops."
        >
          <Segmented
            value={experimental ? 'exp' : 'stable'}
            onChange={(v) => setExperimental(v === 'exp')}
            disabled={uiLocked}
            options={[
              { id: 'stable', label: 'Stable' },
              { id: 'exp', label: 'Experimental' },
            ]}
          />
        </OptionCard>

        {moduleId === 'nvidia' && (
          <OptionCard
            title="3D profile pack"
            hint="Apply imports via Profile Inspector (DRS). Scaling and NVIDIA color stay manual in Control Panel."
          >
            <Segmented
              value={useGsync ? 'gsync' : 'raw'}
              onChange={(v) => setUseGsync(v === 'gsync')}
              disabled={uiLocked}
              options={[
                { id: 'raw', label: 'Raw latency' },
                { id: 'gsync', label: 'G-SYNC / VRR' },
              ]}
            />
            <button
              type="button"
              disabled={uiLocked}
              onClick={() => void openNvidiaCpl()}
              className="glass-chip mt-2 w-full rounded-xl py-2 text-[12px] font-semibold disabled:opacity-40"
            >
              Open Control Panel
            </button>
          </OptionCard>
        )}

        {moduleId === 'internet' && (
          <OptionCard
            title="Stack profile"
            hint="Features update live. Lowest latency vs high throughput NIC stack on next Apply."
          >
            <Segmented
              value={preferLowestLatency ? 'lat' : 'tp'}
              onChange={(v) => setPreferLowestLatency(v === 'lat')}
              disabled={uiLocked}
              options={[
                { id: 'lat', label: 'Lowest latency' },
                { id: 'tp', label: 'High throughput' },
              ]}
            />
          </OptionCard>
        )}

        {moduleId !== 'nvidia' && moduleId !== 'internet' && (
          <OptionCard title="Scope" hint="Only this app’s install and Windows keys it owns.">
            <p className="text-[12px] leading-snug text-secondary">
              Apply is reversible via Repair. Anti-cheat and game files stay untouched.
            </p>
          </OptionCard>
        )}
      </section>

      <section className="glass shrink-0 rounded-2xl px-3 py-2.5">
        <p className="text-[10px] font-semibold tracking-[0.14em] text-muted">STATUS</p>
        <p className="mt-0.5 text-[13px] font-semibold leading-snug">{detailLine}</p>
        {error && <p className="mt-1.5 text-sm text-error">{error}</p>}
        {busy && progressText && (
          <div className="mt-2">
            <div className="mb-1 flex justify-between text-[11px] text-muted">
              <span>{progressText}</span>
              <span className="tabular">{progress >= 0 ? `${Math.round(progress)}%` : ''}</span>
            </div>
            <div className="h-1.5 overflow-hidden rounded-full bg-black/40 ring-1 ring-white/10">
              <motion.div
                className="h-full rounded-full bg-white"
                animate={{ width: `${Math.max(4, Math.min(100, progress < 0 ? 30 : progress))}%` }}
                transition={{ duration: 0.3 }}
              />
            </div>
          </div>
        )}
      </section>

      {/* Features: fixed shell — skeleton until detect; no staggered pop-in */}
      <section className="glass flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl p-2.5">
        <p className="mb-1.5 shrink-0 px-1 text-[10px] font-semibold tracking-[0.14em] text-muted">
          FEATURES
        </p>
        <div className="grid min-h-0 flex-1 content-start gap-1.5 overflow-hidden sm:grid-cols-2">
          {detecting || !status ? (
            <>
              {Array.from({ length: 6 }, (_, i) => (
                <div
                  key={i}
                  className="glass-chip h-[3.25rem] animate-pulse rounded-xl bg-raised/60 ring-1 ring-glass-border"
                  aria-hidden
                />
              ))}
            </>
          ) : (
            features.map((f) => (
              <div
                key={f.title}
                className={`glass-chip flex min-h-0 flex-col justify-center rounded-xl px-2.5 py-1.5 ${
                  f.active ? '' : 'opacity-50'
                }`}
              >
                <div className="flex items-center gap-1.5">
                  <span
                    className={`flex h-4 w-4 shrink-0 items-center justify-center rounded-full text-[10px] font-bold ${
                      f.active
                        ? 'bg-success/20 text-success ring-1 ring-success/40'
                        : 'bg-sunken text-muted ring-1 ring-glass-border'
                    }`}
                    aria-hidden
                  >
                    {f.active ? '✓' : '·'}
                  </span>
                  <span className="truncate text-[12px] font-semibold leading-tight">{f.title}</span>
                </div>
                <p className="mt-0.5 line-clamp-2 pl-5 text-[10px] leading-snug text-muted">
                  {f.detail}
                </p>
              </div>
            ))
          )}
        </div>
      </section>

      <div className="flex shrink-0 gap-2">
        <motion.button
          type="button"
          disabled={uiLocked}
          whileTap={{ scale: 0.98 }}
          onClick={() => void runApply()}
          className="flex-1 rounded-xl bg-white py-2.5 text-sm font-semibold text-black shadow-[0_0_28px_rgb(255_255_255/0.14)] disabled:opacity-40"
        >
          {status?.isApplied ? 'Reapply' : 'Apply'}
        </motion.button>
        <motion.button
          type="button"
          disabled={uiLocked}
          whileTap={{ scale: 0.98 }}
          onClick={() => void runRepair()}
          className="glass-chip rounded-xl px-5 py-2.5 text-sm font-semibold text-text disabled:opacity-40"
        >
          Repair
        </motion.button>
      </div>
    </div>
  )
}

function OptionCard({
  title,
  hint,
  children,
}: {
  title: string
  hint: string
  children: React.ReactNode
}) {
  return (
    <div className="glass-chip rounded-xl px-3 py-2">
      <p className="text-[10px] font-semibold tracking-[0.12em] text-muted">{title}</p>
      <div className="mt-1.5">{children}</div>
      <p className="mt-1.5 text-[10px] leading-snug text-muted">{hint}</p>
    </div>
  )
}

function Segmented({
  value,
  onChange,
  options,
  disabled,
}: {
  value: string
  onChange: (v: string) => void
  options: { id: string; label: string }[]
  disabled?: boolean
}) {
  return (
    <div
      role="radiogroup"
      className="flex gap-1 rounded-lg bg-black/50 p-1 ring-1 ring-white/12"
    >
      {options.map((o) => {
        const on = value === o.id
        return (
          <button
            key={o.id}
            type="button"
            role="radio"
            aria-checked={on}
            disabled={disabled}
            onClick={() => onChange(o.id)}
            className={`relative flex flex-1 items-center justify-center gap-1 rounded-md px-2 py-2 text-[11px] font-semibold transition ${
              on
                ? 'bg-white text-black shadow-[0_1px_0_rgb(255_255_255/0.35)_inset,0_4px_12px_rgb(0_0_0/0.35)] ring-1 ring-white/80'
                : 'bg-transparent text-muted hover:bg-white/5 hover:text-secondary'
            }`}
          >
            {on && (
              <span className="text-[10px] font-bold text-emerald-700" aria-hidden>
                ●
              </span>
            )}
            <span className="relative z-10">{o.label}</span>
          </button>
        )
      })}
    </div>
  )
}
