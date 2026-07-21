import { useEffect, useMemo, useState } from 'react'
import { motion, useReducedMotion } from 'framer-motion'
import {
  host,
  type GameHubSnapshot,
  type GameListItem,
  type GamePreset,
  onHostEvent,
} from '../lib/host'
import {
  classifyStatus,
  parseApplyReport,
  type Tone,
} from '../lib/moduleUx'

const easeOut = [0.23, 1, 0.32, 1] as const

type Outcome = 'idle' | 'applied' | 'partial' | 'failed' | 'repaired'

/** Per-game logos — cache-bust so new assets load after rebuild. */
function gameIcon(g: GameListItem): { src: string; src2x?: string } {
  const v = 'v=5'
  if (g.id === 'marvel-rivals') {
    return {
      src: `/logos/marvel-rivals.png?${v}`,
      src2x: `/logos/marvel-rivals-128.png?${v}`,
    }
  }
  const byId: Record<string, string> = {
    'black-ops-7': `/logos/black-ops-7.png?${v}`,
    fortnite: `/logos/fortnite.png?${v}`,
    valorant: `/logos/valorant.png?${v}`,
    cs2: `/logos/cs2.png?${v}`,
    'apex-legends': `/logos/apex-legends.png?${v}`,
    'helldivers-2': `/logos/helldivers-2.png?${v}`,
    'the-finals': `/logos/the-finals.png?${v}`,
    predecessor: `/logos/predecessor.png?${v}`,
    'league-of-legends': `/logos/league-of-legends.png?${v}`,
    'marvel-rivals': `/logos/marvel-rivals.png?${v}`,
  }
  const src = g.icon?.trim()
    ? `${g.icon.trim()}${g.icon.includes('?') ? '&' : '?'}${v}`
    : byId[g.id] || `/logos/${g.id}.png?${v}`
  return { src }
}

function GameRowIcon({
  game,
  active,
}: {
  game: GameListItem
  active: boolean
}) {
  const { src, src2x } = gameIcon(game)
  // Compact tile — matches nav logos scale better than a 48px hero square
  const px = 32
  return (
    <span className="relative flex h-8 w-8 shrink-0 items-center justify-center">
      <img
        src={src}
        srcSet={src2x ? `${src} 1x, ${src2x} 2x` : undefined}
        alt=""
        width={px}
        height={px}
        draggable={false}
        decoding="async"
        className="pointer-events-none block h-8 w-8 select-none"
        style={{
          borderRadius: 5,
          boxShadow: active
            ? '0 0 0 1px rgba(0,0,0,0.12), 0 1px 4px rgba(0,0,0,0.2)'
            : '0 0 0 1px rgba(255,255,255,0.08), 0 1px 3px rgba(0,0,0,0.3)',
        }}
        onError={(e) => {
          const el = e.currentTarget
          if (el.dataset.fallback) return
          el.dataset.fallback = '1'
          el.src = '/logos/games.png'
          el.removeAttribute('srcset')
        }}
      />
    </span>
  )
}

/**
 * Games hub — different layout from other optimizers:
 * left rail = pick a game (icon + title); right = profile + features + Apply/Repair.
 * Verify-all lives in Settings, not here.
 */
