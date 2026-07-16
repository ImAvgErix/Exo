import { useCallback, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import type { ModuleId } from '../data/mock'
import { navItems } from '../data/mock'
import './TopBar.css'

interface TopBarProps {
  active: ModuleId | 'home' | null
  settingsOpen: boolean
  onNavigate: (id: ModuleId | 'home') => void
  onToggleSettings: () => void
}

type BlobKey = 'home' | ModuleId | 'gear'

function activeBlobKey(
  active: ModuleId | 'home' | null,
  settingsOpen: boolean,
): BlobKey {
  if (settingsOpen) return 'gear'
  if (active === 'home') return 'home'
  if (active) return active
  return 'home'
}

export function TopBar({
  active,
  settingsOpen,
  onNavigate,
  onToggleSettings,
}: TopBarProps) {
  const barRef = useRef<HTMLDivElement>(null)
  const blobRefs = useRef<Partial<Record<BlobKey, HTMLButtonElement>>>({})
  const [liquid, setLiquid] = useState({ x: 8, w: 52 })

  const measureLiquid = useCallback(() => {
    const bar = barRef.current
    const key = activeBlobKey(active, settingsOpen)
    const blob = blobRefs.current[key]
    if (!bar || !blob) return
    const barRect = bar.getBoundingClientRect()
    const blobRect = blob.getBoundingClientRect()
    setLiquid({
      x: blobRect.left - barRect.left,
      w: blobRect.width,
    })
  }, [active, settingsOpen])

  useLayoutEffect(() => {
    measureLiquid()
    const bar = barRef.current
    if (!bar) return
    const observer = new ResizeObserver(measureLiquid)
    observer.observe(bar)
    return () => observer.disconnect()
  }, [measureLiquid])

  const setBlobRef = (key: BlobKey) => (el: HTMLButtonElement | null) => {
    if (el) blobRefs.current[key] = el
  }

  return (
    <nav className="top-bar" aria-label="Exo navigation">
      <div ref={barRef} className="top-bar__pill glass glass--strong">
        <span
          className="top-bar__liquid"
          style={
            {
              '--liquid-x': `${liquid.x}px`,
              '--liquid-w': `${liquid.w}px`,
            } as CSSProperties
          }
          aria-hidden="true"
        />

        <button
          ref={setBlobRef('home')}
          type="button"
          className={`top-bar__blob ${!settingsOpen && active === 'home' ? 'is-active' : ''}`}
          data-testid="nav-home"
          aria-label="Home"
          onClick={() => onNavigate('home')}
        >
          <span className="top-bar__exo">EXO</span>
        </button>

        <div className="top-bar__modules">
          {navItems.map((item) => (
            <button
              key={item.id}
              ref={setBlobRef(item.id)}
              type="button"
              className={`top-bar__blob ${!settingsOpen && active === item.id ? 'is-active' : ''}`}
              data-testid={`nav-${item.id}`}
              aria-label={item.label}
              onClick={() => onNavigate(item.id)}
            >
              <img
                src={item.logo}
                alt=""
                width={22}
                height={22}
                className="top-bar__logo"
                draggable={false}
              />
              <span className="top-bar__label">{item.label}</span>
            </button>
          ))}
        </div>

        <button
          ref={setBlobRef('gear')}
          type="button"
          className={`top-bar__blob top-bar__gear-btn ${settingsOpen ? 'is-active' : ''}`}
          data-testid="nav-settings"
          aria-label="Settings"
          aria-expanded={settingsOpen}
          onClick={onToggleSettings}
        >
          <svg
            className={`top-bar__gear ${settingsOpen ? 'is-open' : ''}`}
            width="18"
            height="18"
            viewBox="0 0 24 24"
            fill="none"
            aria-hidden="true"
          >
            <path
              d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z"
              stroke="currentColor"
              strokeWidth="1.6"
            />
            <path
              d="M19.4 13.5a7.7 7.7 0 0 0 .05-1l1.7-1.33-1.6-2.77-2.04.62a7.9 7.9 0 0 0-1.72-1L15.5 5h-3.2l-.29 2.02a7.9 7.9 0 0 0-1.72 1l-2.04-.62-1.6 2.77L8.55 11.5c-.03.33-.05.67-.05 1s.02.67.05 1l-1.7 1.33 1.6 2.77 2.04-.62c.53.42 1.1.76 1.72 1L12.3 21h3.2l.29-2.02a7.9 7.9 0 0 0 1.72-1l2.04.62 1.6-2.77-1.75-1.33Z"
              stroke="currentColor"
              strokeWidth="1.6"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      </div>
    </nav>
  )
}
