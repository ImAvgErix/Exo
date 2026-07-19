import type { ModuleId, ModuleStatus } from './host'

export type FeatureRow = { title: string; detail: string; active: boolean }

type Opts = {
  experimental: boolean
  useGsync: boolean
  preferLowestLatency: boolean
}

/** Rewrite / inject feature rows so the list matches the toggles about to Apply. */
export function featuresForSelection(
  moduleId: ModuleId,
  base: FeatureRow[] | undefined,
  opts: Opts,
): FeatureRow[] {
  const rows = (base ?? []).map((f) => ({ ...f }))

  upsert(
    rows,
    'Apply mode',
    opts.experimental
      ? experimentalDetail(moduleId)
      : 'Stable — full safe reversible stack for this module.',
    true,
  )

  if (moduleId === 'nvidia') {
    upsert(
      rows,
      'Latency / sync policy',
      opts.useGsync
        ? 'Next Apply (Profile Inspector): G-SYNC / VRR DRS pack. Scaling/color stay in Control Panel.'
        : 'Next Apply (Profile Inspector): raw-latency DRS pack. Scaling/color stay in Control Panel.',
      true,
    )
    upsert(
      rows,
      'Display scaling & color',
      'Not forced by Exo. Use Open Control Panel for scaling, Full RGB, and NVIDIA color.',
      true,
    )
    // Older detect titles
    patchTitleMatch(rows, /g-?sync|latency|sync policy|3d profile/i, (f) => {
      if (/latency|sync|g-?sync/i.test(f.title)) {
        f.detail = opts.useGsync
          ? 'Selected: G-SYNC / VRR on next Apply.'
          : 'Selected: raw latency (no VRR) on next Apply.'
      }
    })
  }

  if (moduleId === 'internet') {
    upsert(
      rows,
      'Policy',
      opts.preferLowestLatency
        ? 'Selected: lowest latency (FC/IM-style path knobs off; gaming-oriented NIC stack).'
        : 'Selected: high throughput (multi-gig defaults; FC/IM kept where useful).',
      true,
    )
    upsert(
      rows,
      'Host policy',
      opts.experimental
        ? 'Experimental re-stamps MMCSS / Games task / Psched host knobs on Apply.'
        : 'Stable stamps safe host knobs (NTI=10, Responsiveness=10, Games task, Psched).',
      true,
    )
  }

  if (moduleId === 'discord' && opts.experimental) {
    upsert(
      rows,
      'Experimental rebuild',
      'Force client debloat + lean Equicord profile rebuild on Apply.',
      true,
    )
  } else {
    removeTitle(rows, 'Experimental rebuild')
  }

  if (moduleId === 'steam' && opts.experimental) {
    upsert(
      rows,
      'Experimental guard',
      'Tighter in-game soft-reclaim cadence (1s / 2s) on the memory guard.',
      true,
    )
  } else {
    removeTitle(rows, 'Experimental guard')
  }

  if ((moduleId === 'riot' || moduleId === 'epic') && opts.experimental) {
    upsert(
      rows,
      'Experimental yield',
      'Tighter launcher yield loop + launcher FSO re-stamp on Apply.',
      true,
    )
  } else {
    removeTitle(rows, 'Experimental yield')
  }

  if (moduleId === 'nvidia' && opts.experimental) {
    upsert(
      rows,
      'Experimental re-import',
      'Force DRS profile re-import even if already verified (SafePolicy retained).',
      true,
    )
  } else {
    removeTitle(rows, 'Experimental re-import')
  }

  return rows
}

export function statusDetailForSelection(
  moduleId: ModuleId,
  baseDetail: string | undefined,
  opts: Opts,
): string {
  const parts: string[] = []
  parts.push(opts.experimental ? 'Experimental apply' : 'Stable apply')
  if (moduleId === 'nvidia') parts.push(opts.useGsync ? 'G-SYNC / VRR' : 'raw latency')
  if (moduleId === 'internet')
    parts.push(opts.preferLowestLatency ? 'lowest latency stack' : 'high throughput stack')
  const sel = parts.join(' · ')
  if (!baseDetail || baseDetail === '—') return `${sel}.`
  return `${baseDetail} · Next: ${sel}.`
}

function experimentalDetail(id: ModuleId): string {
  switch (id) {
    case 'discord':
      return 'Experimental — force debloat + lean Equicord rebuild.'
    case 'steam':
      return 'Experimental — tighter memory/contention guard cadence.'
    case 'internet':
      return 'Experimental — force re-stamp of full host + NIC stack.'
    case 'nvidia':
      return 'Experimental — force DRS profile re-import (still SafePolicy).'
    case 'riot':
    case 'epic':
      return 'Experimental — tighter yield cadence + launcher FSO.'
    default:
      return 'Experimental — extra force paths on Apply.'
  }
}

function upsert(rows: FeatureRow[], title: string, detail: string, active: boolean) {
  const i = rows.findIndex((r) => r.title.toLowerCase() === title.toLowerCase())
  if (i >= 0) {
    rows[i] = { ...rows[i], title, detail, active }
  } else {
    // Put option-driven rows near the top so the toggle change is obvious.
    rows.unshift({ title, detail, active })
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

export function optionsFromStatus(s: ModuleStatus | null): Partial<Opts> {
  if (!s?.options) return {}
  return {
    experimental: s.options.experimental,
    useGsync: s.options.useGsync,
    preferLowestLatency: s.options.preferLowestLatency,
  }
}
