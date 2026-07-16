import { useCallback, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import type { ModuleId } from '../data/mock'
import { navItems } from '../data/mock'
import './FloatingDock.css'

interface FloatingDockProps {
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

export function FloatingDock({
  active,
  settingsOpen,
  onNavigate,
  onToggleSettings,
}: FloatingDockProps) {
  const pillRef = useRef<HTMLDivElement>(null)
  const blobRefs = useRef<Partial<Record<BlobKey, HTMLButtonElement>>>({})
  const [liquidY, setLiquidY] = useState(10)

  const measureLiquid = useCallback(() => {
    const pill = pillRef.current
    const key = activeBlobKey(active, settingsOpen)
    const blob = blobRefs.current[key]
    if (!pill || !blob) return
    const pillRect = pill.getBoundingClientRect()
    const blobRect = blob.getBoundingClientRect()
    setLiquidY(blobRect.top - pillRect.top)
  }, [active, settingsOpen])

  useLayoutEffect(() => {
    measureLiquid()
    const pill = pillRef.current
    if (!pill) return
    const observer = new ResizeObserver(measureLiquid)
    observer.observe(pill)
    return () => observer.disconnect()
  }, [measureLiquid])

  const setBlobRef = (key: BlobKey) => (el: HTMLButtonElement | null) => {
    if (el) blobRefs.current[key] = el
  }

  return (
    <nav className="floating-dock" aria-label="Exo navigation">
      <div
        ref={pillRef}
        className="floating-dock__pill glass glass--strong"
      >
        <span
          className="floating-dock__liquid"
          style={{ '--liquid-y': `${liquidY}px` } as CSSProperties}
          aria-hidden="true"
        />

        <div className="floating-dock__zone floating-dock__zone--top">
          <button
            ref={setBlobRef('home')}
            type="button"
            className={`floating-dock__blob ${!settingsOpen && active === 'home' ? 'is-active' : ''}`}
            data-testid="nav-home"
            aria-label="Home"
            onClick={() => onNavigate('home')}
          >
            <span className="floating-dock__exo">EXO</span>
          </button>
        </div>

        <div className="floating-dock__zone floating-dock__zone--mid">
          <div className="floating-dock__items">
            {navItems.map((item) => (
              <button
                key={item.id}
                ref={setBlobRef(item.id)}
                type="button"
                className={`floating-dock__blob ${!settingsOpen && active === item.id ? 'is-active' : ''}`}
                data-testid={`nav-${item.id}`}
                aria-label={item.label}
                onClick={() => onNavigate(item.id)}
              >
                <img
                  src={item.logo}
                  alt=""
                  width={24}
                  height={24}
                  className="floating-dock__logo"
                  draggable={false}
                />
              </button>
            ))}
          </div>
        </div>

        <div className="floating-dock__zone floating-dock__zone--bottom">
          <button
            ref={setBlobRef('gear')}
            type="button"
            className={`floating-dock__blob floating-dock__gear-btn ${settingsOpen ? 'is-active' : ''}`}
            data-testid="nav-settings"
            aria-label="Settings"
            aria-expanded={settingsOpen}
            onClick={onToggleSettings}
          >
            <svg
              className={`floating-dock__gear ${settingsOpen ? 'is-open' : ''}`}
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
      </div>
    </nav>
  )
}
