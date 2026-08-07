/**
 * volumeBooster — raise per-user voice volume ceiling past Discord's 200% cap.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  const MAX = 500

  ExocordTools.register({
    id: 'volumeBooster',
    title: 'Volume Booster',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0
      const media = findByProps('setLocalVolume') || findByProps('getLocalVolume')
      if (media && typeof media.setLocalVolume === 'function') {
        const orig = media.setLocalVolume.bind(media)
        media.setLocalVolume = function (userId, volume) {
          const v = typeof volume === 'number' ? Math.min(MAX, Math.max(0, volume)) : volume
          return orig(userId, v)
        }
        patched++
      }

      // Slider max constants sometimes live on a settings module.
      const caps = findByProps('MAX_VOLUME') || findByProps('MediaEngineContextSettings')
      if (caps) {
        for (const key of Object.keys(caps)) {
          if (/MAX_VOLUME|VOLUME_MAX|maxVolume/i.test(key) && typeof caps[key] === 'number') {
            try {
              caps[key] = MAX
              patched++
            } catch { /* */ }
          }
        }
      }

      return patched > 0
    }
  })
})()
