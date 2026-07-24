import { useEffect } from 'react'
import { initHostBridge } from './lib/host'
import { ReelApp } from './pages/ReelApp'

export default function App() {
  useEffect(() => {
    initHostBridge()
  }, [])

  return <ReelApp />
}
