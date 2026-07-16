import { useMemo, useState } from 'react'
import type { ModuleId, PageId } from './data/mock'
import { modules } from './data/mock'
import { TopBar } from './components/TopBar'
import { SettingsFlyout } from './components/SettingsFlyout'
import { HomePage } from './pages/HomePage'
import { ModulePage } from './pages/ModulePage'
import { NvidiaPanelPage } from './pages/NvidiaPanelPage'
import './App.css'

function barActive(page: PageId): ModuleId | 'home' | null {
  if (page === 'home') return 'home'
  if (page === 'nvidia-panel') return 'nvidia'
  return page
}

export default function App() {
  const [page, setPage] = useState<PageId>('home')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [darkMode, setDarkMode] = useState(true)
  const [autoUpdate, setAutoUpdate] = useState(true)

  const showBack = page === 'nvidia-panel'

  const content = useMemo(() => {
    if (page === 'home') {
      return <HomePage onOpen={(id) => setPage(id)} />
    }
    if (page === 'nvidia-panel') {
      return <NvidiaPanelPage />
    }
    return (
      <ModulePage
        key={page}
        module={modules[page]}
        onOpenDisplayPanel={
          page === 'nvidia' ? () => setPage('nvidia-panel') : undefined
        }
      />
    )
  }, [page])

  const navigate = (id: ModuleId | 'home') => {
    setSettingsOpen(false)
    setPage(id)
  }

  return (
    <div className={`app-page ${darkMode ? 'theme-dark' : 'theme-light'}`}>
      <div className="exo-shell" data-testid="exo-shell">
        <div className="exo-workspace">
          <TopBar
            active={barActive(page)}
            settingsOpen={settingsOpen}
            onNavigate={navigate}
            onToggleSettings={() => setSettingsOpen((v) => !v)}
          />

          <div className="exo-stage">
            {showBack ? (
              <div className="exo-titlebar">
                <button
                  type="button"
                  className="exo-back"
                  data-testid="btn-back"
                  aria-label="Back"
                  onClick={() => setPage('nvidia')}
                >
                  ‹
                </button>
              </div>
            ) : null}
            <main className="exo-main">{content}</main>
          </div>
        </div>

        <SettingsFlyout
          open={settingsOpen}
          darkMode={darkMode}
          autoUpdate={autoUpdate}
          onClose={() => setSettingsOpen(false)}
          onDarkMode={setDarkMode}
          onAutoUpdate={setAutoUpdate}
        />
      </div>
    </div>
  )
}
