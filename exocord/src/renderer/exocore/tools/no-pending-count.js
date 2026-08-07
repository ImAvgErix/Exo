/**
 * noPendingCount — hide the friend-request pending badge count.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findStore (name) {
    if (!window.Exocore || typeof window.Exocore.findStore !== 'function') return null
    return window.Exocore.findStore(name)
  }

  function ensureCss () {
    if (document.getElementById('exocord-no-pending-count')) return
    const style = document.createElement('style')
    style.id = 'exocord-no-pending-count'
    style.textContent = `
      [class*="friendsBadge"],
      [class*="pendingCount"],
      [aria-label*="Pending" i] [class*="numberBadge"],
      nav[aria-label*="Friends" i] [class*="numberBadge"] {
        display: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'noPendingCount',
    title: 'No Pending Count',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      let patched = 0
      const rel = findStore('RelationshipStore')
      if (rel && typeof rel.getPendingCount === 'function') {
        try {
          rel.getPendingCount = function () { return 0 }
          patched++
        } catch { /* */ }
      }
      return patched > 0 || !!document.documentElement
    }
  })
})()
