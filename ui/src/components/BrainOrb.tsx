import { useEffect, useRef } from 'react'

/**
 * Exo's brain — a Fibonacci dot-sphere where every dot is alive on its own.
 * Points are depth-shaded and drawn as real pixels so it's sharp at any size.
 *
 * The life comes from the DOTS, not from shoving the whole ball around:
 *   - every dot shimmers and twinkles on its own phase (per-dot breathing);
 *   - it throws expressive GESTURES with intent, not for the sake of moving —
 *     ripples roll across it, it pulses when it talks, bursts apart and
 *     reassembles, coalesces into a tight core, shivers when it's thinking hard;
 *   - it stays anchored near center with only a gentle float, so a move means
 *     something.
 *
 *   state 'idle'  — gentle float + self-directed glances; periodic gestures.
 *   state 'think' — leans in, a meridian scan sweep, quicker glances.
 *   state 'work'  — every dot pulses; energized.
 *   state 'speak' — a nod + a bright ring pulses out from the front.
 *
 * Reacts to the cursor with a subtle parallax tilt. Reduced-motion aware — the
 * gestures soften and slow, but it never goes still.
 */
export type BrainState = 'idle' | 'think' | 'work' | 'speak'

const N = 320
const TAU = Math.PI * 2
const GOLDEN = Math.PI * (3 - Math.sqrt(5))
const PTS: [number, number, number][] = Array.from({ length: N }, (_, i) => {
  const y = 1 - (i / (N - 1)) * 2
  const r = Math.sqrt(Math.max(0, 1 - y * y))
  const th = i * GOLDEN
  return [Math.cos(th) * r, y, Math.sin(th) * r]
})
// Stable per-dot phases so each point animates on its own clock.
const PH = Array.from({ length: N }, (_, i) => (Math.sin(i * 12.9898) * 43758.5453) % 1)
const PH2 = Array.from({ length: N }, (_, i) => (Math.sin(i * 78.233) * 12543.123) % 1)

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v))
// smooth 0 -> 1 -> 0 over a normalized age
const envSin = (x: number) => Math.sin(clamp(x, 0, 1) * Math.PI)

