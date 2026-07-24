import { useCallback, useEffect, useRef, useState } from 'react'
import { BrainOrb, type BrainState } from '../components/BrainOrb'
import { host, type DashboardSnapshot, type ModuleId, type VerifyAllResult } from '../lib/host'

/* Exo is a brain. The orb is the whole UI: it reads the PC, forms an opinion,
   and *talks* — proposing what to optimize, one thing at a time. You answer;
   it works. Monochrome. No module grid, no top chrome (native window frame). */

const LABEL: Record<string, string> = {
  discord: 'Discord', brave: 'Brave', steam: 'Steam', internet: 'Internet', nvidia: 'NVIDIA', games: 'Games',
}
// The order the brain thinks about things — biggest FPS levers first.
const ORDER = ['nvidia', 'internet', 'steam', 'games', 'discord', 'brave'] as const

const pick = <T,>(a: T[]): T => a[Math.floor(Math.random() * a.length)]

type Sugg = { id: string; kind: 'optimize' | 'reapply' }
type Opt = { label: string; run: () => void; primary?: boolean }

export function OrbApp() {
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [phase, setPhase] = useState<'greet' | 'scanning' | 'ask' | 'working' | 'done'>('greet')
  const [msg, setMsg] = useState('')
  const [opts, setOpts] = useState<Opt[]>([])
  const [msgKey, setMsgKey] = useState(0)
  const [speaking, setSpeaking] = useState(false)
  const queue = useRef<Sugg[]>([])
  const qi = useRef(0)
  const spokeContext = useRef(false)
  const speakTimer = useRef<number | undefined>(undefined)

  const say = useCallback((message: string, options: Opt[]) => {
    setMsg(message)
    setOpts(options)
    setMsgKey((k) => k + 1)
    setSpeaking(true)
    window.clearTimeout(speakTimer.current)
    speakTimer.current = window.setTimeout(() => setSpeaking(false), 850)
  }, [])

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

  useEffect(() => { greet() /* eslint-disable-next-line */ }, [])

  function greet() {
    setPhase('greet')
    say(pick([
      "I'm Exo — the brain wired into your rig. Want me to see what we can sharpen?",
      "Exo, online. Say the word and I'll read your machine for anything holding back frames.",
      "I'm your rig's brain. Want me to scan for what we can make faster?",
    ]), [
      { label: 'Read my PC', run: () => void scan(), primary: true },
      { label: 'Not now', run: () => rest() },
    ])
  }

  function rest() {
    setPhase('done')
    say(pick(['Standing by. Wake me whenever.', "I'll be here. Ping me when you want a tune-up."]), [
      { label: 'Read my PC', run: () => void scan(), primary: true },
    ])
  }

  // Live-vitals aside the brain can drop in for personality / awareness.
  function vitalsAside(): string | null {
    const l = dash?.live
    if (!l) return null
    if (l.memoryPercent >= 88) return `your RAM's sitting at ${Math.round(l.memoryPercent)}% — worth closing a few background apps`
    if (l.netRating === 'Poor') return "your network's rated Poor right now"
    if (l.hasGpu !== false && l.gpuPercent >= 92) return 'your GPU is pinned — mid-game, maybe?'
    return null
  }

  async function scan() {
    setPhase('scanning')
    spokeContext.current = false
    say(pick(['Reading every system on your machine…', 'Feeling out the hardware…', 'Scanning — give me a second…']), [])
    try {
      const r: VerifyAllResult = await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      const q: Sugg[] = []
      for (const id of ORDER) {
        const row = r.results?.find((x) => x.id === id)
        const k = row?.statusKind
        if (k === 'partial') q.push({ id, kind: 'reapply' })
        else if (k === 'ready' || (!k && !d.modules.find((m) => m.id === id)?.applied)) q.push({ id, kind: 'optimize' })
        // applied / missing → nothing to do
      }
      queue.current = q
      qi.current = 0
      if (q.length === 0) done(d)
      else ask()
    } catch {
      say("I couldn't finish reading the PC. Try again?", [{ label: 'Retry', run: () => void scan(), primary: true }])
    }
  }

  function ask() {
    const s = queue.current[qi.current]
    if (!s) return done()
    const name = LABEL[s.id]
    const remaining = queue.current.length - qi.current

    let line =
      s.kind === 'reapply'
        ? pick([
            `${name} was tuned, but a few tweaks slipped — want me to top it back up?`,
            `Something reset part of ${name}. Reapply?`,
          ])
        : pick([
            `${name} is still running stock. Want me to tune it?`,
            `I can squeeze more out of ${name}. Do it?`,
            `${name} isn't optimized yet — want the pass?`,
          ])

    // Weave in awareness on the first ask, or when a vital is relevant.
    if (!spokeContext.current) {
      spokeContext.current = true
      const aside = vitalsAside()
      const count = remaining > 1 ? `I found ${remaining} things worth doing. ` : ''
      const ctx = aside ? `Also — ${aside}. ` : ''
      line = `${count}${ctx}${line}`
    } else if (s.id === 'internet' && (dash?.live?.netRating === 'Fair' || dash?.live?.netRating === 'Poor')) {
      line = `Your network's rated ${dash?.live?.netRating}. ${line}`
    }

    setPhase('ask')
    say(line.trim(), [
      { label: s.kind === 'reapply' ? `Reapply ${name}` : `Optimize ${name}`, run: () => void apply(s.id), primary: true },
      { label: 'Skip', run: () => next() },
      { label: 'Stop', run: () => done() },
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
    say(pick([`On it — tuning ${name}…`, `Working ${name}…`, `Give me a sec on ${name}…`]), [])
    try {
      const s = await host.apply(id as ModuleId)
      const d = await host.getDashboard()
      setDash(d)
      setPhase('ask')
      const more = qi.current + 1 < queue.current.length
      say(pick([`${name}'s optimized${s.statusKind === 'partial' ? ' — mostly' : ''}.`, `Done — ${name}'s dialed in.`, `${name} handled.`]),
        more
          ? [{ label: 'Next', run: () => next(), primary: true }, { label: 'Stop here', run: () => done() }]
          : [{ label: 'Wrap up', run: () => next(), primary: true }])
    } catch {
      setPhase('ask')
      say(`${name} hit a snag — I logged it. Move on?`, [{ label: 'Next', run: () => next(), primary: true }])
    }
  }

  function done(d?: DashboardSnapshot) {
    const snap = d ?? dash
    const applied = snap?.modules.filter((m) => m.applied).length ?? 0
    const total = snap?.modules.length ?? 6
    setPhase('done')
    const aside = vitalsAside()
    const base =
      applied >= total
        ? pick(["That's everything — your rig is fully dialed in.", 'All of it optimized. You are as sharp as I can make you.'])
        : pick(['Done. Everything you okayed is optimized.', "That's handled. Ping me anytime for another pass."])
    say(aside ? `${base} One thing though — ${aside}.` : base, [
      { label: 'Read again', run: () => void scan(), primary: true },
    ])
  }

  const orbState: BrainState = speaking ? 'speak' : phase === 'scanning' ? 'think' : phase === 'working' ? 'work' : 'idle'

  return (
    <div style={{ position: 'fixed', inset: 0, background: '#000', color: '#e9e9ec', fontFamily: 'var(--font-mono)', overflow: 'hidden', userSelect: 'none' }}>
      <div style={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 0, padding: 24, textAlign: 'center' }}>
        <BrainOrb state={orbState} size={420} />

        <div key={msgKey} style={{ marginTop: 8, maxWidth: 600, animation: 'exo-say .5s var(--ease-spring, ease) both' }}>
          <p style={{ margin: 0, fontFamily: 'var(--font-display)', fontSize: 20, fontWeight: 500, lineHeight: 1.42, letterSpacing: '-0.01em', color: '#dcdce0' }}>{msg}</p>
        </div>

        <div style={{ marginTop: 22, display: 'flex', flexWrap: 'wrap', gap: 10, justifyContent: 'center', minHeight: 44 }}>
          {opts.map((o, i) => (
            <button key={i} onClick={o.run} style={o.primary ? primaryBtn : ghostBtn}>{o.label}</button>
          ))}
        </div>
      </div>
    </div>
  )
}

const DISPLAY = { fontFamily: 'var(--font-display)' }
const primaryBtn: React.CSSProperties = { padding: '11px 24px', borderRadius: 999, background: '#f4f4f6', color: '#0b0b0d', ...DISPLAY, fontSize: 13, fontWeight: 700, border: 'none', cursor: 'pointer', boxShadow: '0 0 30px rgba(255,255,255,0.12)' }
const ghostBtn: React.CSSProperties = { padding: '11px 20px', borderRadius: 999, background: 'transparent', color: '#c4c4ca', ...DISPLAY, fontSize: 13, fontWeight: 600, border: '1px solid rgba(255,255,255,0.16)', cursor: 'pointer' }
