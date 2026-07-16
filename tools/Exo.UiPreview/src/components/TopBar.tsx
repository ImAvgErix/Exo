import type { ModuleId } from '../data/mock'
import { navItems } from '../data/mock'
import './TopBar.css'

interface TopBarProps {
  active: ModuleId | 'home' | null
  settingsOpen: boolean
  onNavigate: (id: ModuleId | 'home') => void
  onToggleSettings: () => void
}

export function TopBar({
  active,
  settingsOpen,
  onNavigate,
  onToggleSettings,
}: TopBarProps) {
  const showExo = active !== 'home'

  return (
    <nav className="top-bar" aria-label="Exo navigation">
      <div className="top-bar__row">
        <div className="top-bar__end">
          {showExo ? (
            <button
              type="button"
              className="glass-circle enter"
              style={{ '--i': 0 } as React.CSSProperties}
              data-testid="nav-home"
              aria-label="Home"
              onClick={() => onNavigate('home')}
            >
              <span className="glass-circle__content">
                <span className="top-bar__exo">EXO</span>
              </span>
              <span className="glass-circle__label">Home</span>
            </button>
          ) : null}
        </div>

        <div className="top-bar__modules">
          {navItems.map((item, i) => (
            <button
              key={item.id}
              type="button"
              className={`glass-circle enter ${!settingsOpen && active === item.id ? 'is-active' : ''}`}
              style={{ '--i': i + 1 } as React.CSSProperties}
              data-testid={`nav-${item.id}`}
              aria-label={item.label}
              onClick={() => onNavigate(item.id)}
            >
              <span className="glass-circle__content">
                <img
                  src={item.logo}
                  alt=""
                  width={22}
                  height={22}
                  className="top-bar__logo"
                  draggable={false}
                />
              </span>
              <span className="glass-circle__label">{item.label}</span>
            </button>
          ))}
        </div>

        <div className="top-bar__end">
          <button
            type="button"
            className={`glass-circle enter ${settingsOpen ? 'is-active' : ''}`}
            style={{ '--i': navItems.length + 1 } as React.CSSProperties}
            data-testid="nav-settings"
            aria-label="Settings"
            aria-expanded={settingsOpen}
            onClick={onToggleSettings}
          >
            <span className="glass-circle__content">
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
            </span>
            <span className="glass-circle__label">Settings</span>
          </button>
        </div>
      </div>
    </nav>
  )
}
