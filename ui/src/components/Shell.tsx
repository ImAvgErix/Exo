import { useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { AnimatePresence, LayoutGroup, motion, useReducedMotion } from 'framer-motion'
import { host, type ModuleId } from '../lib/host'
import { SettingsDrawer } from './SettingsDrawer'
import { WelcomePrompt } from './WelcomePrompt'

const modules: { id: ModuleId; label: string; logo: string }[] = [
  { id: 'discord', label: 'Discord', logo: '/logos/discord.png' },
  { id: 'brave', label: 'Brave', logo: '/logos/brave.png' },
  { id: 'steam', label: 'Steam', logo: '/logos/steam.png' },
  { id: 'games', label: 'Games', logo: '/logos/games.png' },
  { id: 'internet', label: 'Internet', logo: '/logos/internet.png' },
  { id: 'nvidia', label: 'NVIDIA', logo: '/logos/nvidia.png' },
]

export function Shell() {
  const loc = useLocation()
  const nav = useNavigate()
  const onHome = loc.pathname === '/' || loc.pathname === ''
  const [settingsOpen, setSettingsOpen] = useState(false)
  const reduce = useReducedMotion()

  return (
    <div className="relative flex h-full flex-col overflow-hidden bg-page text-text">
      <WelcomePrompt />
      {/*
        Header is a 3-column grid so the module rail can never cover Settings / caption.
        Absolute-centered nav used to steal gear clicks once Brave made the rail wider.
        z-[70] keeps chrome above SettingsDrawer backdrop so the gear always toggles.
      */}
      <div className="relative z-[70] shrink-0 px-3 pt-2.5">
        <header className="glass specular grid h-12 shrink-0 grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-1 rounded-2xl px-2">
          <div className="flex shrink-0 items-center gap-1">
            <button
              type="button"
              onClick={() => setSettingsOpen((v) => !v)}
              className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-xl ${
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
              className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-xl ${
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

          <nav
            className="flex min-w-0 items-center justify-center overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
            aria-label="Optimizers"
          >
            <LayoutGroup id="exo-nav">
              <div className="flex max-w-full items-center gap-0.5">
                {modules.map((m) => {
                  const active =
                    loc.pathname === `/module/${m.id}` ||
                    loc.pathname.endsWith(`/module/${m.id}`)
                  return (
                    <NavLink
                      key={m.id}
                      to={`/module/${m.id}`}
                      onClick={() => setSettingsOpen(false)}
                      className={`relative flex shrink-0 items-center gap-1 rounded-xl px-1.5 py-1.5 text-[10px] font-semibold tracking-wide sm:gap-1.5 sm:px-2 sm:text-[11px] ${
                        active ? 'text-text' : 'text-muted hover:text-secondary'
                      }`}
                      title={m.label}
                    >
                      {active && (
                        <motion.span
                          layoutId={reduce ? undefined : 'nav-pill'}
                          className="absolute inset-0 -z-0 rounded-xl bg-raised ring-1 ring-glass-border"
                          transition={
                            reduce
                              ? { duration: 0 }
                              : { type: 'spring', duration: 0.38, bounce: 0.12 }
                          }
                          aria-hidden
                        />
                      )}
                      <img
                        src={m.logo}
                        alt=""
                        className="relative z-10 h-3.5 w-3.5 shrink-0 object-contain opacity-90"
                        draggable={false}
                      />
                      <span className="relative z-10 max-w-[4.5rem] truncate">{m.label}</span>
                    </NavLink>
                  )
                })}
              </div>
            </LayoutGroup>
          </nav>

          <div className="flex shrink-0 items-center justify-end gap-1">
            <button
              type="button"
              onClick={() => void host.minimize()}
              className="flex h-7 w-9 items-center justify-center rounded-lg bg-raised text-secondary ring-1 ring-glass-border hover:bg-[#24242C] hover:text-text"
              aria-label="Minimize"
              title="Minimize"
            >
              <IconMinimize />
            </button>
            <button
              type="button"
              onClick={() => void host.close()}
              className="flex h-7 w-9 items-center justify-center rounded-lg bg-raised text-secondary ring-1 ring-glass-border hover:bg-[#C42B1C] hover:text-white hover:ring-[#E04A3A]"
              aria-label="Close"
              title="Close"
            >
              <IconClose />
            </button>
          </div>
        </header>
      </div>

      <main className="relative z-10 min-h-0 flex-1 overflow-hidden px-3 pb-3 pt-2.5">
        <AnimatePresence mode="wait" initial={false}>
          <motion.div
            key={loc.pathname}
            className="h-full min-h-0"
            initial={reduce ? false : { opacity: 0, y: 6 }}
            animate={{ opacity: 1, y: 0 }}
            exit={reduce ? undefined : { opacity: 0, y: -4 }}
            transition={
              reduce
                ? { duration: 0 }
                : { duration: 0.22, ease: [0.23, 1, 0.32, 1] }
            }
          >
            <Outlet />
          </motion.div>
        </AnimatePresence>
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
