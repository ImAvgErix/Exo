import type { ModuleId, ModuleState } from './host'
import amdLogo from '../assets/logos/amd.svg'
import braveLogo from '../assets/logos/brave.svg'
import discordLogo from '../assets/logos/discord.svg'
import nvidiaLogo from '../assets/logos/nvidia.png'
import spotifyLogo from '../assets/logos/spotify.svg'
import steamLogo from '../assets/logos/steam.png'
import windowsLogo from '../assets/logos/windows.svg'

/**
 * The eight rows of the shell, in fixed order.
 *
 * One row per optimizer, and every optimizer has a row — there is no module the engine
 * supports that this list omits.
 */

export type BrandMark =
  /** Official full-colour brand artwork. */
  | { kind: 'brand'; src: string }
  /** Phosphor glyph, for the rows that are not a brand at all. */
  | { kind: 'symbol'; name: SymbolName }

export type SymbolName = 'windows' | 'network'

export type ModuleRow = {
  id: ModuleId
  label: string
  mark: BrandMark
  /** Brand colour, or the ink token when the official mark is black (Steam). */
  color: string
  /** Present only for modules with a real choice; pressing opens the profile sheet. */
  options?: ReadonlyArray<readonly [label: string, hint: string]>
  /** Turns the chosen index into the params host.apply() expects. */
  applyOptions?: (pick: number) => Record<string, unknown>
  /**
   * Why this row can read as not installed, when "<Name> is not installed" would leave the
   * user arguing with the screen. Only needed where the module's scope is narrower than its
   * name suggests.
   */
  missingHint?: string
}

export const MODULES: readonly ModuleRow[] = [
  {
    id: 'nvidia',
    label: 'NVIDIA',
    mark: { kind: 'brand', src: nvidiaLogo },
    color: '#76b900',
    options: [
      ['G-SYNC / VRR', 'Cap just under your refresh rate and let the panel sync. Smoothest.'],
      ['Raw latency', 'No cap, no sync. Lowest input lag, some tearing.'],
    ],
    // The pack is a preference the machine cannot infer: G-SYNC caps frames just under the
    // refresh rate, raw latency leaves them uncapped. Sent explicitly, never defaulted.
    applyOptions: (pick) => ({ useGsync: pick === 0 }),
  },
  {
    id: 'amd',
    // Covers both halves of AMD: Radeon Software debloat when a Radeon adapter is present,
    // and the chipset driver reported for information when the CPU is a Ryzen. A Ryzen owner
    // with an NVIDIA card used to see NOT INSTALLED, which is not true of their machine.
    label: 'AMD',
    mark: { kind: 'brand', src: amdLogo },
    color: '#ed1c24',
    missingHint:
      'No AMD hardware found — no Radeon adapter and no Ryzen CPU — so there is nothing here to tune.',
  },
  {
    id: 'system',
    label: 'Windows',
    mark: { kind: 'brand', src: windowsLogo },
    color: '#0078d4',
  },
  {
    id: 'internet',
    label: 'Internet',
    mark: { kind: 'symbol', name: 'network' },
    color: 'var(--exo-text)',
    options: [
      ['Lowest latency', 'Strip offloads and coalescing. Best for ranked play.'],
      ['High throughput', 'Keep offloads on. Better for large downloads.'],
    ],
    applyOptions: (pick) => ({ preferLowestLatency: pick === 0 }),
  },
  {
    id: 'steam',
    label: 'Steam',
    // The official Steam mark is black; si--color would paint it invisible on this canvas.
    mark: { kind: 'brand', src: steamLogo },
    color: 'var(--exo-text)',
  },
  {
    id: 'discord',
    label: 'Discord',
    mark: { kind: 'brand', src: discordLogo },
    color: '#5865f2',
  },
  {
    id: 'spotify',
    label: 'Spotify',
    mark: { kind: 'brand', src: spotifyLogo },
    color: '#1db954',
  },
  {
    id: 'brave',
    label: 'Brave',
    mark: { kind: 'brand', src: braveLogo },
    color: '#fb542b',
  },
] as const

type StatePresentation = {
  word: string
  /** Word colour; the ink token means "follow --exo-text". */
  color: string
  dot: string
  label: string
  filled: boolean
}

export const STATE_PRESENTATION: Record<ModuleState, StatePresentation> = {
  applied: { word: 'ON', color: 'var(--exo-text)', dot: 'var(--exo-text)', label: 'REAPPLY', filled: false },
  ready: { word: 'READY', color: 'var(--exo-secondary)', dot: 'var(--exo-dot-ready)', label: 'APPLY', filled: true },
  blocked: { word: 'STUCK', color: 'var(--exo-amber)', dot: 'var(--exo-amber)', label: 'RETRY', filled: false },
  missing: { word: 'NOT INSTALLED', color: 'var(--exo-muted)', dot: 'var(--exo-dot-missing)', label: 'NOT INSTALLED', filled: false },
}

export const BLOCKED_REASON =
  'Windows has a restart pending. Exo resumes AMD from the blocked step afterwards.'

export function tooltipFor(row: ModuleRow, state: ModuleState, pick: number): string {
  if (state === 'missing') {
    return row.missingHint ?? `${row.label} is not installed, so there is nothing to apply`
  }
  if (state === 'blocked') return BLOCKED_REASON
  if (row.options) return `Choose a profile, then apply — currently ${row.options[pick]?.[0] ?? row.options[0][0]}`
  const word = STATE_PRESENTATION[state].label
  return `${word.charAt(0)}${word.slice(1).toLowerCase()} ${row.label}`
}
