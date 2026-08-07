/**
 * noSystemBadge — suppress Windows taskbar / tray unread badge updates.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'noSystemBadge',
    title: 'No System Badge',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0

      const badge = findByProps('setBadge', 'setBadgeCount') ||
        findByProps('setApplicationBadge') ||
        findByProps('setTrayBadge')
      if (badge) {
        for (const key of Object.keys(badge)) {
          if (typeof badge[key] !== 'function') continue
          if (!/badge|tray|overlay|flash/i.test(key)) continue
          try {
            badge[key] = function () { return undefined }
            patched++
          } catch { /* */ }
        }
      }

      try {
        if (window.DiscordNative && DiscordNative.app && typeof DiscordNative.app.setBadge === 'function') {
          DiscordNative.app.setBadge = function () { return undefined }
          patched++
        }
      } catch { /* */ }

      return patched > 0
    }
  })
})()