type GKind = 'ripple' | 'pulse' | 'burst' | 'coalesce' | 'shiver'
type Gesture = { kind: GKind; t0: number; dur: number }

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
    let R0 = 130
    const resize = () => {
      W = window.innerWidth
      H = window.innerHeight
      const dpr = Math.min(2, window.devicePixelRatio || 1)
      cv.width = Math.floor(W * dpr)
      cv.height = Math.floor(H * dpr)
      cv.style.width = W + 'px'
      cv.style.height = H + 'px'
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      R0 = clamp(Math.min(W, H) * 0.17, 96, 150)
    }
    resize()
    window.addEventListener('resize', resize)

    const onMove = (e: PointerEvent) => {
      ptr.current.tx = (e.clientX / window.innerWidth - 0.5) * 2
      ptr.current.ty = (e.clientY / window.innerHeight - 0.5) * 2
    }
    window.addEventListener('pointermove', onMove)

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    const calm = reduced ? 0.55 : 1

    let clock = 0
    let prev = performance.now()
    const gaze = { x: 0, y: 0, tx: 0, ty: 0 }
    let zoom = 1
    let nextGestureAt = prev + 1600
    let nextGazeAt = prev + 1100
    let nodT = -1e4
    let perkT = -1e4
    let gesture: Gesture | null = null
    let lastState = state

    const frame = (now: number) => {
      const st = stateRef.current
      const realDt = Math.min(0.05, (now - prev) / 1000)
      prev = now
      const energy = st === 'work' ? 1 : st === 'think' ? 0.72 : st === 'speak' ? 0.42 : 0.16
      clock += realDt * (0.5 + energy * 1.1) * calm
      const t = clock

      // State-change gestures — the orb reacts to what it's doing.
      if (st !== lastState) {
        if (st === 'speak') {
          gesture = { kind: 'pulse', t0: now, dur: 0.75 }
          nodT = now
          perkT = now
        } else if (st === 'work') {
          perkT = now
        }
        lastState = st
      }

      // Idle gestures with intent — mostly gentle, occasionally big. Never a
      // constant fidget: there are real gaps between them.
      if (st === 'idle' && now >= nextGestureAt) {
        const r = Math.random()
        const kind: GKind = r < 0.36 ? 'ripple' : r < 0.62 ? 'shiver' : r < 0.8 ? 'pulse' : r < 0.92 ? 'coalesce' : 'burst'
        const dur = kind === 'burst' ? 1.05 : kind === 'coalesce' ? 1.35 : kind === 'ripple' ? 1.7 : kind === 'shiver' ? 0.8 : 0.75
        gesture = { kind, t0: now, dur }
        if (kind === 'burst' || kind === 'pulse') perkT = now
        nextGestureAt = now + 2400 + Math.random() * 3400
      }
      if (gesture && (now - gesture.t0) / 1000 > gesture.dur) gesture = null

      // Self-directed glances.
      if (now >= nextGazeAt) {
        gaze.tx = (Math.random() * 2 - 1) * (st === 'work' ? 0.3 : 0.55)
        gaze.ty = (Math.random() * 2 - 1) * 0.24
        nextGazeAt = now + 1600 + Math.random() * 2600
      }
      const ge = reduced ? 0.02 : 0.035
      gaze.x += (gaze.tx - gaze.x) * ge
      gaze.y += (gaze.ty - gaze.y) * ge

      const p = ptr.current
      if (!reduced) {
        p.x += (p.tx - p.x) * 0.05
        p.y += (p.ty - p.y) * 0.05
      }

      const nodAge = (now - nodT) / 1000
      const nod = nodAge >= 0 && nodAge < 0.6 ? Math.sin((nodAge / 0.6) * Math.PI) * Math.exp(-nodAge * 3.2) * 0.24 : 0
      const perkAge = (now - perkT) / 1000
      const perk = perkAge >= 0 && perkAge < 0.55 ? 1 - perkAge / 0.55 : 0

      const zoomTarget = st === 'think' ? 1.06 : st === 'work' ? 1.03 : st === 'speak' ? 1.04 : 1.0
      zoom += (zoomTarget - zoom) * 0.06
      const zoomNow = zoom + perk * 0.05
      const breath = 1 + 0.02 * Math.sin(t * 1.4)
      const Rr = R0 * zoomNow * breath

      // Gentle float only — anchored near center-top so a real move reads as
      // intent, not restlessness.
      const drift = R0 * 0.12 * calm
      const cx = W * 0.5 + drift * Math.sin(t * 0.5)
      const cy = H * 0.4 + drift * 0.7 * Math.cos(t * 0.43)

      const ay = t * 0.3 + gaze.x + (reduced ? 0 : p.x * 0.42)
      const ax = 0.3 + gaze.y * 0.6 + (reduced ? 0 : p.y * 0.3) + nod
      const cay = Math.cos(ay)
      const say = Math.sin(ay)
      const cax = Math.cos(ax)
      const sax = Math.sin(ax)
      const scan = Math.sin(t * 1.4)
      const workPulse = 0.5 + 0.5 * Math.sin(t * 6)

      // Active gesture params, computed once.
      const g = gesture
      const gAge = g ? (now - g.t0) / 1000 : 0
      const gNorm = g ? clamp(gAge / g.dur, 0, 1) : 0
      const gE = g ? envSin(gNorm) : 0
      const ringPos = 1 - gNorm // pulse ring travels front -> back
      const twinkleGlow = perk * 0.16

      ctx.clearRect(0, 0, W, H)

      for (let i = 0; i < N; i++) {
        const [px0, py0, pz0] = PTS[i]

        // ---- per-dot radial displacement (this is what makes dots move) ----
        let disp = 0.012 * Math.sin(t * 1.8 + PH[i] * TAU) // always-on shimmer
        if (g) {
          if (g.kind === 'ripple') disp += 0.09 * gE * Math.sin(py0 * 3.2 - gAge * 13 + PH[i] * 0.7)
          else if (g.kind === 'shiver') disp += 0.055 * gE * Math.sin(t * 40 + PH[i] * 90)
          else if (g.kind === 'pulse') disp += 0.03 * gE
          else if (g.kind === 'burst') disp += gE * (0.5 + 0.85 * PH[i])
          else if (g.kind === 'coalesce') disp -= gE * 0.42 * (0.45 + 0.55 * PH2[i])
        }
        if (st === 'work') disp += 0.05 * workPulse * (0.5 + 0.5 * PH[i])
        const rad = Rr * (1 + disp)

        // rotate Y then X, scaled by the displaced radius
        const x1 = px0 * cay - pz0 * say
        const z1 = px0 * say + pz0 * cay
        const y2 = py0 * cax - z1 * sax
        const z2 = py0 * sax + z1 * cax
        const depth = (z2 + 1) / 2
        const sx = cx + x1 * rad
        const sy = cy + y2 * rad

        // ---- per-dot brightness / size ----
        let bright = 0.22 + depth * 0.78 + twinkleGlow * depth
        bright += 0.05 * Math.sin(t * 2.2 + PH[i] * TAU) // twinkle
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
        if (g && (g.kind === 'pulse' || g.kind === 'burst')) {
          const band = 1 - Math.min(1, Math.abs(depth - ringPos) / 0.16)
          if (band > 0) {
            bright += band * gE * 0.8
            dot += band * gE * 1.3
          }
          if (g.kind === 'burst') bright += gE * 0.18
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
