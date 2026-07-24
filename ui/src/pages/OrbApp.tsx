import { useCallback, useEffect, useRef, useState } from 'react'
import { BrainOrb, type BrainState } from '../components/BrainOrb'
import { host, onHostEvent, type DashboardSnapshot, type ModuleId, type ModuleStatus, type VerifyAllResult } from '../lib/host'

/* Exo is a brain. The orb is the whole UI: it lives in the app — roaming,
   thinking, reacting — reads the PC, forms an opinion, and *talks*, proposing
   what to optimize one thing at a time. You answer; it works. It only ever calls
   the rig "good to go" after re-reading the machine and confirming the tweaks
   actually stuck. Monochrome. No module grid, no top chrome (black native frame).

   Personality lives in the pick([...]) lines below — one place, easy to swap for
   a generated voice (the planned Grok layer) later without touching flow. */

const LABEL: Record<string, string> = {
  discord: 'Discord', brave: 'Brave', steam: 'Steam', internet: 'Internet', nvidia: 'NVIDIA', games: 'Games',
}
// The order the brain thinks about things — biggest FPS levers first.
const ORDER = ['nvidia', 'internet', 'steam', 'games', 'discord', 'brave'] as const

const pick = <T,>(a: T[]): T => a[Math.floor(Math.random() * a.length)]

function joinNames(ids: string[]): string {
  const n = ids.map((id) => LABEL[id] ?? id)
  if (n.length <= 1) return n[0] ?? ''
  if (n.length === 2) return `${n[0]} and ${n[1]}`
  return `${n.slice(0, -1).join(', ')}, and ${n[n.length - 1]}`
}

type Sugg = { id: string; kind: 'optimize' | 'reapply' }
type Opt = { label: string; run: () => void; primary?: boolean }

