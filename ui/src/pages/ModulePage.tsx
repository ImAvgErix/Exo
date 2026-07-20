import { useEffect, useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { motion, useReducedMotion } from 'framer-motion'
import { host, type ModuleId, type ModuleStatus, onHostEvent } from '../lib/host'
import {
  checkableFeatures,
  featuresForSelection,
  statusDetailForSelection,
  statusFromFeatures,
} from '../lib/featurePreview'

const ids: ModuleId[] = ['discord', 'steam', 'windows', 'internet', 'nvidia', 'riot', 'epic']

function isModule(id: string | undefined): id is ModuleId {
  return !!id && (ids as string[]).includes(id)
}

const easeOut = [0.23, 1, 0.32, 1] as const

type Outcome = 'idle' | 'applied' | 'partial' | 'failed' | 'repaired'

export function ModulePage() {
  const { id } = useParams()
  const moduleId = isModule(id) ? id : 'discord'
  const reduce = useReducedMotion()
  const [status, setStatus] = useState<ModuleStatus | null>(null)
  const [detecting, setDetecting] = useState(true)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(0)
  const [progressText, setProgressText] = useState('')
  const [useGsync, setUseGsync] = useState(true)
  const [preferLowestLatency, setPreferLowestLatency] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<Outcome>('idle')
  const [outcomeMsg, setOutcomeMsg] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    // Keep previous status while re-detecting so the card doesn't flash empty.
    setDetecting(true)
    setError(null)
    setBusy(false)
    setProgress(0)
    setProgressText('')
    setOutcome('idle')
    setOutcomeMsg(null)
    ;(async () => {
      try {
        const s = await host.detect(moduleId)
        if (cancelled) return
        setStatus(s)
        if (s.options?.useGsync != null) setUseGsync(!!s.options.useGsync)
        if (s.options?.preferLowestLatency != null)
          setPreferLowestLatency(!!s.options.preferLowestLatency)
        if (s.isApplied) {
          setOutcome('applied')
          setOutcomeMsg(null)
        }
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
    setOutcome('idle')
    setOutcomeMsg(null)
    setProgress(0)
    setProgressText('Starting…')
    try {
      const s = await host.apply(moduleId, {
        experimental: true,
        useGsync,
        preferLowestLatency,
      })
      setStatus(s)
      const feats = featuresForSelection(
        moduleId,
        s.features,
        { experimental: true, useGsync, preferLowestLatency },
      )
      const stats = statusFromFeatures(feats, s.isApplied)
      if (stats.offCount === 0 || s.isApplied) {
        setOutcome('applied')
        setOutcomeMsg(
          stats.total > 0
            ? `Done — ${stats.onCount}/${stats.total} features on.`
            : 'Done — stack applied.',
        )
      } else {
        setOutcome('partial')
        setOutcomeMsg(
          `Finished with gaps — ${stats.onCount}/${stats.total} on (${stats.offCount} still off). Reapply if needed.`,
        )
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Apply failed'
      // Surface full multi-line log path (host appends "Full log: …")
      setError(msg)
      setOutcome('failed')
      setOutcomeMsg(msg.includes('Full log:') ? msg : `${msg}\nOpen Logs from Settings for apply-{module}-latest.log`)
    } finally {
      setBusy(false)
      setProgressText('')
    }
  }

  async function runRepair() {
    setBusy(true)
    setError(null)
    setOutcome('idle')
    setOutcomeMsg(null)
    try {
      const s = await host.repair(moduleId)
      setStatus(s)
      setOutcome('repaired')
      setOutcomeMsg('Repair finished — Exo changes reversed.')
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Repair failed'
      setError(msg)
      setOutcome('failed')
      setOutcomeMsg(msg)
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
    () => ({ experimental: true, useGsync, preferLowestLatency }),
    [useGsync, preferLowestLatency],
  )

  const features = useMemo(
    () =>
      status
        ? featuresForSelection(moduleId, status.features, selection)
        : [],
    [moduleId, status, selection],
  )

  // Counts + headline always come from the visible list (never host statusText alone).
  const featureStats = useMemo(
    () => statusFromFeatures(features, status?.isApplied),
    [features, status?.isApplied],
  )

  const detailLine = useMemo(() => {
    if (detecting || !status) return 'Checking this PC…'
    if (outcomeMsg && outcome !== 'idle') return outcomeMsg
    if (featureStats.offCount > 0 && outcome === 'idle') {
      const off = checkableFeatures(features)
        .filter((f) => !f.active)
        .map((f) => f.title)
      return `Off: ${off.join(', ')}.`
    }
    return statusDetailForSelection(moduleId, status.detail, selection)
  }, [
    detecting,
    moduleId,
    status,
    selection,
    outcome,
    outcomeMsg,
    featureStats.offCount,
    features,
  ])

  const statusHeadline = (() => {
    if (detecting && features.length === 0) return 'Checking this PC…'
    if (busy) return progressText || 'Working…'
    if (outcome === 'applied') return 'Applied'
    if (outcome === 'partial') {
      return featureStats.offCount > 0
        ? `Partially applied — ${featureStats.offCount} still off`
        : 'Partially applied'
    }
    if (outcome === 'failed') return 'Failed'
    if (outcome === 'repaired') return 'Repaired'
    // Prefer visible feature math over host statusText ("2 need Apply" vs 3 red rows).
    if (features.length > 0) return featureStats.headline
    if (status?.isApplied) return 'Applied'
    return status?.statusText ?? '—'
  })()

  const uiLocked = detecting || busy
  const activeCount = featureStats.onCount
  const totalCount = featureStats.total
  const allCheckableOn =
    featureStats.total > 0 && featureStats.offCount === 0
  const outcomeTone =
    outcome === 'applied' ||
    (outcome === 'idle' && (status?.isApplied || allCheckableOn))
      ? 'ok'
      : outcome === 'failed'
        ? 'bad'
        : outcome === 'partial' ||
            (outcome === 'idle' && featureStats.offCount > 0)
          ? 'warn'
          : outcome === 'repaired'
            ? 'ok'
            : 'neutral'

  return (
    <div className="flex h-full min-h-0 flex-col gap-2 overflow-hidden">
      {moduleId === 'nvidia' && (
        <section className="glass specular shrink-0 rounded-2xl px-3 py-2">
          <div className="flex items-center justify-between gap-2">
            <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">
              Stack profile
            </p>
            <button
              type="button"
              disabled={uiLocked}
              onClick={() => void openNvidiaCpl()}
              className="text-[11px] font-semibold text-muted hover:text-secondary disabled:opacity-40"
            >
              Control Panel
            </button>
          </div>
          <div className="mt-1.5">
            <Segmented
              value={useGsync ? 'gsync' : 'raw'}
              onChange={(v) => setUseGsync(v === 'gsync')}
              disabled={uiLocked}
              options={[
                { id: 'raw', label: 'Raw latency' },
                { id: 'gsync', label: 'G-SYNC / VRR' },
              ]}
            />
          </div>
        </section>
      )}

      {moduleId === 'internet' && (
        <section className="glass specular shrink-0 rounded-2xl px-3 py-2">
          <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">
            Stack profile
          </p>
          <div className="mt-1.5">
            <Segmented
              value={preferLowestLatency ? 'lat' : 'tp'}
              onChange={(v) => setPreferLowestLatency(v === 'lat')}
              disabled={uiLocked}
              options={[
                { id: 'lat', label: 'Lowest latency' },
                { id: 'tp', label: 'High throughput' },
              ]}
            />
          </div>
        </section>
      )}

      {/* Status — outcome is loud and obvious */}
      <section
        className="glass specular shrink-0 rounded-2xl px-3.5 py-2.5"
        style={
          outcomeTone === 'ok'
            ? { boxShadow: 'inset 0 0 0 1px color-mix(in srgb, var(--color-success) 35%, transparent)' }
            : outcomeTone === 'bad'
              ? { boxShadow: 'inset 0 0 0 1px color-mix(in srgb, var(--color-error) 40%, transparent)' }
              : outcomeTone === 'warn'
                ? { boxShadow: 'inset 0 0 0 1px #fbbf2444' }
                : undefined
        }
      >
        <div className="flex items-start gap-2.5">
          <span
            className={`mt-1 h-2 w-2 shrink-0 rounded-full ${
              outcomeTone === 'ok'
                ? 'bg-success shadow-[0_0_8px_var(--color-success)]'
                : outcomeTone === 'bad'
                  ? 'bg-error shadow-[0_0_8px_var(--color-error)]'
                  : outcomeTone === 'warn'
                    ? 'bg-amber-400'
                    : 'bg-muted/50'
            }`}
            aria-hidden
          />
          <div className="min-w-0 flex-1">
            <p
              className={`truncate text-[16px] font-semibold tracking-tight leading-snug ${
                outcomeTone === 'ok'
                  ? 'text-success'
                  : outcomeTone === 'bad'
                    ? 'text-error'
                    : outcomeTone === 'warn'
                      ? 'text-amber-300'
                      : 'text-text'
              }`}
            >
              {statusHeadline}
            </p>
            <p className="mt-0.5 line-clamp-2 text-[12px] leading-snug text-muted">{detailLine}</p>
            {error && outcome !== 'failed' && (
              <p className="mt-1 text-[12px] text-error line-clamp-2">{error}</p>
            )}
            {busy && (
              <div className="mt-2">
                <div className="mb-1 flex justify-between text-[11px] text-muted">
                  <span className="truncate">{progressText || 'Working…'}</span>
                  <span className="tabular shrink-0">
                    {progress >= 0 ? `${Math.round(progress)}%` : ''}
                  </span>
                </div>
                <div className="h-1 overflow-hidden rounded-full bg-black/50">
                  <motion.div
                    className="h-full rounded-full bg-white/90"
                    animate={{
                      width: `${Math.max(4, Math.min(100, progress < 0 ? 30 : progress))}%`,
                    }}
                    transition={{ duration: 0.28, ease: easeOut }}
                  />
                </div>
              </div>
            )}
          </div>
        </div>
      </section>

      {/* Features — dense, no scroll; 2-col single-line rows */}
      <section className="glass specular flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl">
        <div className="flex shrink-0 items-baseline justify-between gap-2 border-b border-white/[0.06] px-3 py-2">
          <p className="text-[11px] font-semibold tracking-[0.06em] text-secondary">Features</p>
          <p className="text-[11px] tabular text-muted">
            {detecting || !status
              ? '…'
              : totalCount === 0
                ? '—'
                : `${activeCount}/${totalCount} on`}
          </p>
        </div>

        <div className="min-h-0 flex-1 overflow-hidden px-2 py-1.5">
          {detecting || !status ? (
            <div className="grid grid-cols-2 gap-x-2 gap-y-1">
              {Array.from({ length: 10 }, (_, i) => (
                <div key={i} className="h-7 animate-pulse rounded-md bg-raised/50" aria-hidden />
              ))}
            </div>
          ) : features.length === 0 ? (
            <p className="px-2 py-4 text-center text-[12px] text-muted">
              Apply to load the feature stack.
            </p>
          ) : (
            <ul className="grid h-full grid-cols-2 content-start gap-x-1 gap-y-0.5">
              {features.map((f) => (
                <li
                  key={f.title}
                  title={f.detail || f.title}
                  className="flex min-w-0 items-center gap-1.5 rounded-md px-1.5 py-1"
                >
                  <span
                    className={`flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full text-[9px] font-bold leading-none ${
                      f.active
                        ? 'bg-success/20 text-success'
                        : 'bg-white/5 text-muted'
                    }`}
                    aria-hidden
                  >
                    {f.active ? '✓' : '·'}
                  </span>
                  <span
                    className={`min-w-0 truncate text-[11px] font-medium leading-tight ${
                      f.active ? 'text-text' : 'text-muted'
                    }`}
                  >
                    {f.title}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </section>

      <div className="flex shrink-0 gap-2">
        <motion.button
          type="button"
          disabled={uiLocked}
          whileTap={reduce ? undefined : { scale: 0.96 }}
          onClick={() => void runApply()}
          className="flex-1 rounded-xl bg-white py-2.5 text-sm font-semibold text-black shadow-[0_0_24px_rgb(255_255_255/0.12)] disabled:opacity-40"
        >
          {busy
            ? 'Working…'
            : moduleId === 'internet'
              ? 'Analyze & Apply'
              : status?.isApplied
                ? 'Reapply'
                : 'Apply'}
        </motion.button>
        <motion.button
          type="button"
          disabled={uiLocked}
          whileTap={reduce ? undefined : { scale: 0.96 }}
          onClick={() => void runRepair()}
          className="glass-chip rounded-xl px-5 py-2.5 text-sm font-semibold text-text disabled:opacity-40"
        >
          Repair
        </motion.button>
      </div>
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
      className="flex gap-1 rounded-lg bg-black/50 p-1 ring-1 ring-white/10"
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
            className={`flex flex-1 items-center justify-center rounded-md px-2 py-1.5 text-[11px] font-semibold transition-colors ${
              on
                ? 'bg-white text-black shadow-[0_1px_0_rgb(255_255_255/0.3)_inset,0_3px_10px_rgb(0_0_0/0.35)]'
                : 'bg-transparent text-muted hover:bg-white/5 hover:text-secondary'
            }`}
          >
            {o.label}
          </button>
        )
      })}
    </div>
  )
}
