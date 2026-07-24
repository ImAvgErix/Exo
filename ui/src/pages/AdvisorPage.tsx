import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { motion, useReducedMotion } from 'framer-motion'
import { host, type AdvisorInsight, type AdvisorResult } from '../lib/host'

const easeOut = [0.23, 1, 0.32, 1] as const

/**
 * Functional advisor surface (not a design piece — the design layer restyles it).
 * Renders the deterministic, offline insights from host.advisorInsights(): each
 * one points at an existing module action, nothing is auto-applied here.
 */
export function AdvisorPage() {
  const reduce = useReducedMotion()
  const nav = useNavigate()
  const [data, setData] = useState<AdvisorResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const r = await host.advisorInsights()
      setData(r)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Advisor failed')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  function act(insight: AdvisorInsight) {
    if (!insight.moduleId) return
    // Never auto-applies — routes to the module where the user drives Apply/Repair.
    nav(`/module/${insight.moduleId}`)
  }

  return (
    <motion.section
      className="glass specular flex h-full min-h-0 flex-col overflow-hidden rounded-2xl"
      initial={reduce ? false : { opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={reduce ? { duration: 0 } : { duration: 0.28, ease: easeOut }}
    >
      <div className="flex shrink-0 items-center justify-between gap-3 border-b border-glass-border px-4 py-3.5">
        <div className="min-w-0">
          <p className="text-[10px] font-semibold tracking-[0.16em] text-muted">ADVISOR</p>
          <p className="truncate text-[13px] font-semibold leading-snug">
            {data?.summary ?? (loading ? 'Reading this PC…' : '—')}
          </p>
        </div>
        <button
          type="button"
          disabled={loading}
          onClick={() => void load()}
          className="rounded-lg bg-raised px-2.5 py-1 text-[11px] font-semibold ring-1 ring-glass-border hover:bg-[#24242C] hover:text-text disabled:opacity-50"
          title="Re-read live state and recompute advice"
        >
          {loading ? '…' : 'Refresh'}
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-3 py-3">
        {error ? (
          <p className="px-1 py-2 text-sm text-error">{error}</p>
        ) : data && data.insights.length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-1 text-center">
            <p className="text-2xl">✓</p>
            <p className="text-sm font-semibold">Nothing to look at</p>
            <p className="text-[12px] text-muted">
              {data.optimized > 0
                ? 'Every detected module is optimized.'
                : 'Apply an optimizer to get started.'}
            </p>
          </div>
        ) : (
          <ul className="flex flex-col gap-2">
            {(data?.insights ?? []).map((ins, i) => (
              <InsightRow key={i} insight={ins} onAct={() => act(ins)} reduce={!!reduce} index={i} />
            ))}
          </ul>
        )}
      </div>
    </motion.section>
  )
}

function InsightRow({
  insight,
  onAct,
  reduce,
  index,
}: {
  insight: AdvisorInsight
  onAct: () => void
  reduce: boolean
  index: number
}) {
  const dot = dotColor(insight.severity)
  const actionLabel = labelForAction(insight.action)
  return (
    <motion.li
      className="flex items-start gap-3 rounded-xl bg-raised px-3 py-2.5 ring-1 ring-glass-border"
      initial={reduce ? false : { opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={reduce ? { duration: 0 } : { duration: 0.22, ease: easeOut, delay: index * 0.03 }}
    >
      <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${dot}`} aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="text-[13px] font-semibold leading-snug">{insight.title}</p>
        <p className="mt-0.5 text-[12px] leading-snug text-muted">{insight.detail}</p>
      </div>
      {actionLabel && insight.moduleId ? (
        <button
          type="button"
          onClick={onAct}
          className="shrink-0 self-center rounded-lg bg-sunken px-2.5 py-1.5 text-[11px] font-semibold ring-1 ring-glass-border hover:bg-[#24242C] hover:text-text"
        >
          {actionLabel}
        </button>
      ) : null}
    </motion.li>
  )
}

function dotColor(sev: AdvisorInsight['severity']) {
  switch (sev) {
    case 'warn':
      return 'bg-error'
    case 'suggest':
      return 'bg-steam'
    default:
      return 'bg-success'
  }
}

function labelForAction(action: AdvisorInsight['action']) {
  switch (action) {
    case 'apply':
      return 'Apply'
    case 'reapply':
      return 'Reapply'
    case 'open':
      return 'Open'
    default:
      return null
  }
}
