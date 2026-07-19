import { useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { host, type ModuleId } from '../lib/host'
import { SettingsDrawer } from './SettingsDrawer'

const modules: { id: ModuleId; label: string; logo: string }[] = [
  { id: 'discord', label: 'Discord', logo: '/logos/discord.png' },
  { id: 'steam', label: 'Steam', logo: '/logos/steam.png' },
  { id: 'internet', label: 'Internet', logo: '/logos/internet.png' },
  { id: 'nvidia', label: 'NVIDIA', logo: '/logos/nvidia.png' },
  { id: 'riot', label: 'Riot', logo: '/logos/riot.png' },
  { id: 'epic', label: 'Epic', logo: '/logos/epic.png' },
]

/** Fixed side width so center nav never shifts when home appears. */
const SIDE_W = 'w-[96px]'

export function Shell() {
  const loc = useLocation()
  const nav = useNavigate()
  const onHome = loc.pathname === '/' || loc.pathname === ''
  const [settingsOpen, setSettingsOpen] = useState(false)

  return (
    <div className="relative flex h-full flex-col overflow-hidden bg-page text-text">
      <div className="shrink-0 px-3 pt-2.5">
        {/*
          Three-zone bar: left controls | absolutely centered optimizers | right spacer
          for native min/close overlay. Optimizers stay dead-center always.
        */}
        <header className="glass specular relative z-20 flex h-12 shrink-0 items-center rounded-2xl px-2">
          {/* Left cluster — fixed width (settings always; home slot reserved) */}
          <div className={`relative z-10 flex ${SIDE_W} shrink-0 items-center gap-1`}>
            <button
              type="button"
              onClick={() => setSettingsOpen((v) => !v)}
              className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-xl transition ${
                settingsOpen
                  ? 'bg-raised text-text ring-1 ring-glass-border'
                  : 'text-secondary hover:bg-raised hover:text-text'
              }`}
              aria-label="Settings"
              aria-expanded={settingsOpen}
              title="Settings"
            >
              <IconGear />
            </button>
            <button
              type="button"
              onClick={() => {
                if (onHome) return
                setSettingsOpen(false)
                nav('/')
              }}
              className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-xl transition ${
                onHome
                  ? 'pointer-events-none opacity-0'
                  : 'text-secondary hover:bg-raised hover:text-text'
              }`}
              aria-label="Home"
              title="Home"
              tabIndex={onHome ? -1 : 0}
              aria-hidden={onHome}
            >
              <IconHome />
            </button>
          </div>

          {/* Optimizers — true center of the bar, never pushed by side clusters */}
          <nav
            className="pointer-events-none absolute inset-0 z-0 flex items-center justify-center"
            aria-label="Optimizers"
          >
            <div className="pointer-events-auto flex items-center gap-0.5">
              {modules.map((m) => {
                const active =
                  loc.pathname === `/module/${m.id}` ||
                  loc.pathname.endsWith(`/module/${m.id}`)
                return (
                  <NavLink
                    key={m.id}
                    to={`/module/${m.id}`}
                    onClick={() => setSettingsOpen(false)}
                    className={`flex items-center gap-1.5 rounded-xl px-2.5 py-1.5 text-[11px] font-semibold tracking-wide transition ${
                      active
                        ? 'bg-raised text-text ring-1 ring-glass-border'
                        : 'text-muted hover:bg-raised/80 hover:text-secondary'
                    }`}
                  >
                    <img
                      src={m.logo}
                      alt=""
                      className="h-3.5 w-3.5 object-contain opacity-90"
                      draggable={false}
                    />
                    {m.label}
                  </NavLink>
                )
              })}
            </div>
          </nav>

          {/*
            Right cluster — matches native caption overlay width so min/close
            sit inside the glass bar. Also offers web fallbacks if host is unavailable.
          */}
          <div
            className={`relative z-10 ml-auto flex ${SIDE_W} shrink-0 items-center justify-end gap-1`}
          >
            <button
              type="button"
              onClick={() => void host.minimize()}
              className="flex h-7 w-9 items-center justify-center rounded-lg bg-raised text-secondary ring-1 ring-glass-border transition hover:bg-[#24242C] hover:text-text"
              aria-label="Minimize"
              title="Minimize"
            >
              <IconMinimize />
            </button>
            <button
              type="button"
              onClick={() => void host.close()}
              className="flex h-7 w-9 items-center justify-center rounded-lg bg-raised text-secondary ring-1 ring-glass-border transition hover:bg-[#C42B1C] hover:text-white hover:ring-[#E04A3A]"
              aria-label="Close"
              title="Close"
            >
              <IconClose />
            </button>
          </div>
        </header>
      </div>

      <main className="relative z-10 min-h-0 flex-1 overflow-hidden px-3 pb-3 pt-2.5">
        <Outlet />
      </main>

      <SettingsDrawer open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </div>
  )
}

function IconHome() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden>
      <path
        d="M4.5 10.5 12 4l7.5 6.5V20a1 1 0 0 1-1 1h-4.5v-5.5h-4V21H5.5a1 1 0 0 1-1-1v-9.5Z"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function IconGear() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden>
      <path
        d="M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z"
        stroke="currentColor"
        strokeWidth="1.75"
      />
      <path
        d="M19.4 13.5a7.8 7.8 0 0 0 .05-1.5 7.8 7.8 0 0 0-.05-1.5l2.05-1.6-2-3.45-2.45.95a7.6 7.6 0 0 0-2.6-1.5L14 2h-4l-.4 2.9a7.6 7.6 0 0 0-2.6 1.5l-2.45-.95-2 3.45 2.05 1.6a7.8 7.8 0 0 0-.05 1.5c0 .5.02 1 .05 1.5l-2.05 1.6 2 3.45 2.45-.95a7.6 7.6 0 0 0 2.6 1.5L10 22h4l.4-2.9a7.6 7.6 0 0 0 2.6-1.5l2.45.95 2-3.45-2.05-1.6Z"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function IconMinimize() {
  return (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden>
      <path d="M2 6.5h8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  )
}

function IconClose() {
  return (
    <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden>
      <path
        d="M3 3l6 6M9 3L3 9"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
    </svg>
  )
}
