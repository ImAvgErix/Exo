import type { FeatureRow } from './featurePreview'
import { checkableFeatures, statusFromFeatures } from './featurePreview'

/** Shared status vocabulary for every optimizer module. */
export type StatusKind = 'checking' | 'ready' | 'applied' | 'partial' | 'missing' | 'blocked' | 'failed' | 'repaired'

export type Tone = 'ok' | 'bad' | 'warn' | 'neutral'

export type ApplyReportStep = {
  id: string
  status: 'ok' | 'fail' | 'skip' | 'unknown'
  reason: string
  raw: string
}

/** Parse host lines like "startup|ok:removed=0" or "yield|ok". */
export function parseApplyReport(lines: string[] | undefined | null): ApplyReportStep[] {
  if (!lines?.length) return []
  return lines.map((raw) => {
    const pipe = raw.indexOf('|')
    if (pipe < 0) return { id: raw, status: 'unknown' as const, reason: '', raw }
    const id = raw.slice(0, pipe).trim() || 'step'
    const rest = raw.slice(pipe + 1).trim()
    const colon = rest.indexOf(':')
    const statusWord = (colon >= 0 ? rest.slice(0, colon) : rest).trim().toLowerCase()
    const reason = colon >= 0 ? rest.slice(colon + 1).trim() : ''
    let status: ApplyReportStep['status'] = 'unknown'
    if (statusWord === 'ok' || statusWord.startsWith('ok')) status = 'ok'
    else if (statusWord === 'fail' || statusWord.startsWith('fail')) status = 'fail'
    else if (statusWord === 'skip' || statusWord.startsWith('skip')) status = 'skip'
    return { id, status, reason, raw }
  })
}

export function toneForKind(kind: StatusKind): Tone {
  switch (kind) {
    case 'applied':
    case 'repaired':
      return 'ok'
    case 'failed':
    case 'blocked':
      return 'bad'
    case 'partial':
    case 'missing':
      return 'warn'
    default:
      return 'neutral'
  }
}

export function headlineForKind(
  kind: StatusKind,
  opts?: { on?: number; total?: number; offCount?: number; busyText?: string },
): string {
  const on = opts?.on
  const total = opts?.total
  const count =
    typeof on === 'number' && typeof total === 'number' && total > 0
      ? ` · ${on}/${total} on`
      : ''
  switch (kind) {
    case 'checking':
      return opts?.busyText || 'Checking this PC…'
    case 'applied':
      return `Applied${count}`
    case 'partial':
      return opts?.offCount && opts.offCount > 0
        ? `Partial · ${opts.offCount} still off${count}`
        : `Partial${count}`
    case 'ready':
      return opts?.offCount && opts.offCount > 0
        ? `Ready · ${opts.offCount} need Apply`
        : 'Ready'
    case 'missing':
      return 'Missing target'
    case 'blocked':
      return 'Blocked'
    case 'failed':
      return 'Failed'
    case 'repaired':
      return 'Repaired'
    default:
      return '—'
  }
}

/**
 * Classify live feature list into shared vocabulary.
 * Host isApplied + checkable gaps drive Partial vs Applied.
 */
