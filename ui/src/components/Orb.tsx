import { useEffect, useRef } from 'react'

/**
 * The "brain" — a living JARVIS-style orb. Breathes when idle; spins faster,
 * brightens and emits thinking-ripples when the app is working (Verify/Apply).
 * Tints to the focused system's accent. Canvas 2D, reduced-motion aware.
 */
export function Orb({ accent, active }: { accent: string; active: boolean }) {
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
    let w = 0
    let h = 0
    let raf = 0
    const t0 = performance.now()
    const ripples: { r: number }[] = []
    let lastRipple = 0

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
    }
    resize()
    const ro = new ResizeObserver(resize)
    ro.observe(cv)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    const frame = (now: number) => {
      const t = (now - t0) / 1000
      ctx.clearRect(0, 0, w, h)
      const [ar, ag, ab] = rgb(accentRef.current)
      const act = activeRef.current
      const acc = (a: number) => `rgba(${ar},${ag},${ab},${a})`
      const cx = w * 0.6
      const cy = h * 0.58
      const base = Math.min(w, h) * 0.28
      const breathe = 1 + 0.035 * Math.sin(t * (act ? 2.4 : 1.0))
      const R = base * breathe

      // outer glow
      let g = ctx.createRadialGradient(cx, cy, R * 0.2, cx, cy, R * 2.4)
      g.addColorStop(0, acc(act ? 0.22 : 0.13))
      g.addColorStop(1, acc(0))
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(cx, cy, R * 2.4, 0, Math.PI * 2)
      ctx.fill()

      // core sphere
      g = ctx.createRadialGradient(cx - R * 0.22, cy - R * 0.26, R * 0.05, cx, cy, R)
      g.addColorStop(0, `rgba(255,255,255,${act ? 0.5 : 0.34})`)
      g.addColorStop(0.34, acc(0.5))
      g.addColorStop(1, acc(0.02))
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(cx, cy, R, 0, Math.PI * 2)
      ctx.fill()

      // orbit rings + travelling nodes
      const spin = t * (act ? 0.9 : 0.32)
      for (let i = 0; i < 3; i++) {
        const rr = R * (1.24 + i * 0.28)
        const tilt = 0.4 + i * 0.14
        ctx.save()
        ctx.translate(cx, cy)
        ctx.rotate(spin * (i % 2 ? -1 : 1) + i * 1.1)
        ctx.scale(1, tilt)
        ctx.beginPath()
        ctx.arc(0, 0, rr, 0, Math.PI * 2)
        ctx.strokeStyle = acc(act ? 0.3 : 0.16)
        ctx.lineWidth = 1.3
        ctx.stroke()
        const a = spin * 1.6 + i * 2.1
        ctx.beginPath()
        ctx.arc(Math.cos(a) * rr, Math.sin(a) * rr, 2.3, 0, Math.PI * 2)
        ctx.fillStyle = 'rgba(255,255,255,0.85)'
        ctx.fill()
        ctx.restore()
      }

      // thinking ripples while working
      if (act && now - lastRipple > 850) {
        ripples.push({ r: R * 1.1 })
        lastRipple = now
      }
      for (let i = ripples.length - 1; i >= 0; i--) {
        const rp = ripples[i]
        rp.r += 1.7
        const a = Math.max(0, 0.4 * (1 - (rp.r - R * 1.1) / (R * 1.7)))
        if (a <= 0) {
          ripples.splice(i, 1)
          continue
        }
        ctx.beginPath()
        ctx.arc(cx, cy, rp.r, 0, Math.PI * 2)
        ctx.strokeStyle = acc(a)
        ctx.lineWidth = 1.1
        ctx.stroke()
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
      style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none', zIndex: 2 }}
    />
  )
}
