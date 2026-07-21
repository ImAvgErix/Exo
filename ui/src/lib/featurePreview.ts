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
  gamePreset?: 'potato' | 'optimized' | string
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
    'launcher junk cleaned',
    'profile',
    'dlss left alone',
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

/**
 * Build status from the list the user sees.
 * onCount/total always include every visible row so "4/6 on" matches the grid.
 * offCount only counts apply-relevant (non-info) gaps.
 */
export function statusFromFeatures(
  rows: FeatureRow[],
  isApplied?: boolean,
): { headline: string; offCount: number; onCount: number; total: number } {
  const visibleOn = rows.filter((f) => f.active).length
  const visibleTotal = rows.length
  const check = checkableFeatures(rows)
  const off = check.filter((f) => !f.active)

  if (visibleTotal === 0) {
    return {
      headline: isApplied ? 'Applied' : 'Ready to optimize',
      offCount: 0,
      onCount: 0,
      total: 0,
    }
  }

  // No checkable gaps → Applied (or Ready if nothing was applied yet)
  if (off.length === 0) {
    return {
      headline:
        isApplied || visibleOn === visibleTotal
          ? visibleOn === visibleTotal
            ? 'Applied'
            : `Applied · ${visibleOn}/${visibleTotal} on`
          : 'Ready to optimize',
      offCount: 0,
      onCount: visibleOn,
      total: visibleTotal,
    }
  }

  // Applied with live gaps → Partial (keep real offCount)
  if (isApplied) {
    return {
      headline: `Partial · ${off.length} still off · ${visibleOn}/${visibleTotal} on`,
      offCount: off.length,
      onCount: visibleOn,
      total: visibleTotal,
    }
  }

  if (off.length === 1) {
    return {
      headline: `1 setting needs Apply (${off[0].title})`,
      offCount: 1,
      onCount: visibleOn,
      total: visibleTotal,
    }
  }
  return {
    headline: `${off.length} settings need Apply`,
    offCount: off.length,
    onCount: visibleOn,
    total: visibleTotal,
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

  if (moduleId === 'games') {
    const potato = opts.gamePreset === 'potato'
    upsertInfo(
      rows,
      'Profile',
      potato
        ? 'Selected: Potato — max FPS, low textures / draw distance.'
        : 'Selected: Optimized — high FPS, normal-looking textures.',
    )
    patchTitleMatch(rows, /game profile/i, (f) => {
      f.detail = potato
        ? 'Next Apply: Potato profile + packs.'
        : 'Next Apply: Optimized profile + packs.'
    })
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
        : moduleId === 'games'
          ? opts.gamePreset === 'potato'
            ? 'Potato'
            : 'Optimized'
          : null

  if (!baseDetail || baseDetail === '—') {
    return profile
      ? moduleId === 'games'
        ? `${profile} profile selected.`
        : `${profile} stack.`
      : 'Ready.'
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
