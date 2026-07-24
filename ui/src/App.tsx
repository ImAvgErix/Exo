import { useEffect } from 'react'
import { initHostBridge } from './lib/host'
import { OrbApp } from './pages/OrbApp'

export default function App() {
  useEffect(() => {
    initHostBridge()
  }, [])

  return <OrbApp />
}
