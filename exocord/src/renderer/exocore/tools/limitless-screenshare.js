/**
 * limitlessScreenshare — first-party Equicord-class tool.
 * Raises client-side screenshare resolution / FPS caps (default 1440p60).
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  const MAX_RES = 1440
  const MAX_FPS = 60

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function eachModule (fn) {
    if (!window.Exocore || typeof window.Exocore.eachModule !== 'function') return undefined
    return window.Exocore.eachModule(fn)
  }

  ExocordTools.register({
    id: 'limitlessScreenshare',
    title: 'Limitless Screenshare',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0

      const quality = findByProps('getGoLiveSource') ||
        findByProps('updatePreset') ||
        findByProps('ApplicationStreamSettingKeys') ||
        eachModule(m => {
          try {
            if (m && (m.ApplicationStreamFPSButtons || m.ApplicationStreamResolutionButtonsWithSource)) return m
          } catch { /* */ }
          return undefined
        })

      if (quality) {
        for (const key of Object.keys(quality)) {
          const val = quality[key]
          if (typeof val === 'function' && /max|premium|nitro|canUse|isPremium/i.test(key)) {
            try {
              quality[key] = function () { return true }
              patched++
            } catch { /* */ }
          }
        }
        // Common button tables: inject 1440 / 60 if missing.
        for (const tableName of [
          'ApplicationStreamFPSButtons',
          'ApplicationStreamFPSButtonsWithSource',
          'ApplicationStreamResolutionButtons',
          'ApplicationStreamResolutionButtonsWithSource'
        ]) {
          const table = quality[tableName]
          if (!Array.isArray(table)) continue
          try {
            if (tableName.indexOf('FPS') >= 0) {
              if (!table.some(x => x && (x.value === MAX_FPS || x.label === String(MAX_FPS)))) {
                table.push({ value: MAX_FPS, label: String(MAX_FPS) })
                patched++
              }
            } else if (!table.some(x => x && (x.value === MAX_RES || x.label === String(MAX_RES) || x.label === '1440p'))) {
              table.push({ value: MAX_RES, label: '1440p' })
              patched++
            }
          } catch { /* */ }
        }
      }

      // Media engine constraints sometimes clamp width/height.
      const media = findByProps('getDesktopSource') || findByProps('setDesktopSource')
      if (media && typeof media.getConstraints === 'function') {
        try {
          const orig = media.getConstraints.bind(media)
          media.getConstraints = function () {
            const c = orig.apply(this, arguments) || {}
            if (c.video) {
              c.video.width = { max: 2560 }
              c.video.height = { max: 1440 }
              c.video.frameRate = { max: MAX_FPS }
            }
            return c
          }
          patched++
        } catch { /* */ }
      }

      return patched > 0
    }
  })
})()
