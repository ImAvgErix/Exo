/**
 * notificationVolume — scale notification sounds (complements Exocore quietSounds).
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  const SCALE = 0.5
  const NOTIFY = /^(notification|mention|reaction|call|ring|pom|deafen|mute|disconnect|join|leave|stream)/i

  ExocordTools.register({
    id: 'notificationVolume',
    title: 'Notification Volume',
    tier: 'default',
    defaultOn: true,
    apply () {
      const sounds = findByProps('playSound')
      if (!sounds || typeof sounds.playSound !== 'function') return false

      const current = sounds.playSound.bind(sounds)
      if (current.__exocordNotifyVol) return true

      sounds.playSound = function (name, volume, ...rest) {
        const isNotify = typeof name === 'string' && NOTIFY.test(name)
        if (!isNotify) return current(name, volume, ...rest)
        const scaled = typeof volume === 'number' ? volume * SCALE : SCALE
        return current(name, scaled, ...rest)
      }
      sounds.playSound.__exocordNotifyVol = true
      return true
    }
  })
})()
