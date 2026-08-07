/**
 * noProfileThemes — strip Nitro profile theme / banner effects client-side.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function ensureCss () {
    if (document.getElementById('exocord-no-profile-themes')) return
    const style = document.createElement('style')
    style.id = 'exocord-no-profile-themes'
    style.textContent = `
      [class*="profileTheme"],
      [class*="userProfileTheme"],
      [class*="profileEffects"],
      [class*="premiumProfile"],
      [style*="profile-gradient"] {
        background: none !important;
        animation: none !important;
      }
      [class*="profileTheme"]::before,
      [class*="profileTheme"]::after {
        display: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'noProfileThemes',
    title: 'No Profile Themes',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      let patched = 0
      const themes = findByProps('getUserProfileTheme') ||
        findByProps('getProfileTheme') ||
        findByProps('canUseProfileThemes')
      if (themes) {
        for (const key of Object.keys(themes)) {
          if (typeof themes[key] !== 'function') continue
          if (!/theme|profile|premium|banner|effect/i.test(key)) continue
          try {
            themes[key] = function () { return null }
            patched++
          } catch { /* */ }
        }
      }
      return patched > 0 || !!document.documentElement
    }
  })
})()