export function classifyStatus(args: {
  detecting?: boolean
  busy?: boolean
  busyText?: string
  outcome?: 'idle' | 'applied' | 'partial' | 'failed' | 'repaired'
  hostStatusText?: string
  hostDetail?: string
  /** Host MapState statusKind — prefer over client regex when set */
  hostStatusKind?: string
  isApplied?: boolean
  features: FeatureRow[]
}): {
  kind: StatusKind
  headline: string
  detail: string
  tone: Tone
  on: number
  total: number
  offCount: number
} {
  const { features } = args
  const on = features.filter((f) => f.active).length
  const total = features.length
  const stats = statusFromFeatures(features, args.isApplied)
  const offCount = stats.offCount
  const offTitles = checkableFeatures(features)
    .filter((f) => !f.active)
    .map((f) => f.title)

  if (args.detecting && total === 0) {
    return {
      kind: 'checking',
      headline: headlineForKind('checking'),
      detail: 'Reading live state…',
      tone: 'neutral',
      on: 0,
      total: 0,
      offCount: 0,
    }
  }

  if (args.busy) {
    // One line only — ModulePage also shows this under the progress bar.
    // Don't repeat the same string as headline + detail.
    return {
      kind: 'checking',
      headline: 'Working…',
      detail: args.busyText || 'Applying changes…',
      tone: 'neutral',
      on,
      total,
      offCount,
    }
  }

  if (args.outcome === 'failed') {
    return {
      kind: 'failed',
      headline: headlineForKind('failed'),
      detail: args.hostDetail || 'See log for details.',
      tone: 'bad',
      on,
      total,
      offCount,
    }
  }

  if (args.outcome === 'repaired') {
    return {
      kind: 'repaired',
      headline: headlineForKind('repaired', { on, total }),
      detail: 'Exo changes reversed. Verify if you want a live re-check.',
      tone: 'ok',
      on,
      total,
      offCount,
    }
  }

  // Prefer host-provided statusKind when present (honest missing/partial from C#).
  if (
    args.hostStatusKind === 'missing' &&
    !args.isApplied &&
    args.outcome !== 'applied' &&
    args.outcome !== 'partial'
  ) {
    return {
      kind: 'missing',
      headline: headlineForKind('missing'),
      detail: args.hostDetail || args.hostStatusText || 'Install the target, then reopen.',
      tone: 'warn',
      on,
      total,
      offCount,
    }
  }

  const hostText = `${args.hostStatusText || ''} ${args.hostDetail || ''}`.toLowerCase()
  const missing =
    args.hostStatusKind === 'missing' ||
    /not installed|steam not installed|no .*gpu|marvel rivals not installed|missing target/.test(
      hostText,
    ) ||
    (total > 0 &&
      checkableFeatures(features).length > 0 &&
      checkableFeatures(features).every((f) => !f.active) &&
      /install|not found|not installed/i.test(features[0]?.detail || features[0]?.title || ''))

  if (missing && !args.isApplied) {
    return {
      kind: 'missing',
      headline: headlineForKind('missing'),
      detail: args.hostDetail || args.hostStatusText || 'Install the target, then reopen.',
      tone: 'warn',
      on,
      total,
      offCount,
    }
  }

  if (args.outcome === 'partial' || (offCount > 0 && (args.isApplied || on > 0))) {
    // Applied with gaps, or mixed tiles
    if (offCount > 0 && (args.isApplied || args.outcome === 'partial' || on > 0)) {
      const kind: StatusKind =
        args.isApplied || args.outcome === 'applied' || args.outcome === 'partial'
          ? 'partial'
          : 'ready'
      return {
        kind,
        headline: headlineForKind(kind, { on, total, offCount }),
        detail:
          offTitles.length > 0
            ? `Off: ${offTitles.join(', ')}.`
            : args.hostDetail || 'Some checks still off.',
        tone: toneForKind(kind),
        on,
        total,
        offCount,
      }
    }
  }

  if (offCount === 0 && total > 0 && (args.isApplied || args.outcome === 'applied' || on === total)) {
    return {
      kind: 'applied',
      headline: headlineForKind('applied', { on, total }),
      detail: 'Verified on this PC from live checks.',
      tone: 'ok',
      on,
      total,
      offCount: 0,
    }
  }

  if (offCount === 0 && total > 0) {
    return {
      kind: 'ready',
      headline: headlineForKind('ready', { on, total }),
      detail: args.hostDetail || 'Apply to optimize.',
      tone: 'neutral',
      on,
      total,
      offCount: 0,
    }
  }

  return {
    kind: 'ready',
    headline: headlineForKind('ready', { on, total, offCount }),
    detail:
      offTitles.length > 0
        ? `Off: ${offTitles.join(', ')}.`
        : args.hostDetail || 'Apply to optimize.',
    tone: 'neutral',
    on,
    total,
    offCount,
  }
}
