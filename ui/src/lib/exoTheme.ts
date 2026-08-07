import type { TextColour, TextSize } from './host'

/**
 * The two knobs from Settings, painted as CSS custom properties.
 *
 * The host is the source of truth and persists them, but `settings.get` is an async trip
 * over the WebView2 bridge — so the last known pair is mirrored into localStorage and read
 * back synchronously at module load. Without that, every launch paints one frame of default
 * white at scale 1 before the host answers, which reads as a flash on a grey/large setup.
 */

const STORAGE_KEY = 'exo.textTheme'

export const TEXT_COLOURS: Record<TextColour, string> = {
  white: '#ffffff',
  grey: '#a8a8ae',
}

export const TEXT_SCALES: Record<TextSize, number> = {
  small: 0.9,
  normal: 1,
  large: 1.14,
}

export type TextTheme = { textColour: TextColour; textSize: TextSize }

export const DEFAULT_TEXT_THEME: TextTheme = { textColour: 'white', textSize: 'normal' }

function isColour(value: unknown): value is TextColour {
  return value === 'white' || value === 'grey'
}

function isSize(value: unknown): value is TextSize {
  return value === 'small' || value === 'normal' || value === 'large'
}

/** Never throws: a blocked or full localStorage must not stop the app from starting. */
export function readCachedTextTheme(): TextTheme {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return DEFAULT_TEXT_THEME
    const parsed = JSON.parse(raw) as Partial<TextTheme>
    return {
      textColour: isColour(parsed.textColour) ? parsed.textColour : DEFAULT_TEXT_THEME.textColour,
      textSize: isSize(parsed.textSize) ? parsed.textSize : DEFAULT_TEXT_THEME.textSize,
    }
  } catch {
    return DEFAULT_TEXT_THEME
  }
}

export function applyTextTheme(theme: TextTheme) {
  const root = document.documentElement
  const ink = TEXT_COLOURS[theme.textColour] ?? TEXT_COLOURS.white
  const scale = TEXT_SCALES[theme.textSize] ?? 1
  // Legacy tokens (older shell CSS).
  root.style.setProperty('--exo-text', ink)
  root.style.setProperty('--exo-scale', String(scale))
  // AMOLED Tailwind @theme tokens — ExoApp reads text-fg / text-muted / text-faint.
  root.style.setProperty('--color-fg', ink)
  if (theme.textColour === 'grey') {
    root.style.setProperty('--color-muted', '#7a7a80')
    root.style.setProperty('--color-faint', '#4a4a50')
  } else {
    root.style.setProperty('--color-muted', '#8a8a8a')
    root.style.setProperty('--color-faint', '#555555')
  }
  // Text size only — layout/icons stay fixed; type scales via CSS calc on text utilities.
  root.style.setProperty('--exo-text-scale', String(scale))
  // Drop legacy full-UI zoom if an older build left it inline.
  root.style.removeProperty('--exo-ui-zoom')
  root.style.zoom = '1'
  root.dataset.exoTextColour = theme.textColour
  root.dataset.exoTextSize = theme.textSize
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(theme))
  } catch {
    // A theme that cannot be cached still applies; it just costs a flash next launch.
  }
}

/** Normalises whatever the host sent, falling back to the cached pair per field. */
export function textThemeFrom(
  settings: { textColour?: string; textSize?: string } | null | undefined,
  fallback: TextTheme = DEFAULT_TEXT_THEME,
): TextTheme {
  return {
    textColour: isColour(settings?.textColour) ? settings.textColour : fallback.textColour,
    textSize: isSize(settings?.textSize) ? settings.textSize : fallback.textSize,
  }
}
