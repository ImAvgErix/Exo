/**
 * disableCallIdle — tool wrapper for voice-channel idle disconnect suppression.
 * Complements Exocore noCallIdle; registers as a first-party tool.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function findStore (name) {
    if (!window.Exocore || typeof window.Exocore.findStore !== 'function') return null
    return window.Exocore.findStore(name)
  }

  ExocordTools.register({
    id: 'disableCallIdle',
    title: 'Disable Call Idle',
    tier: 'default',
    defaultOn: true,
    apply () {
      const idle = findByProps('setIdle', 'handleIdleUpdate') ||
        findByProps('startIdleTimer') ||
        findStore('IdleStore')
      if (!idle) return false

      let patched = false
      for (const name of ['startIdleTimer', 'handleIdleUpdate', 'setIdle', 'resetIdleTimer']) {
        if (typeof idle[name] !== 'function') continue
        try {
          idle[name] = function () { /* Exocord: no idle disconnect */ }
          patched = true
        } catch { /* */ }
      }
      return patched
    }
  })
})()
