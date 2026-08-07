/**
 * noNitroUpsell — kill Nitro / Gift / Shop / Boost nag surfaces client-side.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function ensureCss () {
    if (document.getElementById('exocord-no-nitro-upsell')) return
    const style = document.createElement('style')
    style.id = 'exocord-no-nitro-upsell'
    style.textContent = `
      [class*="premiumUpsell"],
      [class*="premiumBrand"],
      [class*="giftButton"],
      [class*="nitroWheel"],
      [href*="/store"],
      [href*="/nitro"],
      button[aria-label*="Nitro" i],
      button[aria-label*="Gift" i],
      a[href="/quest-home"],
      [class*="questHome"],
      [class*="boostedGuildBanner"] {
        display: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'noNitroUpsell',
    title: 'No Nitro Upsell',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      let patched = 0

      const open = findByProps('openPremiumUpsellModal') ||
        findByProps('openNitroUpsell') ||
        findByProps('showPremiumUpsell')
      if (open) {
        for (const key of Object.keys(open)) {
          if (typeof open[key] !== 'function') continue
          if (!/upsell|nitro|premium|gift|boost|quest/i.test(key)) continue
          try {
            open[key] = function () { return null }
            patched++
          } catch { /* */ }
        }
      }

      return true
    }
  })
})()
