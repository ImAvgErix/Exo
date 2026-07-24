import { useCallback, useEffect, useRef, useState } from 'react'
import { ThinkingOrb, type OrbState } from 'thinking-orbs'
import { host, onHostEvent, type DashboardSnapshot, type ModuleId, type ModuleStatus } from '../lib/host'

/* The whole app IS the thinking orb. Monochrome. The orb's state reflects what
   the PC is doing; a minimal black-and-white shell wraps it. */

const SYSTEMS = ['discord', 'brave', 'steam', 'internet', 'nvidia', 'games'] as const
const LABEL: Record<string, string> = {
  discord: 'Discord', brave: 'Brave', steam: 'Steam', internet: 'Internet', nvidia: 'NVIDIA', games: 'Games',
}

export function OrbApp() {
  const [dash, setDash] = useState<DashboardSnapshot | null>(null)
  const [sel, setSel] = useState(0)
  const [detail, setDetail] = useState<Record<string, ModuleStatus>>({})
  const [verifying, setVerifying] = useState(false)
  const [busy, setBusy] = useState<{ id: string; label: string } | null>(null)
  const [repairing, setRepairing] = useState(false)
  const [caption, setCaption] = useState('')
  const capTimer = useRef<number | undefined>(undefined)

  const id = SYSTEMS[sel]

  const flash = useCallback((s: string) => {
    setCaption(s)
    window.clearTimeout(capTimer.current)
    capTimer.current = window.setTimeout(() => setCaption(''), 2600)
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

  const loadDetail = useCallback(async (m: string, force = false) => {
    try {
      const s = await host.detect(m as ModuleId, force ? { force: true } : undefined)
      setDetail((d) => ({ ...d, [m]: s }))
    } catch { /* keep */ }
  }, [])

  useEffect(() => {
    if (id !== 'games' && !detail[id]) void loadDetail(id)
  }, [id, detail, loadDetail])

  useEffect(() => onHostEvent('module.progress', (data) => {
    const p = data as { status?: string }
    if (p.status) setBusy((b) => (b ? { ...b, label: p.status! } : b))
  }), [])

  const det = detail[id] ?? null
  const applied = dash?.modules.find((m) => m.id === id)?.applied ?? false
  const optimized = dash?.modules.filter((m) => m.applied).length ?? 0

  // Orb state reflects activity.
  const orbState: OrbState = verifying ? 'searching' : busy ? 'working' : repairing ? 'solving' : 'listening'

  async function runVerify() {
    if (verifying || busy) return
    setVerifying(true)
    flash('Scanning every system…')
    try {
      const r = await host.verifyAll()
      const d = await host.getDashboard()
      setDash(d)
      setDetail({})
      flash(`${r.applied} of ${r.applied + r.partial + r.ready + r.missing} verified`)
    } catch {
      flash('Scan failed')
    } finally {
      setVerifying(false)
    }
  }

  async function apply() {
    if (busy || verifying) return
    setBusy({ id, label: 'Starting…' })
    flash(`Optimizing ${LABEL[id]}…`)
    try {
      const s = await host.apply(id as ModuleId)
      setDetail((d) => ({ ...d, [id]: s }))
      const d = await host.getDashboard()
      setDash(d)
      flash(`${LABEL[id]} optimized`)
    } catch {
      flash(`${LABEL[id]} failed`)
      await loadDetail(id, true)
    } finally {
      setBusy(null)
    }
  }

  async function repair() {
    if (busy || verifying) return
    setRepairing(true)
    flash(`Reverting ${LABEL[id]}…`)
    try {
      const s = await host.repair(id as ModuleId)
      setDetail((d) => ({ ...d, [id]: s }))
      const d = await host.getDashboard()
      setDash(d)
      flash(`${LABEL[id]} reverted`)
    } catch {
      flash('Repair failed')
    } finally {
      setRepairing(false)
    }
  }

  const live = dash?.live
  const tel = live
    ? `CPU ${live.hasCpu === false ? '—' : Math.round(live.cpuPercent) + '%'}   GPU ${live.hasGpu === false ? '—' : Math.round(live.gpuPercent) + '%'}   MEM ${Math.round(live.memoryPercent)}%   NET ${live.netLinkSpeed && live.netLinkSpeed !== '—' ? live.netLinkSpeed : live.netLink.split(' ')[0]}`
    : ''

  const statusWord = busy?.label || (verifying ? 'Searching' : det?.statusText || (applied ? 'Optimized' : 'Ready'))
  const working = verifying || !!busy || repairing

  return (
    <div style={{ position: 'fixed', inset: 0, background: '#000', color: '#e9e9ec', fontFamily: 'var(--font-mono)', overflow: 'hidden', userSelect: 'none' }}>
      {/* window controls */}
      <div style={{ position: 'absolute', top: 12, right: 12, display: 'flex', gap: 6, zIndex: 10 }}>
        <button onClick={() => void host.minimize()} aria-label="Minimize" style={winBtn}><svg width="11" height="11" viewBox="0 0 12 12"><path d="M2 6.5h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg></button>
        <button onClick={() => void host.close()} aria-label="Close" style={winBtn}><svg width="11" height="11" viewBox="0 0 12 12"><path d="M3 3l6 6M9 3L3 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg></button>
      </div>

      {/* telemetry — whisper-quiet, top-left */}
      <div style={{ position: 'absolute', top: 16, left: 20, fontSize: 10, letterSpacing: '0.14em', color: '#5c5c62', zIndex: 10 }}>{tel}</div>

      {/* integrity — top center */}
      <div style={{ position: 'absolute', top: 15, left: '50%', transform: 'translateX(-50%)', fontSize: 10, letterSpacing: '0.22em', color: '#6a6a70', zIndex: 10 }}>
        {optimized}/{SYSTEMS.length} OPTIMIZED
      </div>

      {/* THE ORB — the whole app */}
      <div style={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 4 }}>
        <button
          onClick={() => void runVerify()}
          title="Scan every system"
          style={{ width: 300, height: 300, display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'transparent', border: 'none', cursor: working ? 'default' : 'pointer', padding: 0, filter: 'drop-shadow(0 0 44px rgba(255,255,255,0.09))' }}
        >
          {/* package tunes only size 64 — scale up to hero size */}
          <div style={{ transform: 'scale(4.6)', transformOrigin: 'center', lineHeight: 0 }}>
            <ThinkingOrb state={orbState} size={64} theme="dark" speed={working ? 1.15 : 0.75} />
          </div>
        </button>

        {/* selected system + status, right under the orb */}
        <div style={{ marginTop: 10, textAlign: 'center' }}>
          <div style={{ ...DISPLAY, fontSize: 26, fontWeight: 600, letterSpacing: '-0.01em' }}>{LABEL[id]}</div>
          <div style={{ marginTop: 6, fontSize: 11, letterSpacing: '0.16em', textTransform: 'uppercase', color: caption ? '#e9e9ec' : '#6a6a70', transition: 'color .3s' }}>
            {caption || statusWord}
          </div>
        </div>

        {/* actions */}
        <div style={{ marginTop: 16, display: 'flex', gap: 10 }}>
          <button onClick={() => void apply()} disabled={working} style={primaryBtn(working)}>
            {busy && busy.id === id ? 'Working…' : applied ? 'Reapply' : 'Optimize'}
          </button>
          <button onClick={() => void repair()} disabled={working} style={ghostBtn(working)}>Revert</button>
        </div>
      </div>

      {/* system selector — minimal dotted strip along the bottom */}
      <div style={{ position: 'absolute', bottom: 22, left: '50%', transform: 'translateX(-50%)', display: 'flex', gap: 4, alignItems: 'center', zIndex: 10 }}>
        {SYSTEMS.map((s, i) => {
          const on = i === sel
          const isApplied = dash?.modules.find((m) => m.id === s)?.applied
          return (
            <button
              key={s}
              onClick={() => setSel(i)}
              title={LABEL[s]}
              style={{ display: 'flex', alignItems: 'center', gap: 7, padding: '7px 12px', borderRadius: 999, border: 'none', cursor: 'pointer', background: on ? 'rgba(255,255,255,0.08)' : 'transparent', transition: 'background .25s' }}
            >
              <span style={{ width: 5, height: 5, borderRadius: '50%', background: isApplied ? '#e9e9ec' : '#3a3a40', boxShadow: isApplied ? '0 0 6px rgba(255,255,255,0.5)' : 'none' }} />
              <span style={{ fontSize: 10.5, letterSpacing: '0.1em', color: on ? '#e9e9ec' : '#6a6a70', transition: 'color .25s' }}>{LABEL[s]}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
}

const DISPLAY = { fontFamily: 'var(--font-display)' }
const winBtn: React.CSSProperties = { display: 'flex', alignItems: 'center', justifyContent: 'center', width: 34, height: 26, borderRadius: 9, background: 'rgba(255,255,255,0.04)', border: '1px solid rgba(255,255,255,0.1)', color: '#9a9aa0', cursor: 'pointer' }
function primaryBtn(disabled: boolean): React.CSSProperties {
  return { padding: '11px 26px', borderRadius: 999, background: '#f4f4f6', color: '#0b0b0d', ...DISPLAY, fontSize: 13, fontWeight: 700, border: 'none', cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.5 : 1, boxShadow: '0 0 30px rgba(255,255,255,0.12)' }
}
function ghostBtn(disabled: boolean): React.CSSProperties {
  return { padding: '11px 22px', borderRadius: 999, background: 'transparent', color: '#c4c4ca', ...DISPLAY, fontSize: 13, fontWeight: 600, border: '1px solid rgba(255,255,255,0.16)', cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.5 : 1 }
}
