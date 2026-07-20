import type { ModuleId } from './host'

export type FeatureRow = {
  title: string
  detail: string
  active: boolean
  /** Informational only — never counted as a gap / need-Apply row */
  info?: boolean
}

type Opts = {
  experimental: boolean
  useGsync: boolean
  preferLowestLatency: boolean
}

/** Titles that are always informational (not real optimize checks). */
const INFO_TITLES = new Set(
  [
    'optimization verified',
    'anti-cheat untouched',
    'safe repair',
    'policy',
    'stack profile',
    'latency / sync policy',
    'display scaling & color',
    'gaming multimedia stack',
    'adapter',
    'last apply',
    'one-click repair ready',
  ].map((s) => s.toLowerCase()),
)

export function isInfoFeature(f: FeatureRow): boolean {
  if (f.info) return true
  return INFO_TITLES.has(f.title.trim().toLowerCase())
}

/** Rows that count toward "N settings need Apply". */
export function checkableFeatures(rows: FeatureRow[]): FeatureRow[] {
  return rows.filter((f) => !isInfoFeature(f))
}

/** Build status line from the same list the user sees. */
export function statusFromFeatures(
  rows: FeatureRow[],
  isApplied?: boolean,
): { headline: string; offCount: number; onCount: number; total: number } {
  const check = checkableFeatures(rows)
  const on = check.filter((f) => f.active).length
  const off = check.filter((f) => !f.active)
  const total = check.length
  if (total === 0) {
    return {
      headline: isApplied ? 'Applied' : 'Ready to optimize',
      offCount: 0,
      onCount: 0,
      total: 0,
    }
  }
  if (off.length === 0 || isApplied) {
    return {
      headline: isApplied || off.length === 0 ? 'Applied' : 'Ready to optimize',
      offCount: 0,
      onCount: on,
      total,
    }
  }
  if (off.length === 1) {
    return {
      headline: `1 setting needs Apply (${off[0].title})`,
      offCount: 1,
      onCount: on,
      total,
    }
  }
  return {
    headline: `${off.length} settings need Apply`,
    offCount: off.length,
    onCount: on,
    total,
  }
}

/** Rewrite / inject feature rows so the list matches the toggles about to Apply. */
export function featuresForSelection(
  moduleId: ModuleId,
  base: FeatureRow[] | undefined,
  opts: Opts,
): FeatureRow[] {
  const rows = (base ?? []).map((f) => ({ ...f, info: isInfoFeature(f) }))

  // Apply mode is always competitive max-aggression (no Stable/Experimental split).
  removeTitle(rows, 'Apply mode')
  removeTitle(rows, 'Experimental rebuild')
  removeTitle(rows, 'Experimental guard')
  removeTitle(rows, 'Experimental yield')
  removeTitle(rows, 'Experimental re-import')

  if (moduleId === 'nvidia') {
    upsertInfo(
      rows,
      'Latency / sync policy',
      opts.useGsync
        ? 'Next Apply (Profile Inspector): G-SYNC / VRR DRS pack. Scaling/color stay in Control Panel.'
        : 'Next Apply (Profile Inspector): raw-latency DRS pack. Scaling/color stay in Control Panel.',
    )
    upsertInfo(
      rows,
      'Display scaling & color',
      'Not forced by Exo. Use Open Control Panel for scaling, Full RGB, and NVIDIA color.',
    )
    patchTitleMatch(rows, /g-?sync|latency|sync policy|3d profile/i, (f) => {
      if (/latency|sync|g-?sync/i.test(f.title) && !isInfoFeature(f)) {
        f.detail = opts.useGsync
          ? 'Selected: G-SYNC / VRR on next Apply.'
          : 'Selected: raw latency (no VRR) on next Apply.'
      }
    })
  }

  if (moduleId === 'internet') {
    upsertInfo(
      rows,
      'Stack profile',
      opts.preferLowestLatency
        ? 'Selected: lowest latency (IM/RSC/LSO off; competitive Nagle/ACK + host stack).'
        : 'Selected: high throughput (multi-gig; FC/IM on where useful + competitive host stack).',
    )
    // Real host knobs from apply — keep as checkable only if host already sent a real row;
    // this is a description of the stack, not a live probe.
    upsertInfo(
      rows,
      'Gaming multimedia stack',
      'Network throttle off, max responsiveness, games priority class, foreground boost, and hardware GPU scheduling.',
    )
    removeTitle(rows, 'Host policy')
    removeTitle(rows, 'Policy')
    // Connection path is a probe, not "applied policy" alone
    const path = rows.find((r) => /connection path/i.test(r.title))
    if (path) path.info = false
  }

  // Stamp info flag after mutations
  for (const r of rows) {
    if (isInfoFeature(r)) r.info = true
  }

  return rows
}

export function statusDetailForSelection(
  moduleId: ModuleId,
  baseDetail: string | undefined,
  opts: Opts,
): string {
  const profile =
    moduleId === 'nvidia'
      ? opts.useGsync
        ? 'G-SYNC / VRR'
        : 'raw latency'
      : moduleId === 'internet'
        ? opts.preferLowestLatency
          ? 'lowest latency'
          : 'high throughput'
        : null

  if (!baseDetail || baseDetail === '—') {
    return profile ? `${profile} stack.` : 'Ready.'
  }
  return profile ? `${baseDetail} · ${profile}` : baseDetail
}

function upsertInfo(rows: FeatureRow[], title: string, detail: string) {
  const i = rows.findIndex((r) => r.title.toLowerCase() === title.toLowerCase())
  if (i >= 0) {
    rows[i] = { ...rows[i], title, detail, active: true, info: true }
  } else {
    rows.unshift({ title, detail, active: true, info: true })
  }
}

function removeTitle(rows: FeatureRow[], title: string) {
  const i = rows.findIndex((r) => r.title.toLowerCase() === title.toLowerCase())
  if (i >= 0) rows.splice(i, 1)
}

function patchTitleMatch(
  rows: FeatureRow[],
  re: RegExp,
  fn: (f: FeatureRow) => void,
) {
  for (const f of rows) {
    if (re.test(f.title)) fn(f)
  }
}
