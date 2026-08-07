import { useEffect } from 'react'
import { MotionConfig } from 'motion/react'
import { initHostBridge } from './lib/host'
import { ExoApp } from './components/ExoApp'

/**
 * Exo 4.8.1 — AMOLED tweaks UI (Grok Build design) wired to the host.
 */
export default function App() {
  useEffect(() => {
    initHostBridge()
  }, [])

  return (
    <MotionConfig
      reducedMotion="user"
      transition={{ type: 'spring', stiffness: 520, damping: 38, mass: 0.72 }}
    >
      <ExoApp />
    </MotionConfig>
  )
}