export function GamesPage() {
  const reduce = useReducedMotion()
  const [hub, setHub] = useState<GameHubSnapshot | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [preset, setPreset] = useState<GamePreset>('optimized')
  const [detecting, setDetecting] = useState(true)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(0)
  const [progressText, setProgressText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<Outcome>('idle')
  const [outcomeMsg, setOutcomeMsg] = useState<string | null>(null)
  const [reportOpen, setReportOpen] = useState(false)

  async function refresh(gameId?: string | null) {
    setDetecting(true)
    setError(null)
    try {
      const snap = await host.listGames(gameId ?? selectedId ?? undefined)
      setHub(snap)
      setSelectedId(snap.selectedGameId)
      const p = snap.selected?.options?.gamePreset
      if (p === 'potato' || p === 'optimized') setPreset(p)
      if (snap.selected?.isApplied) {
        setOutcome('applied')
        setOutcomeMsg(null)
      } else {
        setOutcome('idle')
        setOutcomeMsg(null)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Detect failed')
    } finally {
      setDetecting(false)
    }
  }

  useEffect(() => {
    void refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    const off = onHostEvent('module.progress', (data) => {
      const d = data as { module?: string; percent?: number; status?: string }
      if (d.module && d.module !== 'games') return
      if (typeof d.percent === 'number') setProgress(d.percent)
      if (d.status) setProgressText(d.status)
    })
    return () => off()
  }, [])

  async function selectGame(g: GameListItem) {
    if (busy) return
    setSelectedId(g.id)
    setOutcome('idle')
    setOutcomeMsg(null)
    setReportOpen(false)
    await refresh(g.id)
  }

  async function runApply() {
    if (!selectedId) return
    const row = hub?.games.find((g) => g.id === selectedId)
    if (!row?.installed) {
      setError('Game is not installed — Apply is blocked.')
      setOutcome('failed')
      setOutcomeMsg('Not installed.')
      return
    }
    setBusy(true)
    setError(null)
    setOutcome('idle')
    setOutcomeMsg(null)
    setProgress(0)
    setProgressText('Starting…')
    setReportOpen(false)
    try {
      // Host always forces borderless — apply throws only on hard failure.
      let snap = await host.applyGame(selectedId, preset, 'borderless')
      setHub(snap)
      setSelectedId(snap.selectedGameId)
      setProgressText('Verifying live…')
      setProgress(92)
      // Fresh hub after apply so tiles match disk
      snap = await host.listGames(selectedId)
      setHub(snap)
      setSelectedId(snap.selectedGameId)
      const feats = snap.selected?.features ?? []
      const on = feats.filter((f) => f.active).length
      const total = feats.length
      // Apply RPC succeeded → always show applied. Live feature rows are diagnostics only.
      const label = preset === 'potato' ? 'Potato' : 'Optimized'
      // Keep toggle on what we just applied (host may also echo it back).
      setPreset(preset)
      const p = snap.selected?.options?.gamePreset
      if (p === 'potato' || p === 'optimized') setPreset(p)
      setOutcome('applied')
      setOutcomeMsg(
        total > 0
          ? `${label} applied (borderless) — ${on}/${total} checks on. Restart the game.`
          : `${label} applied (borderless). Restart the game.`,
      )
      if ((snap.selected?.applyReport?.length ?? 0) > 0) setReportOpen(true)
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Apply failed'
      setError(msg)
      setOutcome('failed')
      setOutcomeMsg(msg)
    } finally {
      setBusy(false)
      setProgressText('')
    }
  }

  async function runRepair() {
    if (!selectedId) return
    const row = hub?.games.find((g) => g.id === selectedId)
    if (!row?.installed) {
      setError('Game is not installed — nothing to repair.')
      setOutcome('failed')
      setOutcomeMsg('Not installed — Repair is blocked.')
      return
    }
    setBusy(true)
    setError(null)
    setOutcome('idle')
    setOutcomeMsg(null)
    setReportOpen(false)
    try {
      const snap = await host.repairGame(selectedId)
      setHub(snap)
      setSelectedId(snap.selectedGameId)
      setOutcome('repaired')
      setOutcomeMsg('Repair finished — Exo changes reversed for this game.')
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Repair failed'
      setError(msg)
      setOutcome('failed')
      setOutcomeMsg(msg)
    } finally {
      setBusy(false)
    }
  }

  const selected = hub?.selected ?? null
  const features = selected?.features ?? []
  const locked = detecting || busy
  const selectedRow = hub?.games.find((g) => g.id === selectedId) ?? null
  const isInstalled = selectedRow?.installed === true
  const isReady = selectedRow?.ready === true
  /** Apply/Repair only when installed + optimizer wired */
  const canApply = !!selectedId && isReady && isInstalled
  const notInstalled = !!selectedId && isReady && !isInstalled

  const classified = useMemo(
    () =>
      classifyStatus({
        detecting,
        busy,
        busyText: progressText || 'Working…',
        outcome,
        hostStatusText: selected?.statusText ?? hub?.statusText,
        hostDetail: outcomeMsg || selected?.detail || hub?.detail,
        isApplied: selected?.isApplied,
        features,
      }),
    [
      detecting,
      busy,
      progressText,
      outcome,
      outcomeMsg,
      selected,
      hub,
      features,
    ],
  )

  const tone: Tone = classified.tone
  const reportSteps = useMemo(
    () => parseApplyReport(selected?.applyReport),
    [selected?.applyReport],
  )

  const games = hub?.games ?? []
  const installed = useMemo(() => games.filter((g) => g.installed).length, [games])

  return (
    <motion.div
      className="flex h-full min-h-0 flex-col gap-2 overflow-hidden"
      initial={reduce ? false : { opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={reduce ? { duration: 0 } : { duration: 0.24, ease: easeOut }}
    >
      {/* Catalog header */}
      <section className="glass specular shrink-0 rounded-2xl px-3.5 py-2.5">
        <div className="flex items-center justify-between gap-2">
          <div>
            <p className="text-[10px] font-semibold tracking-[0.16em] text-muted">GAMES</p>
            <p className="mt-0.5 text-[14px] font-semibold tracking-tight text-text">
              {detecting && !hub
                ? 'Detecting…'
                : `${installed} installed · ${games.length} in catalog`}
            </p>
          </div>
          <p className="text-[11px] text-muted">Pick a title → profile → Apply</p>
        </div>
      </section>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,12.5rem)_1fr] gap-2 overflow-hidden">
        {/* Game list with icons */}
        <section className="glass specular flex min-h-0 flex-col overflow-hidden rounded-2xl">
          <p className="shrink-0 border-b border-white/[0.06] px-3 py-2 text-[11px] font-semibold tracking-[0.06em] text-secondary">
            Library
          </p>
          <ul className="min-h-0 flex-1 space-y-0.5 overflow-y-auto p-1.5">
            {detecting && games.length === 0
              ? Array.from({ length: 4 }, (_, i) => (
                  <li key={i} className="h-12 animate-pulse rounded-xl bg-raised/50" />
                ))
              : games.map((g) => {
                  const active = g.id === selectedId
                  const missing = g.ready && !g.installed
                  return (
                    <li key={g.id}>
                      <button
                        type="button"
                        disabled={locked}
                        onClick={() => void selectGame(g)}
                        className={`flex w-full items-center gap-2.5 rounded-xl px-2 py-2 text-left transition-colors ${
                          active
                            ? 'bg-white text-black shadow-[0_1px_0_rgb(255_255_255/0.25)_inset]'
                            : missing
                              ? 'bg-transparent text-text/45 hover:bg-raised/60 hover:text-text/70'
                              : 'bg-transparent text-text hover:bg-raised'
                        } disabled:opacity-50`}
                      >
                        <span className={missing && !active ? 'opacity-45 grayscale' : undefined}>
                          <GameRowIcon game={g} active={active} />
                        </span>
                        <span className="min-w-0 flex-1">
                          <span
                            className={`block w-full truncate text-[12px] font-semibold leading-tight ${
                              missing && !active ? 'text-text/50' : ''
                            }`}
                          >
                            {g.title}
                          </span>
                          <span
                            className={`mt-0.5 block w-full truncate text-[10px] ${
                              active ? 'text-black/55' : missing ? 'text-muted/80' : 'text-muted'
                            }`}
                          >
                            {!g.ready
                              ? 'Coming soon'
                              : g.applied
                                ? g.activePreset
                                  ? `${g.activePreset} profile on`
                                  : 'Profile on'
                                : g.installed
                                  ? g.platform
                                  : 'Not installed'}
                          </span>
                        </span>
                      </button>
                    </li>
                  )
                })}
          </ul>
        </section>

        {/* Detail */}
        <div className="flex min-h-0 flex-col gap-2 overflow-hidden">
          <section
            className="glass specular shrink-0 rounded-2xl px-3.5 py-2.5"
            style={
              tone === 'ok'
                ? {
                    boxShadow:
                      'inset 0 0 0 1px color-mix(in srgb, var(--color-success) 35%, transparent)',
                  }
                : tone === 'bad'
                  ? {
                      boxShadow:
                        'inset 0 0 0 1px color-mix(in srgb, var(--color-error) 40%, transparent)',
                    }
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
                  className={`truncate text-[16px] font-semibold tracking-tight ${
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

          {canApply && (
            <section className="glass specular shrink-0 rounded-2xl px-3 py-2">
              <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">Profile</p>
              <div className="mt-1.5">
                <Segmented
                  value={preset}
                  onChange={(v) => setPreset(v === 'potato' ? 'potato' : 'optimized')}
                  disabled={locked}
                  options={[
                    { id: 'potato', label: 'Potato' },
                    { id: 'optimized', label: 'Optimized' },
                  ]}
                />
              </div>
              <p className="mt-1.5 text-[11px] leading-snug text-muted">
                {preset === 'potato'
                  ? 'Max FPS — low textures, short draw distance, heavy effect cuts.'
                  : 'High FPS — normal-looking textures; cuts post, fog, and heavy shadows.'}{' '}
                Always sets borderless for this game (game-specific config keys).
              </p>
            </section>
          )}

          {notInstalled && (
            <section className="glass specular shrink-0 rounded-2xl px-3 py-2.5">
              <p className="text-[11px] font-semibold tracking-[0.04em] text-secondary">
                Not installed
              </p>
              <p className="mt-1 text-[12px] leading-snug text-muted">
                This game isn&apos;t on this PC (or hasn&apos;t been launched yet). Apply stays
                locked until it is installed and run once.
              </p>
            </section>
          )}

          {/* Checks only when installed — hide empty feature noise for missing titles */}
          {isInstalled && (
            <section className="glass specular flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl">
              <div className="flex shrink-0 items-baseline justify-between gap-2 border-b border-white/[0.06] px-3 py-2">
                <p className="text-[11px] font-semibold tracking-[0.06em] text-secondary">Checks</p>
                <p className="text-[11px] tabular text-muted">
                  {detecting || !selected
                    ? '…'
                    : classified.total === 0
                      ? '—'
                      : `${classified.on}/${classified.total} on`}
                </p>
              </div>
              <div className="min-h-0 flex-1 overflow-hidden px-2 py-1.5">
                {detecting && !selected ? (
                  <div className="grid grid-cols-2 gap-x-2 gap-y-1">
                    {Array.from({ length: 8 }, (_, i) => (
                      <div
                        key={i}
                        className="h-7 animate-pulse rounded-md bg-raised/50"
                        aria-hidden
                      />
                    ))}
                  </div>
                ) : features.length === 0 ? (
                  <p className="px-2 py-4 text-center text-[12px] text-muted">
                    Apply to verify this game’s stack.
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
          )}

          {!isInstalled && <div className="min-h-0 flex-1" aria-hidden />}

          <div className="flex shrink-0 gap-2">
            <motion.button
              type="button"
              disabled={locked || !canApply}
              whileTap={reduce ? undefined : { scale: 0.96 }}
              onClick={() => void runApply()}
              className="flex-1 rounded-xl bg-white py-2.5 text-sm font-semibold text-black shadow-[0_0_24px_rgb(255_255_255/0.12)] disabled:opacity-40"
            >
              {busy ? 'Working…' : selected?.isApplied ? 'Reapply' : 'Apply'}
            </motion.button>
            <motion.button
              type="button"
              disabled={locked || !canApply}
              whileTap={reduce ? undefined : { scale: 0.96 }}
              onClick={() => void runRepair()}
              className="glass-chip rounded-xl px-5 py-2.5 text-sm font-semibold text-text disabled:opacity-40"
            >
              Repair
            </motion.button>
          </div>
        </div>
      </div>
    </motion.div>
  )
}

function Segmented({
  value,
  onChange,
  options,
  disabled,
}: {
  value: string
  onChange: (id: string) => void
  options: { id: string; label: string }[]
  disabled?: boolean
}) {
  return (
    <div role="radiogroup" className="flex gap-1 rounded-lg bg-black/50 p-1 ring-1 ring-white/10">
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
