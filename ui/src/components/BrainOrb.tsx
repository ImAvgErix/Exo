import { useEffect, useRef } from 'react'

/**
 * Exo's own crisp dot-sphere "brain". Points on a Fibonacci sphere, rotated and
 * depth-shaded, drawn as real pixels so it's sharp at any size. Monochrome.
 *
 * It is meant to feel ALIVE — not a spinning logo. Even at rest it drifts on its
 * own, glances around on its own, and reacts with little gestures:
 *   state 'idle'  — slow breathing float; self-directed glances; the occasional
 *                   curious "perk" (a brief lean-in + brighten), as if it noticed
 *                   something on its own.
 *   state 'think' — leans in (zoom), quicker glances, a meridian scan sweep.
 *   state 'work'  — energized: bigger drift, a working bob, a bright pulse.
 *   state 'speak' — a nod + an outward ripple, like it's talking to you.
 * Reacts to the cursor with a subtle parallax tilt. Reduced-motion aware —
 * calmer and slower, but never frozen (the orb is the whole app).
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

    const px = size
    cv.width = Math.floor(px * dpr)
    cv.height = Math.floor(px * dpr)
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)

    const onMove = (e: PointerEvent) => {
      ptr.current.tx = (e.clientX / window.innerWidth - 0.5) * 2
      ptr.current.ty = (e.clientY / window.innerHeight - 0.5) * 2
    }
    window.addEventListener('pointermove', onMove)

    // Gaming rigs often have Windows "animation effects" off, which reports
    // prefers-reduced-motion. The orb is the whole app — it must stay alive.
    // Reduced motion = calm mode (slower, smaller, no parallax), never frozen.
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const calm = reduced ? 0.5 : 1

    // Continuous, energy-warped clock so speeding up / slowing down between
    // states never snaps the rotation (the old code recomputed t from a shared
    // origin with a different multiplier, which jumped on every state change).
    let clock = 0
    let prev = performance.now()

    // Impulse timers (ms). Envelopes decay off these.
    let pulseT = -1e4 // outward ripple (speak/work)
    let nodT = -1e4 // pitch nod (speak)
    let perkT = -1e4 // curious lean-in + brighten (idle/on-change)

    // Self-directed gaze: the orb picks a new place to "look" every so often.
    const gaze = { x: 0, y: 0, tx: 0, ty: 0 }
    let nextBehaviorAt = prev + 1200
    let zoom = 1
    let lastState = state

    const frame = (now: number) => {
      const st = stateRef.current
      const realDt = Math.min(0.05, (now - prev) / 1000)
      prev = now
      const energy = st === 'work' ? 1 : st === 'think' ? 0.72 : st === 'speak' ? 0.42 : 0.16
      clock += realDt * (0.5 + energy * 1.1) * calm
      const t = clock

      // State-change gestures.
      if (st !== lastState) {
        if (!reduced && (st === 'speak' || st === 'work')) pulseT = now
        if (st === 'speak') nodT = now
        if (st === 'speak' || st === 'work') perkT = now
        lastState = st
      }

      // Autonomous behavior: glance somewhere new; at rest, sometimes perk up
      // on its own so it reads as curious rather than idle.
      if (now >= nextBehaviorAt) {
        gaze.tx = (Math.random() * 2 - 1) * (st === 'work' ? 0.35 : 0.62)
        gaze.ty = (Math.random() * 2 - 1) * 0.26
        if (!reduced && st === 'idle' && Math.random() < 0.5) perkT = now
        const cadence = st === 'idle' ? 2600 : 1400
        nextBehaviorAt = now + cadence + Math.random() * 2800
      }
      const ge = reduced ? 0.02 : 0.035
      gaze.x += (gaze.tx - gaze.x) * ge
      gaze.y += (gaze.ty - gaze.y) * ge

      // Pointer parallax (skipped in reduced mode).
      const p = ptr.current
      if (!reduced) {
        p.x += (p.tx - p.x) * 0.05
        p.y += (p.ty - p.y) * 0.05
      }

      // Envelopes.
      const nodAge = (now - nodT) / 1000
      const nod = nodAge >= 0 && nodAge < 0.6 ? Math.sin((nodAge / 0.6) * Math.PI) * Math.exp(-nodAge * 3.2) * 0.24 : 0
      const perkAge = (now - perkT) / 1000
      const perk = perkAge >= 0 && perkAge < 0.55 ? 1 - perkAge / 0.55 : 0
      const pulseAge = (now - pulseT) / 1000
      const ripple = pulseAge >= 0 && pulseAge < 0.8 ? 1 - pulseAge / 0.8 : 0

      // Eased lean toward the state, plus a spring from the curious perk.
      const zoomTarget = st === 'think' ? 1.06 : st === 'work' ? 1.03 : st === 'speak' ? 1.02 : 1.0
      zoom += (zoomTarget - zoom) * 0.06
      const zoomNow = zoom + perk * 0.05

      ctx.clearRect(0, 0, px, px)

      // Organic float — a layered drift so it never traces an obvious circle —
      // plus a working bob when applying.
      const wa = px * 0.026 * calm * (st === 'work' ? 1.4 : 1)
      const wx = wa * (Math.sin(t * 0.53) + 0.5 * Math.sin(t * 0.31 + 1.3))
      const wy =
        wa * (Math.cos(t * 0.47) + 0.5 * Math.sin(t * 0.37 + 0.7)) +
        (st === 'work' ? px * 0.018 * Math.sin(t * 5) : 0)

      const cx = px / 2 + wx
      const cy = px / 2 + wy
      const R = px * 0.4 * zoomNow * (1 + 0.02 * Math.sin(t * 1.4))
      const ay = t * 0.35 + gaze.x + (reduced ? 0 : p.x * 0.45)
      const ax = 0.3 + gaze.y * 0.6 + (reduced ? 0 : p.y * 0.32) + nod
      const cay = Math.cos(ay)
      const say = Math.sin(ay)
      const cax = Math.cos(ax)
      const sax = Math.sin(ax)
      const scan = Math.sin(t * 1.4) // meridian sweep position (-1..1) for think
      const workPulse = 0.5 + 0.5 * Math.sin(t * 6)
      const glow = perk * 0.16

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

        let bright = 0.22 + depth * 0.78 + glow * depth
        let dot = 0.7 + depth * 1.7
        if (st === 'think') {
          const near = 1 - Math.min(1, Math.abs(x1 - scan) / 0.22)
          if (near > 0) {
            bright += near * 0.9
            dot += near * 1.6
          }
        } else if (st === 'work') {
          bright += workPulse * 0.4 * depth
          dot += workPulse * 0.8 * depth
        }
        if (ripple > 0) {
          const band = 1 - Math.min(1, Math.abs(depth - (1 - ripple)) / 0.14)
          if (band > 0) {
            bright += band * ripple * 0.8
            dot += band * ripple * 1.4
          }
        }
        ctx.beginPath()
        ctx.arc(sx, sy, Math.max(0.4, dot), 0, Math.PI * 2)
        ctx.fillStyle = `rgba(240,242,248,${Math.min(1, bright)})`
        ctx.fill()
      }

      raf = requestAnimationFrame(frame)
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
