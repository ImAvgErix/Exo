/**
 * stopAutoUnread — throttle automatic mark-as-read on rapid channel switches.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  const COOLDOWN_MS = 1500

  ExocordTools.register({
    id: 'stopAutoUnread',
    title: 'Stop Auto Unread',
    tier: 'default',
    defaultOn: true,
    apply () {
      const read = findByProps('ack', 'ackChannel') ||
        findByProps('markChannelRead', 'markRead')
      if (!read) return false

      let lastAt = 0
      let patched = 0

      for (const key of ['ack', 'ackChannel', 'markChannelRead', 'markRead']) {
        if (typeof read[key] !== 'function') continue
        try {
          const orig = read[key].bind(read)
          read[key] = function () {
            const now = Date.now()
            if (now - lastAt < COOLDOWN_MS) return undefined
            lastAt = now
            return orig.apply(this, arguments)
          }
          patched++
        } catch { /* */ }
      }

      return patched > 0
    }
  })
})()
