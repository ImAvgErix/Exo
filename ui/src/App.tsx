import { useEffect } from 'react'
import { HashRouter, Navigate, Route, Routes } from 'react-router-dom'
import { initHostBridge } from './lib/host'
import { Shell } from './components/Shell'
import { HomePage } from './pages/HomePage'
import { ModulePage } from './pages/ModulePage'
import { GamesPage } from './pages/GamesPage'

export default function App() {
  useEffect(() => {
    initHostBridge()
  }, [])

  return (
    <HashRouter>
      <Routes>
        <Route element={<Shell />}>
          <Route index element={<HomePage />} />
          <Route path="module/games" element={<GamesPage />} />
          <Route path="module/:id" element={<ModulePage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </HashRouter>
  )
}
