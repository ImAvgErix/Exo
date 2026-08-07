/**
 * ignoreActivities — stop sharing game / custom activity presence client-side.
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

  function ensureCss () {
    if (document.getElementById('exocord-ignore-activities')) return
    const style = document.createElement('style')
    style.id = 'exocord-ignore-activities'
    style.textContent = `
      [class*="activityUserPopout"],
      [class*="activityPanel"],
      [class*="nowPlaying"],
      [class*="activityCard"] {
        display: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'ignoreActivities',
    title: 'Ignore Activities',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      let patched = 0

      const activity = findByProps('updateActivity', 'clearActivity') ||
        findByProps('setActivity', 'setCustomStatus')
      if (activity) {
        for (const key of Object.keys(activity)) {
          if (typeof activity[key] !== 'function') continue
          if (!/activity|presence|status|game|rich/i.test(key)) continue
          try {
            activity[key] = function () { return null }
            patched++
          } catch { /* */ }
        }
      }

      const settings = findStore('UserSettingsStore') || findStore('ApplicationStore')
      if (settings && typeof settings.getSettings === 'function') {
        try {
          const s = settings.getSettings()
          if (s && s.activity) {
            s.activity = Object.assign({}, s.activity, { showCurrentGame: false })
            patched++
          }
        } catch { /* */ }
      }

      return patched > 0 || !!document.documentElement
    }
  })
})()
