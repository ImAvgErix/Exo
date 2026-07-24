import { useEffect, useRef } from 'react'

/**
 * Monochrome "thinking" orb — the whole app is this. A grayscale sphere with
 * slow organic internal turbulence, a soft white fresnel rim, a fine grain
 * texture, and a gentle breathe. `energy` (0..1) drives how awake it is:
 * calm and slow near 0, agitated/brighter/faster near 1 (Verify/Apply, or a
 * system that needs attention). Black-and-white only. Canvas 2D, cheap.
 */
export function OrbMono({ energy = 0.12 }: { energy?: number }) {
  const ref = useRef<HTMLCanvasElement | null>(null)
  const energyRef = useRef(energy)
  energyRef.current = energy
  const pointer = useRef({ x: 0.5, y: 0.5, tx: 0.5, ty: 0.5 })

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

    // pre-rendered grain tile
    const grain = document.createElement('canvas')
    grain.width = grain.height = 160
    const gctx = grain.getContext('2d')!
    const img = gctx.createImageData(160, 160)
    for (let i = 0; i < img.data.length; i += 4) {
      const v = 120 + Math.floor(Math.random() * 135)
      img.data[i] = img.data[i + 1] = img.data[i + 2] = v
      img.data[i + 3] = 255
    }
    gctx.putImageData(img, 0, 0)
    const grainPat = ctx.createPattern(grain, 'repeat')

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

    const onMove = (e: PointerEvent) => {
      const r = cv.getBoundingClientRect()
      pointer.current.tx = (e.clientX - r.left) / r.width
      pointer.current.ty = (e.clientY - r.top) / r.height
    }
    window.addEventListener('pointermove', onMove)

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    const frame = (now: number) => {
      const en = energyRef.current
      const t = ((now - t0) / 1000) * (0.5 + en * 1.6)
      const p = pointer.current
      p.x += (p.tx - p.x) * 0.05
      p.y += (p.ty - p.y) * 0.05

      ctx.clearRect(0, 0, w, h)
      const cx = w / 2
      const cy = h / 2
      const R = Math.min(w, h) * 0.3 * (1 + 0.03 * Math.sin(t * 1.2))
      const parX = (p.x - 0.5) * R * 0.12
      const parY = (p.y - 0.5) * R * 0.12

      // outer glow
      let g = ctx.createRadialGradient(cx, cy, R * 0.6, cx, cy, R * 2.1)
      g.addColorStop(0, `rgba(255,255,255,${0.05 + en * 0.09})`)
      g.addColorStop(1, 'rgba(255,255,255,0)')
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(cx, cy, R * 2.1, 0, Math.PI * 2)
      ctx.fill()

      ctx.save()
      ctx.beginPath()
      ctx.arc(cx, cy, R, 0, Math.PI * 2)
      ctx.clip()

      // base
      ctx.fillStyle = '#0c0c0d'
      ctx.fillRect(cx - R, cy - R, R * 2, R * 2)

      // internal turbulence: drifting grayscale blobs, additive
      ctx.globalCompositeOperation = 'lighter'
      const blobs = 5
      for (let i = 0; i < blobs; i++) {
        const a = t * (0.35 + i * 0.06) + i * 1.7
        const rad = R * (0.34 + 0.16 * Math.sin(t * 0.7 + i))
        const bx = cx + parX + Math.cos(a) * R * (0.32 + 0.1 * Math.sin(t * 0.5 + i * 2))
        const by = cy + parY + Math.sin(a * 1.1) * R * (0.3 + 0.1 * Math.cos(t * 0.6 + i))
        const bl = 0.1 + 0.14 * (0.5 + 0.5 * Math.sin(t + i * 2)) + en * 0.12
        g = ctx.createRadialGradient(bx, by, 0, bx, by, rad)
        g.addColorStop(0, `rgba(235,238,245,${bl})`)
        g.addColorStop(1, 'rgba(235,238,245,0)')
        ctx.fillStyle = g
        ctx.beginPath()
        ctx.arc(bx, by, rad, 0, Math.PI * 2)
        ctx.fill()
      }
      ctx.globalCompositeOperation = 'source-over'

      // grain
      if (grainPat) {
        ctx.globalAlpha = 0.05
        ctx.fillStyle = grainPat
        ctx.fillRect(cx - R, cy - R, R * 2, R * 2)
        ctx.globalAlpha = 1
      }

      // spherical shading — dark bottom-right falloff
      g = ctx.createRadialGradient(cx - R * 0.3, cy - R * 0.34, R * 0.1, cx, cy, R)
      g.addColorStop(0, 'rgba(255,255,255,0)')
      g.addColorStop(0.72, 'rgba(0,0,0,0)')
      g.addColorStop(1, 'rgba(0,0,0,0.55)')
      ctx.fillStyle = g
      ctx.fillRect(cx - R, cy - R, R * 2, R * 2)

      ctx.restore()

      // fresnel rim
      g = ctx.createRadialGradient(cx, cy, R * 0.86, cx, cy, R * 1.02)
      g.addColorStop(0, 'rgba(255,255,255,0)')
      g.addColorStop(0.86, `rgba(255,255,255,${0.1 + en * 0.14})`)
      g.addColorStop(1, 'rgba(255,255,255,0)')
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(cx, cy, R * 1.02, 0, Math.PI * 2)
      ctx.fill()

      if (!reduced) raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)

    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
      window.removeEventListener('pointermove', onMove)
    }
  }, [])

  return (
    <canvas
      ref={ref}
      aria-hidden
      style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none' }}
    />
  )
}