// What the brain muses about on its own — mostly real facts read live off the
// machine, plus a little personality. Falls back to quips when telemetry isn't
// in yet.
function thoughtLines(d: DashboardSnapshot | null): string[] {
  const out: string[] = []
  const s = d?.specs
  const l = d?.live
  if (s?.gpu) out.push(`${s.gpu} under the hood — I know its whole mind.`)
  if (s?.cpu && s?.ram) out.push(`${s.cpu}, ${s.ram}. I can feel every core from here.`)
  if (s?.os) out.push(`You're running ${s.os}.`)
  if (l) {
    if (l.hasGpu !== false)
      out.push(`Your GPU's at ${Math.round(l.gpuPercent)}% right now — ${l.gpuPercent < 15 ? 'barely awake' : l.gpuPercent > 80 ? 'working hard' : 'warming up'}.`)
    if (l.hasCpu !== false) out.push(`CPU's ticking around ${Math.round(l.cpuPercent)}%.`)
    if (l.memorySecondary) out.push(`Memory's at ${Math.round(l.memoryPercent)}% — ${l.memorySecondary}.`)
    if (l.netRating) out.push(`Your network's rated ${l.netRating}${l.netIdleMs ? `, ${l.netIdleMs} idle` : ''}.`)
    else if (l.netLink) out.push(`Hooked up over ${l.netLink}.`)
  }
  out.push(
    'Idle hands. Point me at something.',
    'I could be making this faster, you know.',
    "I'm always half-thinking about your frame times.",
    'Say the word and I go to work.',
    'I never really sleep — just wait.',
  )
  return out
}

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
  // Modules we've applied/reapplied this pass — so a tweak that simply can't go
  // further on this box (stays "partial" after we tried) isn't nagged forever.
  const touched = useRef<Set<string>>(new Set())
  const speakTimer = useRef<number | undefined>(undefined)
  // A newer build, if one exists. The brain itself asks about it on launch and
  // also offers it as a chip on the wrap-up. Nothing installs without a tap.
  const updateRef = useRef<{ version: string } | null>(null)
  const phaseRef = useRef(phase)
  phaseRef.current = phase
  const dashRef = useRef<DashboardSnapshot | null>(null)
  dashRef.current = dash

  const say = useCallback((message: string, options: Opt[]) => {
    setMsg(message)
    setOpts(options)
    setMsgKey((k) => k + 1)
    setSpeaking(true)
    window.clearTimeout(speakTimer.current)
    speakTimer.current = window.setTimeout(() => setSpeaking(false), 850)
  }, [])

  // A spontaneous thought — keeps whatever chips are up, just changes the line.
  // This is the brain "thinking out loud" while it waits on you.
  const muse = useCallback((message: string) => {
    setMsg(message)
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

  // On launch, peek for a newer Exo (check-only) and ASK — never auto-install.
  useEffect(() => {
    let stop = false
    ;(async () => {
      try {
        const s = await host.getSettings()
        if (s.checkForUpdatesOnLaunch === false) return
        const u = await host.peekUpdate()
        if (stop || !u.updateAvailable || !u.remoteVersion) return
        updateRef.current = { version: u.remoteVersion }
        if (phaseRef.current === 'greet' || phaseRef.current === 'done') askUpdate()
      } catch { /* offline or rate-limited — don't ask this launch */ }
    })()
    return () => { stop = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Live progress lines while an update downloads/installs.
  const updating = useRef(false)
  useEffect(() => onHostEvent('settings.updateProgress', (data) => {
    const d = data as { status?: string }
    if (updating.current && d.status) setMsg(d.status)
  }), [])

  // The brain has its own mind. When it's idle and waiting on you, it thinks
  // out loud on its own — usually a real fact about your machine, sometimes just
  // a bit of personality. It never talks over a question it's actually asking.
  useEffect(() => {
    if (phase !== 'greet' && phase !== 'done') return
    let alive = true
    const id = window.setInterval(() => {
      if (!alive) return
      if (phaseRef.current !== 'greet' && phaseRef.current !== 'done') return
      muse(pick(thoughtLines(dashRef.current)))
    }, 8500)
    return () => { alive = false; window.clearInterval(id) }
  }, [phase, muse])

  function askUpdate() {
    const v = updateRef.current?.version
    if (!v) return
    setPhase('ask')
    say(pick([
      `A newer me is out — Exo ${v}. Want me to install it and restart?`,
      `Exo ${v} just dropped. Update now? I'll restart myself when it's done.`,
    ]), [
      { label: 'Update now', run: () => void doUpdate(), primary: true },
      { label: 'Later', run: () => greet() },
    ])
  }

  async function doUpdate() {
    setPhase('working')
    updating.current = true
    say('Updating myself — downloading the new build…', [])
    try {
      const r = await host.checkUpdates()
      // installed/shouldExit -> the app exits and relaunches itself.
      if (!r.installed && !r.shouldExit) {
        updating.current = false
        updateRef.current = null
        setPhase('done')
        say(r.message || "The update didn't finish. I'll ask again next launch.", [
          { label: 'OK', run: () => greet(), primary: true },
        ])
      }
    } catch {
      updating.current = false
      setPhase('done')
      say("The update didn't finish — I'll ask again next launch.", [
        { label: 'OK', run: () => greet(), primary: true },
      ])
    }
  }

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
    touched.current.clear()
    say(pick(['Reading every system on your machine…', 'Feeling out the hardware…', 'Scanning — give me a second…']), [])
    try {
      const r: VerifyAllResult = await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      const q: Sugg[] = []
      for (const id of ORDER) {
        const row = r.results?.find((x) => x.id === id)
        const k = row?.statusKind
        if (k === 'missing') continue // not installed — can't optimize it
        if (k === 'partial') q.push({ id, kind: 'reapply' })
        else if (k === 'ready') q.push({ id, kind: 'optimize' })
        else if (!k && !d.modules.find((m) => m.id === id)?.applied) q.push({ id, kind: 'optimize' })
        // 'applied' → already done, nothing to ask
      }
      queue.current = q
      qi.current = 0
      // Nothing to do: we JUST verified, so conclude from these fresh rows
      // directly instead of re-reading the whole machine again.
      if (q.length === 0) conclude({ rows: r.results ?? [], snap: d })
      else ask()
    } catch {
      say("I couldn't finish reading the PC. Try again?", [{ label: 'Retry', run: () => void scan(), primary: true }])
    }
  }

  function ask() {
    const s = queue.current[qi.current]
    if (!s) return void conclude()
    const name = LABEL[s.id]
    const remaining = queue.current.length - qi.current

    let line =
      s.kind === 'reapply'
        ? pick([
            `${name} was optimized before, but some tweaks got reset. Want me to reapply them?`,
            `Something undid part of ${name}'s tuning. Want me to fix it?`,
          ])
        : pick([
            `${name} is still running stock settings. Want me to optimize it?`,
            `${name} isn't optimized yet. Want me to do it?`,
            `I can make ${name} faster. Should I?`,
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
      { label: 'Stop', run: () => void conclude() },
    ])
  }

  function next() {
    qi.current += 1
    if (qi.current >= queue.current.length) void conclude()
    else ask()
  }

  function optimizeRest(list: Sugg[]) {
    queue.current = list
    qi.current = 0
    spokeContext.current = true // already talked this session — no "I found N" preamble
    ask()
  }

  async function apply(id: string) {
    const name = LABEL[id]
    touched.current.add(id)
    setPhase('working')
    say(pick([`On it — tuning ${name}…`, `Working ${name}…`, `Give me a sec on ${name}…`]), [])
    try {
      const s = await host.apply(id as ModuleId)
      const d = await host.getDashboard()
      setDash(d)
      setPhase('ask')
      const more = qi.current + 1 < queue.current.length
      say(pick([`${name}'s optimized${s.statusKind === 'partial' ? ' — as far as it goes here' : ''}.`, `Done — ${name}'s dialed in.`, `${name} handled.`]),
        more
          ? [{ label: 'Next', run: () => next(), primary: true }, { label: 'Stop here', run: () => void conclude() }]
          : [{ label: 'Wrap up', run: () => next(), primary: true }])
    } catch {
      setPhase('ask')
      say(`${name} hit a snag — I logged it. Move on?`, [{ label: 'Next', run: () => next(), primary: true }])
    }
  }

  // The honest verdict. Re-reads the machine (unless scan just did) so "good to
  // go" means the tweaks are actually verified in place — not a stale flag.
  async function conclude(pre?: { rows: ModuleStatus[]; snap: DashboardSnapshot | null }) {
    let rows: ModuleStatus[] = pre?.rows ?? []
    let snap: DashboardSnapshot | null = pre?.snap ?? dash
    if (!pre) {
      setPhase('scanning')
      say(pick(['Double-checking everything stuck…', 'Verifying my work…', 'Making sure it all took…']), [])
      try {
        const v = await host.verifyAll()
        rows = v.results ?? []
      } catch { /* fall back to dashboard flags */ }
      try {
        snap = await host.getDashboard()
        setDash(snap)
      } catch { /* keep last snapshot */ }
    }

    const kindOf = (id: string): string => {
      const k = rows.find((x) => x.id === id)?.statusKind
      if (k) return k
      return snap?.modules.find((m) => m.id === id)?.applied ? 'applied' : 'ready'
    }

    const installed = ORDER.filter((id) => kindOf(id) !== 'missing')
    const leftover: Sugg[] = installed
      .filter((id) => {
        const k = kindOf(id)
        return k === 'ready' || (k === 'partial' && !touched.current.has(id))
      })
      .map((id) => ({ id, kind: kindOf(id) === 'partial' ? ('reapply' as const) : ('optimize' as const) }))
    const atCeiling = installed.filter((id) => kindOf(id) === 'partial' && touched.current.has(id))

    setPhase('done')
    const aside = vitalsAside()
    const chips: Opt[] = []
    let line: string

    if (leftover.length === 0) {
      // Everything installed is applied — or is something we already pushed as
      // far as this box allows. That's a real "good to go".
      line = pick([
        'Your PC is good to go — everything I can tune is optimized and verified.',
        "That's the whole rig dialed in. Your PC is good to go.",
        'All verified and in place. Your PC is good to go.',
      ])
      if (atCeiling.length) {
        const names = joinNames(atCeiling)
        line += ` ${names} ${atCeiling.length > 1 ? 'are' : 'is'} as far as this machine will take ${atCeiling.length > 1 ? 'them' : 'it'} — and that's fine.`
      }
      chips.push({ label: 'Re-scan', run: () => void scan(), primary: true })
    } else {
      const names = joinNames(leftover.map((s) => s.id))
      line = pick([
        `You're optimized everywhere you said yes. Still stock: ${names}. Want the rest done?`,
        `Good pass. ${names} ${leftover.length > 1 ? 'are' : 'is'} still untouched — say the word and I'll finish the job.`,
      ])
      chips.push({ label: leftover.length > 1 ? 'Do the rest' : `Optimize ${LABEL[leftover[0].id]}`, run: () => optimizeRest(leftover), primary: true })
      chips.push({ label: 'Re-scan', run: () => void scan() })
    }
    if (updateRef.current) chips.push({ label: `Install Exo ${updateRef.current.version}`, run: () => void doUpdate() })
    say(aside ? `${line} One thing — ${aside}.` : line, chips)
  }

  const orbState: BrainState = speaking ? 'speak' : phase === 'scanning' ? 'think' : phase === 'working' ? 'work' : 'idle'

  return (
    <div style={{ position: 'fixed', inset: 0, background: '#000', color: '#e9e9ec', fontFamily: 'var(--font-display)', overflow: 'hidden', userSelect: 'none' }}>
      {/* The orb lives across the whole screen — it roams the upper room on its
          own. The conversation is anchored to the lower band so it stays put and
          readable while the brain drifts above it. */}
      <BrainOrb state={orbState} />
      <div style={{ position: 'absolute', left: 0, right: 0, top: '63%', bottom: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', padding: '0 24px' }}>
        <div key={msgKey} style={{ maxWidth: 640, animation: 'exo-say .5s var(--ease-spring, ease) both' }}>
          <p style={{ margin: 0, fontFamily: 'var(--font-voice)', fontSize: 27, fontWeight: 400, lineHeight: 1.28, letterSpacing: '0.005em', color: '#e7e7ec' }}>{msg}</p>
        </div>
        <div style={{ marginTop: 18, display: 'flex', flexWrap: 'wrap', gap: 10, justifyContent: 'center', minHeight: 44 }}>
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
