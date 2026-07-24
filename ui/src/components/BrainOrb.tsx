import { useEffect, useRef } from 'react'

/**
 * Exo's own crisp dot-sphere "brain". Points on a Fibonacci sphere, rotated and
 * depth-shaded, drawn as real pixels so it's sharp at any size. Monochrome.
 *   state 'idle'  — slow breathing rotation (the brain at rest)
 *   state 'think' — faster spin + a meridian scan sweep (reading the PC)
 *   state 'work'  — energized pulse (applying)
 *   state 'speak' — a gentle outward ripple pulse (the brain talking)
 * Reacts to the cursor with a subtle parallax tilt. Reduced-motion aware.
 */
export type BrainState = 'idle' | 'think' | 'work' | 'speak'

const N = 320
const GOLDEN = Math.PI * (3 - Math.sqrt(5))
const PTS: [number, number, number][] = Array.from({ length: N }, (_, i) => {
  const y = 1 - (i / (N - 1)) * 2
  const r = Math.sqrt(Math.max(0, 1 - y * y))
  const th = i * GOLDEN
  return [Math.cos(th) * r, y, Math.sin(th) * r]
})

export function BrainOrb({ state = 'idle', size = 320 }: { state?: BrainState; size?: number }) {
  const ref = useRef<HTMLCanvasElement | null>(null)
  const stateRef = useRef(state)
  stateRef.current = state
  const ptr = useRef({ x: 0, y: 0, tx: 0, ty: 0 })

  useEffect(() => {
    const cv = ref.current
    if (!cv) return
    const ctx = cv.getContext('2d')
    if (!ctx) return
    const dpr = Math.min(2, window.devicePixelRatio || 1)
    let raf = 0
    const t0 = performance.now()
    let pulseT = -10

    const px = size
    cv.width = Math.floor(px * dpr)
    cv.height = Math.floor(px * dpr)
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)

    const onMove = (e: PointerEvent) => {
      ptr.current.tx = (e.clientX / window.innerWidth - 0.5) * 2
      ptr.current.ty = (e.clientY / window.innerHeight - 0.5) * 2
    }
    window.addEventListener('pointermove', onMove)
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    let lastState = state

    const frame = (now: number) => {
      const st = stateRef.current
      if (st !== lastState && (st === 'speak' || st === 'work')) pulseT = now
      lastState = st
      const energy = st === 'work' ? 1 : st === 'think' ? 0.7 : st === 'speak' ? 0.4 : 0.15
      const t = ((now - t0) / 1000) * (0.5 + energy * 1.1)
      const p = ptr.current
      p.x += (p.tx - p.x) * 0.05
      p.y += (p.ty - p.y) * 0.05

      ctx.clearRect(0, 0, px, px)
      const cx = px / 2
      const cy = px / 2
      const R = px * 0.4 * (1 + 0.02 * Math.sin(t * 1.4))
      const ay = t * 0.5 + p.x * 0.5
      const ax = 0.32 + p.y * 0.35
      const cay = Math.cos(ay)
      const say = Math.sin(ay)
      const cax = Math.cos(ax)
      const sax = Math.sin(ax)
      const scan = Math.sin(t * 1.4) // meridian sweep position (-1..1) for think
      const pulseAge = (now - pulseT) / 1000
      const ripple = pulseAge >= 0 && pulseAge < 0.8 ? 1 - pulseAge / 0.8 : 0
      const workPulse = 0.5 + 0.5 * Math.sin(t * 6)

      for (let i = 0; i < N; i++) {
        const [px0, py0, pz0] = PTS[i]
        // rotate Y then X
        const x1 = px0 * cay - pz0 * say
        const z1 = px0 * say + pz0 * cay
        const y2 = py0 * cax - z1 * sax
        const z2 = py0 * sax + z1 * cax
        const depth = (z2 + 1) / 2 // 0 back .. 1 front
        const sx = cx + x1 * R
        const sy = cy + y2 * R

        let bright = 0.22 + depth * 0.78
        let dot = 0.7 + depth * 1.7
        if (st === 'think') {
          const near = 1 - Math.min(1, Math.abs(x1 - scan) / 0.22)
          if (near > 0) {
            bright = Math.min(1, bright + near * 0.9)
            dot += near * 1.6
          }
        } else if (st === 'work') {
          bright = Math.min(1, bright + workPulse * 0.4 * depth)
          dot += workPulse * 0.8 * depth
        }
        if (ripple > 0) {
          const band = 1 - Math.min(1, Math.abs(depth - (1 - ripple)) / 0.14)
          if (band > 0) {
            bright = Math.min(1, bright + band * ripple * 0.8)
            dot += band * ripple * 1.4
          }
        }
        ctx.beginPath()
        ctx.arc(sx, sy, Math.max(0.4, dot), 0, Math.PI * 2)
        ctx.fillStyle = `rgba(240,242,248,${bright})`
        ctx.fill()
      }

      if (!reduced) raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('pointermove', onMove)
    }
  }, [size])

  return (
    <canvas
      ref={ref}
      aria-hidden
      style={{ width: size, height: size, display: 'block', filter: 'drop-shadow(0 0 34px rgba(255,255,255,0.10))' }}
    />
  )
}
