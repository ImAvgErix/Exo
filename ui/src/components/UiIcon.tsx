import {
  ArrowUpRight,
  ArrowsClockwise,
  Check,
  CircleNotch,
  Gear,
  Lightning,
  Minus,
  Network,
  Prohibit,
  Square,
  WindowsLogo,
  X,
  type IconProps,
} from '@phosphor-icons/react'
import type { ComponentType } from 'react'

/**
 * Every glyph in the shell, behind one component.
 *
 * Two families: Phosphor for symbols, and the simple-icons webfont for brand marks. They
 * are rendered differently — a React component versus an <i> with a font glyph — and
 * keeping both here means a caller never has to know which family a mark belongs to, or
 * that Windows is the odd one out (simple-icons ships no Microsoft or Windows mark, so it
 * is a Phosphor glyph while every other brand is a webfont one).
 */

export type UiIconName =
  | 'gear'
  | 'minimize'
  | 'maximize'
  | 'close'
  | 'windows'
  | 'network'
  | 'refresh'
  | 'lightning'
  | 'prohibit'
  | 'spinner'
  | 'check'
  | 'arrowUpRight'

const icons: Record<UiIconName, ComponentType<IconProps>> = {
  gear: Gear,
  minimize: Minus,
  maximize: Square,
  close: X,
  windows: WindowsLogo,
  network: Network,
  refresh: ArrowsClockwise,
  lightning: Lightning,
  prohibit: Prohibit,
  spinner: CircleNotch,
  check: Check,
  arrowUpRight: ArrowUpRight,
}

type SymbolProps = {
  name: UiIconName
  brand?: never
  size?: number
  className?: string
  color?: string
  weight?: IconProps['weight']
  /** Applied to the spinner so a run reads as moving rather than stuck. */
  spin?: boolean
}

type BrandProps = {
  brand: string
  name?: never
  size?: number
  className?: string
  color?: string
  weight?: never
  spin?: never
}

export function UiIcon(props: SymbolProps | BrandProps) {
  const { size = 16, className, color, spin } = props

  if (props.brand !== undefined) {
    return (
      <i
        className={`si si-${props.brand}${className ? ` ${className}` : ''}`}
        style={{ fontSize: size, color }}
        aria-hidden="true"
      />
    )
  }

  const Icon = icons[props.name]
  return (
    <Icon
      className={className}
      size={size}
      color={color}
      weight={props.weight ?? 'regular'}
      style={spin ? { animation: 'exo-spin 900ms linear infinite' } : undefined}
      aria-hidden="true"
      focusable="false"
    />
  )
}
