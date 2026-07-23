import { useEffect, useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { motion, useReducedMotion } from 'framer-motion'
import {
  host,
  type GamePreset,
  type ModuleId,
  type ModuleStatus,
  onHostEvent,
} from '../lib/host'
import {
  featuresForSelection,
  statusFromFeatures,
} from '../lib/featurePreview'
import {
  classifyStatus,
  parseApplyReport,
  type Tone,
} from '../lib/moduleUx'

const ids: ModuleId[] = [
  'discord',
  'brave',
  'steam',
  'games',
  'internet',
  'nvidia',
]

function isModule(id: string | undefined): id is ModuleId {
  return !!id && (ids as string[]).includes(id)
}

const easeOut = [0.23, 1, 0.32, 1] as const

type Outcome = 'idle' | 'applied' | 'partial' | 'failed' | 'repaired'

/**
 * Per-module optimizer page. Apply / Repair only.
 * Full verify-all is Settings → Verify optimizers (not a huge CTA here).
 */
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
  /** Games: Potato (max FPS / muddy) vs Optimized (high FPS, normal look) */
  const [gamePreset, setGamePreset] = useState<GamePreset>('optimized')
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<Outcome>('idle')
  const [outcomeMsg, setOutcomeMsg] = useState<string | null>(null)
  const [reportOpen, setReportOpen] = useState(false)

  useEffect(() => {
    let cancelled = false
    setDetecting(true)
    setError(null)
    setBusy(false)
    setProgress(0)
    setProgressText('')
    setOutcome('idle')
    setOutcomeMsg(null)
    setReportOpen(false)
    ;(async () => {
      try {
        const s = await host.detect(moduleId)
        if (cancelled) return
        setStatus(s)
        if (s.options?.useGsync != null) setUseGsync(!!s.options.useGsync)
        if (s.options?.preferLowestLatency != null)
          setPreferLowestLatency(!!s.options.preferLowestLatency)
        if (s.options?.gamePreset === 'potato' || s.options?.gamePreset === 'optimized')
          setGamePreset(s.options.gamePreset)
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
    setReportOpen(false)
    try {
      let s = await host.apply(moduleId, {
        experimental: true,
        useGsync,
        preferLowestLatency,
        gamePreset,
      })
      setStatus(s)
      setProgressText('Verifying live…')
      setProgress(92)
      // Force live re-detect so tiles match disk (not just apply return)
      s = await host.detect(moduleId, { force: true })
      setStatus(s)
      const feats = featuresForSelection(
        moduleId,
        s.features,
        { experimental: true, useGsync, preferLowestLatency, gamePreset },
      )
      const stats = statusFromFeatures(feats, s.isApplied)
      if (stats.offCount === 0 || (s.isApplied && stats.offCount === 0)) {
        setOutcome('applied')
        setOutcomeMsg(
          stats.total > 0
            ? `Verified — ${stats.onCount}/${stats.total} on.`
            : 'Verified on this PC.',
        )
      } else if (s.isApplied || stats.onCount > 0) {
        setOutcome('partial')
        setOutcomeMsg(
          `Finished with gaps — ${stats.onCount}/${stats.total} on (${stats.offCount} still off).`,
        )
      } else {
        setOutcome('partial')
        setOutcomeMsg(
          `Finished — ${stats.onCount}/${stats.total} on. Reapply if needed.`,
        )
      }
      if ((s.applyReport?.length ?? 0) > 0) setReportOpen(true)
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Apply failed'
      setError(msg)
      setOutcome('failed')
      setOutcomeMsg(
        msg.includes('Full log:')
          ? msg
          : `${msg}\nOpen Logs from Settings for apply-{module}-latest.log`,
      )
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
    setReportOpen(false)
    try {
      const s = await host.repair(moduleId)
      setStatus(s)
      setOutcome('repaired')
      setOutcomeMsg('Repair finished — Exo changes reversed. Use Settings → Verify to re-check.')
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
    () => ({ experimental: true, useGsync, preferLowestLatency, gamePreset }),
    [useGsync, preferLowestLatency, gamePreset],
  )

  const features = useMemo(
    () =>
      status
        ? featuresForSelection(moduleId, status.features, selection)
        : [],
    [moduleId, status, selection],
  )

  const classified = useMemo(
    () =>
      classifyStatus({
        detecting,
        busy,
        busyText: progressText || 'Working…',
        outcome,
        hostStatusText: status?.statusText,
        hostDetail: outcomeMsg || status?.detail,
        hostStatusKind: status?.statusKind,
        isApplied: status?.isApplied,
        features,
      }),
    [detecting, busy, progressText, outcome, outcomeMsg, status, features],
  )

  const tone: Tone = classified.tone
  const reportSteps = useMemo(
    () => parseApplyReport(status?.applyReport),
    [status?.applyReport],
  )

  const uiLocked = detecting || busy
  // Don't run Apply/Repair when the target isn't on this PC (Steam / NVIDIA).
  const canApply =
    classified.kind !== 'missing' &&
    classified.kind !== 'blocked' &&
    status?.statusKind !== 'missing'

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

      {moduleId === 'games' && (
        <section className="glass specular shrink-0 rounded-2xl px-3 py-2">
          <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">
            Profile
          </p>
          <div className="mt-1.5">
            <Segmented
              value={gamePreset}
              onChange={(v) => setGamePreset(v === 'potato' ? 'potato' : 'optimized')}
              disabled={uiLocked}
              options={[
                { id: 'potato', label: 'Potato' },
                { id: 'optimized', label: 'Optimized' },
              ]}
            />
          </div>
          <p className="mt-1.5 text-[11px] leading-snug text-muted">
            {gamePreset === 'potato'
              ? 'Max FPS — low textures, short draw distance, heavy effect cuts.'
              : 'High FPS — normal-looking textures; cuts post, fog, and heavy shadows.'}
          </p>
        </section>
      )}

      {/* Status — shared vocabulary (Ready / Applied / Partial / …) */}
      <section
        className="glass specular shrink-0 rounded-2xl px-3.5 py-2.5"
        style={
          tone === 'ok'
            ? { boxShadow: 'inset 0 0 0 1px color-mix(in srgb, var(--color-success) 35%, transparent)' }
            : tone === 'bad'
              ? { boxShadow: 'inset 0 0 0 1px color-mix(in srgb, var(--color-error) 40%, transparent)' }
              : tone === 'warn'
                ? { boxShadow: 'inset 0 0 0 1px #fbbf2444' }
                : undefined
        }
      >
        <div className="flex items-start gap-2.5">
          <span
            className={`mt-1 h-2 w-2 shrink-0 rounded-full ${
              tone === 'ok'
                ? 'bg-success shadow-[0_0_8px_var(--color-success)]'
                : tone === 'bad'
                  ? 'bg-error shadow-[0_0_8px_var(--color-error)]'
                  : tone === 'warn'
                    ? 'bg-amber-400'
                    : 'bg-muted/50'
            }`}
            aria-hidden
          />
          <div className="min-w-0 flex-1">
            <p
              className={`truncate text-[16px] font-semibold tracking-tight leading-snug ${
                tone === 'ok'
                  ? 'text-success'
                  : tone === 'bad'
                    ? 'text-error'
                    : tone === 'warn'
                      ? 'text-amber-300'
                      : 'text-text'
              }`}
            >
              {classified.headline}
            </p>
            <p className="mt-0.5 line-clamp-2 text-[12px] leading-snug text-muted">
              {classified.detail}
            </p>
            {error && outcome !== 'failed' && (
              <p className="mt-1 text-[12px] text-error line-clamp-2">{error}</p>
            )}
            {busy && (
              <div className="mt-2">
                {/* Step text is already in detail — bar only shows percent */}
                <div className="mb-1 flex justify-end text-[11px] tabular text-muted">
                  {progress >= 0 ? `${Math.round(progress)}%` : ''}
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
            {reportSteps.length > 0 && !busy && (
              <div className="mt-2">
                <button
                  type="button"
                  onClick={() => setReportOpen((v) => !v)}
                  className="text-[11px] font-semibold text-secondary hover:text-text"
                >
                  {reportOpen ? 'Hide last apply' : 'Last apply'} · {reportSteps.length} steps
                </button>
                {reportOpen && (
                  <ul className="mt-1 max-h-20 space-y-0.5 overflow-y-auto">
                    {reportSteps.map((s) => (
                      <li
                        key={s.raw}
                        className="flex items-center gap-1.5 text-[10px] leading-tight"
                      >
                        <span
                          className={
                            s.status === 'ok'
                              ? 'text-success'
                              : s.status === 'fail'
                                ? 'text-error'
                                : 'text-muted'
                          }
                        >
                          {s.status === 'ok' ? '✓' : s.status === 'fail' ? '✕' : '·'}
                        </span>
                        <span className="truncate text-muted">
                          {s.id}
                          {s.reason ? `: ${s.reason}` : ''}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
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
              : classified.total === 0
                ? '—'
                : `${classified.on}/${classified.total} on`}
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
            <ul className="grid h-full grid-cols-2 content-evenly gap-x-2 gap-y-1">
              {features.map((f) => (
                <li
                  key={f.title}
                  title={f.detail || f.title}
                  className={`flex min-w-0 items-center gap-2 rounded-lg px-2.5 py-2 ${
                    f.active ? 'bg-white/[0.03]' : ''
                  }`}
                >
                  <span
                    className={`flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full text-[10px] font-bold leading-none ${
                      f.active
                        ? 'bg-success/20 text-success'
                        : 'bg-white/5 text-muted'
                    }`}
                    aria-hidden
                  >
                    {f.active ? '✓' : '·'}
                  </span>
                  <span
                    className={`min-w-0 truncate text-[12px] font-medium leading-tight ${
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

      {!canApply && !detecting && (
        <section className="glass specular shrink-0 rounded-2xl px-3 py-2.5">
          <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">
            Not available on this PC
          </p>
          <p className="mt-1 text-[12px] leading-snug text-muted">
            {classified.detail ||
              'Install the app or hardware for this optimizer, then reopen the card.'}
          </p>
        </section>
      )}

      <div className="flex shrink-0 gap-2">
        <motion.button
          type="button"
          disabled={uiLocked || !canApply}
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
          disabled={uiLocked || !canApply}
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
