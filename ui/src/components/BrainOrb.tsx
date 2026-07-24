import { useEffect, useRef } from 'react'

/**
 * Exo's brain — a crisp Fibonacci dot-sphere that doesn't just sit there. It
 * lives in the whole app: it roams the space on its own, wanders wide when it's
 * idle, throws in the occasional playful spin or hop, and glides back up near
 * the center to talk to you. Points are depth-shaded and drawn as real pixels so
 * it stays sharp at any size. Monochrome.
 *
 *   state 'idle'  — free roaming: drifts across the room, self-directed glances,
 *                   the odd barrel-roll or little hop, as if it's just... alive.
 *   state 'think' — leans in (zoom), quicker glances, a meridian scan sweep.
 *   state 'work'  — energized: bigger drift, a working bob, a bright pulse.
 *   state 'speak' — glides up near center, nods, and pulses an outward ripple.
 *
 * Reacts to the cursor with a subtle parallax tilt. Reduced-motion aware — it
 * roams more gently and slower, but it never stops living.
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

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v))

export function BrainOrb({ state = 'idle' }: { state?: BrainState }) {
  const ref = useRef<HTMLCanvasElement | null>(null)
  const stateRef = useRef(state)
  stateRef.current = state
  const ptr = useRef({ x: 0, y: 0, tx: 0, ty: 0 })

  useEffect(() => {
    const cv = ref.current
    if (!cv) return
    const ctx = cv.getContext('2d')
    if (!ctx) return

    let W = 0
    let H = 0
    let R0 = 120 // sphere radius (recomputed on resize)
    const resize = () => {
      W = window.innerWidth
      H = window.innerHeight
      const dpr = Math.min(2, window.devicePixelRatio || 1)
      cv.width = Math.floor(W * dpr)
      cv.height = Math.floor(H * dpr)
      cv.style.width = W + 'px'
      cv.style.height = H + 'px'
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      R0 = clamp(Math.min(W, H) * 0.15, 84, 132)
    }
    resize()
    window.addEventListener('resize', resize)

    const onMove = (e: PointerEvent) => {
      ptr.current.tx = (e.clientX / window.innerWidth - 0.5) * 2
      ptr.current.ty = (e.clientY / window.innerHeight - 0.5) * 2
    }
    window.addEventListener('pointermove', onMove)

    // Gaming rigs often have Windows "animation effects" off (reports
    // prefers-reduced-motion). The orb is the whole app — it must stay alive.
    // Reduced = it roams gentler and slower, never frozen.
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const calm = reduced ? 0.55 : 1

    // Continuous energy-warped clock so speeding up / slowing down between states
    // never snaps the motion.
    let clock = 0
    let prev = performance.now()

    // Where the orb is roaming to (sphere center) — starts upper-center.
    const pos = { x: W / 2, y: H * 0.4, tx: W / 2, ty: H * 0.4 }
    // Self-directed gaze (which way it "looks").
    const gaze = { x: 0, y: 0, tx: 0, ty: 0 }
    let spinVel = 0 // playful barrel-roll impulse, decays
    let spinAccum = 0
    let zoom = 1
    let nextRoamAt = prev + 700
    let nextGazeAt = prev + 1100
    let pulseT = -1e4
    let nodT = -1e4
    let perkT = -1e4
    let hopT = -1e4
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
        if (st === 'speak') {
          nodT = now
          perkT = now
          nextRoamAt = Math.min(nextRoamAt, now) // glide toward the talk spot now
        }
        if (st === 'work') perkT = now
        lastState = st
      }

      // Eased lean toward the state, plus a spring from the curious perk.
      const perkAge = (now - perkT) / 1000
      const perk = perkAge >= 0 && perkAge < 0.55 ? 1 - perkAge / 0.55 : 0
      const zoomTarget = st === 'think' ? 1.06 : st === 'work' ? 1.03 : st === 'speak' ? 1.05 : 1.0
      zoom += (zoomTarget - zoom) * 0.06
      const zoomNow = zoom + perk * 0.06
      const R = R0 * zoomNow

      // Pick a new place to roam. Speaking -> settle up near center so the text
      // below stays readable. Idle -> wander the whole upper room, and sometimes
      // do something fun (a spin, a hop).
      if (now >= nextRoamAt) {
        if (st === 'speak') {
          pos.tx = W * 0.5 + (Math.random() * 2 - 1) * W * 0.05
          pos.ty = H * 0.4
          nextRoamAt = now + 800 + Math.random() * 900
        } else {
          const padX = R + 32
          const minY = R + 30
          const maxY = Math.max(minY + 24, H * 0.42)
          pos.tx = padX + Math.random() * Math.max(40, W - 2 * padX)
          pos.ty = minY + Math.random() * (maxY - minY)
          if (!reduced && st === 'idle') {
            const r = Math.random()
            if (r < 0.3) spinVel += (Math.random() < 0.5 ? -1 : 1) * (3 + Math.random() * 3.5)
            else if (r < 0.56) hopT = now
          }
          const cadence = st === 'idle' ? 1900 : 1300
          nextRoamAt = now + cadence + Math.random() * (st === 'idle' ? 2700 : 1200)
        }
      }
      const ease = st === 'speak' ? 0.055 : reduced ? 0.012 : 0.02
      pos.x += (pos.tx - pos.x) * ease
      pos.y += (pos.ty - pos.y) * ease

      // Self-directed glances.
      if (now >= nextGazeAt) {
        gaze.tx = (Math.random() * 2 - 1) * (st === 'work' ? 0.35 : 0.62)
        gaze.ty = (Math.random() * 2 - 1) * 0.26
        nextGazeAt = now + 1500 + Math.random() * 2600
      }
      const ge = reduced ? 0.02 : 0.035
      gaze.x += (gaze.tx - gaze.x) * ge
      gaze.y += (gaze.ty - gaze.y) * ge

      // Pointer parallax.
      const p = ptr.current
      if (!reduced) {
        p.x += (p.tx - p.x) * 0.05
        p.y += (p.ty - p.y) * 0.05
      }

      // Playful spin, decaying.
      spinVel *= 0.94
      spinAccum += spinVel * realDt

      // Envelopes.
      const nodAge = (now - nodT) / 1000
      const nod = nodAge >= 0 && nodAge < 0.6 ? Math.sin((nodAge / 0.6) * Math.PI) * Math.exp(-nodAge * 3.2) * 0.24 : 0
      const hopAge = (now - hopT) / 1000
      const hop = hopAge >= 0 && hopAge < 0.7 ? Math.sin((hopAge / 0.7) * Math.PI) * Math.exp(-hopAge * 2.4) : 0
      const pulseAge = (now - pulseT) / 1000
      const ripple = pulseAge >= 0 && pulseAge < 0.8 ? 1 - pulseAge / 0.8 : 0

      // Micro-drift so even between roams it's never dead still; hop lifts it.
      const driftA = R0 * 0.05 * calm
      const cx = pos.x + driftA * Math.sin(t * 0.9)
      const cy = pos.y + driftA * Math.cos(t * 0.75) - hop * R0 * 0.6

      ctx.clearRect(0, 0, W, H)
      const breath = 1 + 0.02 * Math.sin(t * 1.4)
      const Rr = R * breath
      const ay = t * 0.32 + spinAccum + gaze.x + (reduced ? 0 : p.x * 0.45)
      const ax = 0.3 + gaze.y * 0.6 + (reduced ? 0 : p.y * 0.32) + nod
      const cay = Math.cos(ay)
      const say = Math.sin(ay)
      const cax = Math.cos(ax)
      const sax = Math.sin(ax)
      const scan = Math.sin(t * 1.4)
      const workPulse = 0.5 + 0.5 * Math.sin(t * 6)
      const glow = perk * 0.16

      for (let i = 0; i < N; i++) {
        const [px0, py0, pz0] = PTS[i]
        const x1 = px0 * cay - pz0 * say
        const z1 = px0 * say + pz0 * cay
        const y2 = py0 * cax - z1 * sax
        const z2 = py0 * sax + z1 * cax
        const depth = (z2 + 1) / 2
        const sx = cx + x1 * Rr
        const sy = cy + y2 * Rr

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
    let raf = requestAnimationFrame(frame)
    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('resize', resize)
      window.removeEventListener('pointermove', onMove)
    }
  }, [])

  return (
    <canvas
      ref={ref}
      aria-hidden
      style={{ position: 'fixed', inset: 0, width: '100%', height: '100%', display: 'block', pointerEvents: 'none', filter: 'drop-shadow(0 0 34px rgba(255,255,255,0.09))' }}
    />
  )
}
