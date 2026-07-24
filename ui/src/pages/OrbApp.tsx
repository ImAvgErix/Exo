import { useCallback, useEffect, useRef, useState } from 'react'
import { BrainOrb, type BrainState } from '../components/BrainOrb'
import { host, type DashboardSnapshot, type ModuleId } from '../lib/host'

/* Exo is a brain. The orb is the whole UI: it reads the PC and *asks* what to
   optimize; you answer; it works. Monochrome. No module grid — the brain leads. */

const LABEL: Record<string, string> = {
  discord: 'Discord', brave: 'Brave', steam: 'Steam', internet: 'Internet', nvidia: 'NVIDIA', games: 'Games',
}
const ORDER = ['nvidia', 'internet', 'steam', 'discord', 'brave', 'games'] as const

type Opt = { label: string; run: () => void; primary?: boolean }

export function OrbApp() {
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [phase, setPhase] = useState<'greet' | 'scanning' | 'ask' | 'working' | 'done'>('greet')
  const [msg, setMsg] = useState('')
  const [opts, setOpts] = useState<Opt[]>([])
  const [msgKey, setMsgKey] = useState(0)
  const [speaking, setSpeaking] = useState(false)
  const queue = useRef<string[]>([])
  const qi = useRef(0)
  const speakTimer = useRef<number | undefined>(undefined)

  const say = useCallback((message: string, options: Opt[]) => {
    setMsg(message)
    setOpts(options)
    setMsgKey((k) => k + 1)
    setSpeaking(true)
    window.clearTimeout(speakTimer.current)
    speakTimer.current = window.setTimeout(() => setSpeaking(false), 850)
  }, [])

  // bootstrap + telemetry
  useEffect(() => {
    let stop = false
    ;(async () => {
      try {
        const d = await host.getDashboard()
        if (!stop) setDash(d)
      } catch { /* skeleton */ }
    })()
    const poll = window.setInterval(async () => {
      try {
        const l = await host.getLive()
        if (!stop) setDash((d) => (d ? { ...d, live: l } : d))
      } catch { /* tick */ }
    }, 1500)
    return () => { stop = true; window.clearInterval(poll) }
  }, [])

  // opening line
  const greet = useCallback(() => {
    setPhase('greet')
    say("I'm Exo — the brain for your rig. Want me to look at what we can make faster?", [
      { label: 'Scan my PC', run: () => void scan(), primary: true },
      { label: 'Not now', run: () => idle() },
    ])
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [say])

  useEffect(() => { greet() /* eslint-disable-next-line */ }, [])

  function idle() {
    setPhase('done')
    say('Standing by. Wake me whenever.', [{ label: 'Scan my PC', run: () => void scan(), primary: true }])
  }

  async function scan() {
    setPhase('scanning')
    say('Reading every system on your machine…', [])
    try {
      await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      const notOptimized = ORDER.filter((id) => !d.modules.find((m) => m.id === id)?.applied)
      queue.current = notOptimized
      qi.current = 0
      if (notOptimized.length === 0) {
        done(d)
      } else {
        ask()
      }
    } catch {
      say("I couldn't finish the scan. Want to try again?", [{ label: 'Retry', run: () => void scan(), primary: true }])
    }
  }

  function ask() {
    const id = queue.current[qi.current]
    if (!id) return done()
    const name = LABEL[id]
    const remaining = queue.current.length - qi.current
    setPhase('ask')
    const lead =
      remaining > 1
        ? `${name} isn't optimized yet — one of ${remaining} I'd tune.`
        : `${name} is the last one that isn't optimized.`
    say(`${lead} Want me to do it?`, [
      { label: `Optimize ${name}`, run: () => void apply(id), primary: true },
      { label: 'Skip', run: () => next() },
      { label: 'Stop for now', run: () => done() },
    ])
  }

  function next() {
    qi.current += 1
    if (qi.current >= queue.current.length) done()
    else ask()
  }

  async function apply(id: string) {
    const name = LABEL[id]
    setPhase('working')
    say(`On it — tuning ${name}…`, [])
    try {
      await host.apply(id as ModuleId)
      const d = await host.getDashboard()
      setDash(d)
    } catch { /* still advance; report */ }
    setPhase('ask')
    // brief confirmation, then continue
    say(`${name} is optimized.`, [
      { label: 'Next', run: () => next(), primary: true },
      { label: 'Done for now', run: () => done() },
    ])
  }

  function done(d?: DashboardSnapshot) {
    const snap = d ?? dash
    const applied = snap?.modules.filter((m) => m.applied).length ?? 0
    setPhase('done')
    say(
      applied >= (snap?.modules.length ?? 6)
        ? "That's everything — your rig is fully dialed in."
        : "Done. Everything you okayed is optimized.",
      [
        { label: 'Scan again', run: () => void scan(), primary: true },
      ],
    )
  }

  const orbState: BrainState = speaking
    ? 'speak'
    : phase === 'scanning'
      ? 'think'
      : phase === 'working'
        ? 'work'
        : 'idle'

  const live = dash?.live
  const tel = live
    ? `CPU ${live.hasCpu === false ? '—' : Math.round(live.cpuPercent) + '%'}   GPU ${live.hasGpu === false ? '—' : Math.round(live.gpuPercent) + '%'}   MEM ${Math.round(live.memoryPercent)}%   NET ${live.netLinkSpeed && live.netLinkSpeed !== '—' ? live.netLinkSpeed : live.netLink.split(' ')[0]}`
    : ''
  const optimized = dash?.modules.filter((m) => m.applied).length ?? 0
  const total = dash?.modules.length ?? 6

  return (
    <div style={{ position: 'fixed', inset: 0, background: '#000', color: '#e9e9ec', fontFamily: 'var(--font-mono)', overflow: 'hidden', userSelect: 'none' }}>
      {/* window controls */}
      <div style={{ position: 'absolute', top: 12, right: 12, display: 'flex', gap: 6, zIndex: 10 }}>
        <button onClick={() => void host.minimize()} aria-label="Minimize" style={winBtn}><svg width="11" height="11" viewBox="0 0 12 12"><path d="M2 6.5h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg></button>
        <button onClick={() => void host.close()} aria-label="Close" style={winBtn}><svg width="11" height="11" viewBox="0 0 12 12"><path d="M3 3l6 6M9 3L3 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg></button>
      </div>
      <div style={{ position: 'absolute', top: 16, left: 20, fontSize: 10, letterSpacing: '0.14em', color: '#5c5c62', zIndex: 10 }}>{tel}</div>
      <div style={{ position: 'absolute', top: 15, left: '50%', transform: 'translateX(-50%)', fontSize: 10, letterSpacing: '0.22em', color: '#6a6a70', zIndex: 10 }}>{optimized}/{total} OPTIMIZED</div>

      {/* the brain */}
      <div style={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 6, padding: 24 }}>
        <BrainOrb state={orbState} size={300} />

        {/* what the brain says */}
        <div key={msgKey} style={{ marginTop: 18, maxWidth: 560, textAlign: 'center', animation: 'exo-say .5s var(--ease-spring, ease) both' }}>
          <p style={{ margin: 0, fontFamily: 'var(--font-display)', fontSize: 21, fontWeight: 500, lineHeight: 1.4, letterSpacing: '-0.01em' }}>{msg}</p>
        </div>

        {/* the answers */}
        <div style={{ marginTop: 20, display: 'flex', flexWrap: 'wrap', gap: 10, justifyContent: 'center', minHeight: 44 }}>
          {opts.map((o, i) => (
            <button key={i} onClick={o.run} style={o.primary ? primaryBtn : ghostBtn}>{o.label}</button>
          ))}
        </div>
      </div>
    </div>
  )
}

const winBtn: React.CSSProperties = { display: 'flex', alignItems: 'center', justifyContent: 'center', width: 34, height: 26, borderRadius: 9, background: 'rgba(255,255,255,0.04)', border: '1px solid rgba(255,255,255,0.1)', color: '#9a9aa0', cursor: 'pointer' }
const DISPLAY = { fontFamily: 'var(--font-display)' }
const primaryBtn: React.CSSProperties = { padding: '11px 24px', borderRadius: 999, background: '#f4f4f6', color: '#0b0b0d', ...DISPLAY, fontSize: 13, fontWeight: 700, border: 'none', cursor: 'pointer', boxShadow: '0 0 30px rgba(255,255,255,0.12)' }
const ghostBtn: React.CSSProperties = { padding: '11px 20px', borderRadius: 999, background: 'transparent', color: '#c4c4ca', ...DISPLAY, fontSize: 13, fontWeight: 600, border: '1px solid rgba(255,255,255,0.16)', cursor: 'pointer' }
