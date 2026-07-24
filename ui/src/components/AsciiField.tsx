import { useEffect, useRef } from 'react'

/**
 * Ambient ASCII/dot-matrix field on a canvas — the "screen" behind the UI.
 * Mostly faint gray glyphs; a few tint to the focused system's accent. When
 * `active` (Verify / Apply running), an accent scan-line sweeps through.
 * Cheap: only cells above a brightness threshold are drawn; honors reduced motion.
 */
export function AsciiField({ accent, active }: { accent: string; active: boolean }) {
  const ref = useRef<HTMLCanvasElement | null>(null)
  const accentRef = useRef(accent)
  const activeRef = useRef(active)
  accentRef.current = accent
  activeRef.current = active

  useEffect(() => {
    const cv = ref.current
    if (!cv) return
    const ctx = cv.getContext('2d')
    if (!ctx) return
    const dpr = Math.min(2, window.devicePixelRatio || 1)
    const cell = 22
    const chars = '·:+×∙'.split('')
    let w = 0
    let h = 0
    let raf = 0
    const t0 = performance.now()

    const hash = (x: number, y: number) => {
      const v = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453
      return v - Math.floor(v)
    }
    const rgb = (hex: string) => {
      const s = hex.replace('#', '')
      return [parseInt(s.slice(0, 2), 16), parseInt(s.slice(2, 4), 16), parseInt(s.slice(4, 6), 16)]
    }

    const resize = () => {
      const r = cv.getBoundingClientRect()
      w = r.width
      h = r.height
      cv.width = Math.max(1, Math.floor(w * dpr))
      cv.height = Math.max(1, Math.floor(h * dpr))
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      ctx.font = '12px "JetBrains Mono", ui-monospace, "Cascadia Mono", monospace'
      ctx.textBaseline = 'middle'
    }
    resize()
    const ro = new ResizeObserver(resize)
    ro.observe(cv)

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    const frame = (now: number) => {
      const t = (now - t0) / 1000
      ctx.clearRect(0, 0, w, h)
      const cols = Math.ceil(w / cell)
      const rows = Math.ceil(h / cell)
      const [ar, ag, ab] = rgb(accentRef.current)
      const act = activeRef.current
      const scanY = act ? ((t * 0.5) % 1.15) * h : -9999
      for (let cy = 0; cy < rows; cy++) {
        const y = cy * cell + cell / 2
        const near = act ? Math.max(0, 1 - Math.abs(cy * cell - scanY) / 64) : 0
        for (let cx = 0; cx < cols; cx++) {
          const hx = hash(cx, cy)
          const b = 0.5 + 0.5 * Math.sin(t * 0.55 + cx * 0.34 + cy * 0.27 + hx * 6.283)
          const bright = b * 0.15 + near * 0.65
          if (bright < 0.13) continue
          const useAccent = near > 0.12 || hx > 0.87
          const alpha = Math.min(0.55, bright)
          ctx.fillStyle = useAccent
            ? `rgba(${ar},${ag},${ab},${alpha})`
            : `rgba(150,155,170,${alpha * 0.42})`
          ctx.fillText(chars[Math.floor(hx * chars.length) % chars.length], cx * cell + 4, y)
        }
      }
      if (!reduced || act) raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)

    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
    }
  }, [])

  return (
    <canvas
      ref={ref}
      aria-hidden
      style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none', zIndex: 1 }}
    />
  )
}
