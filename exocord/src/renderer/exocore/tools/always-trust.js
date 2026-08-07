/**
 * alwaysTrust — skip Discord's "are you sure?" external link / domain prompts.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'alwaysTrust',
    title: 'Always Trust Links',
    tier: 'default',
    defaultOn: true,
    apply () {
      const trust = findByProps('isTrustedDomain') || findByProps('isDomainTrusted') ||
        findByProps('checkTrustedDomain')
      if (!trust) return false
      let n = 0
      for (const key of Object.keys(trust)) {
        if (typeof trust[key] !== 'function') continue
        if (!/trust|domain|unsafe|masked/i.test(key)) continue
        try {
          trust[key] = function () { return true }
          n++
        } catch { /* */ }
      }
      return n > 0
    }
  })
})()
