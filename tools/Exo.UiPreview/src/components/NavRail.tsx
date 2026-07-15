import type { ModuleId } from '../data/mock'
import { navItems } from '../data/mock'
import './NavRail.css'

interface NavRailProps {
  active: ModuleId | 'home' | null
  settingsOpen: boolean
  onNavigate: (id: ModuleId | 'home') => void
  onToggleSettings: () => void
}

export function NavRail({
  active,
  settingsOpen,
  onNavigate,
  onToggleSettings,
}: NavRailProps) {
  return (
    <nav className="nav-rail" aria-label="Exo navigation">
      <button
        type="button"
        className={`nav-rail__brand ${active === 'home' ? 'is-active' : ''}`}
        data-testid="nav-home"
        aria-label="Home"
        onClick={() => onNavigate('home')}
      >
        EXO
      </button>

      <div className="nav-rail__items">
        {navItems.map((item) => (
          <button
            key={item.id}
            type="button"
            className={`nav-rail__item ${active === item.id ? 'is-active' : ''}`}
            data-testid={`nav-${item.id}`}
            aria-label={item.label}
            onClick={() => onNavigate(item.id)}
          >
            <img
              src={item.logo}
              alt=""
              width={22}
              height={22}
              className="nav-rail__logo"
              draggable={false}
            />
            <span className="nav-rail__label">{item.label}</span>
          </button>
        ))}
      </div>

      <button
        type="button"
        className={`nav-rail__settings ${settingsOpen ? 'is-active' : ''}`}
        data-testid="nav-settings"
        aria-label="Settings"
        aria-expanded={settingsOpen}
        onClick={onToggleSettings}
      >
        <svg
          className={`nav-rail__gear ${settingsOpen ? 'is-open' : ''}`}
          width="15"
          height="15"
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
    </nav>
  )
}
