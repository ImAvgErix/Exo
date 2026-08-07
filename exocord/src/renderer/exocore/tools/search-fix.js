/**
 * searchFix — work around Discord search index / empty-result stalls.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'searchFix',
    title: 'Search Fix',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0
      const search = findByProps('getSearchResultsQuery') ||
        findByProps('searchMessages') ||
        findByProps('fetchMessages', 'search')
      if (!search) return false

      for (const key of Object.keys(search)) {
        if (typeof search[key] !== 'function') continue
        if (!/error|fail|stall|timeout|retry/i.test(key)) continue
        try {
          const orig = search[key].bind(search)
          search[key] = function () {
            try { return orig.apply(this, arguments) } catch { return null }
          }
          patched++
        } catch { /* sealed */ }
      }

      // Some builds gate search behind a premium / indexing flag.
      for (const key of Object.keys(search)) {
        if (typeof search[key] !== 'function') continue
        if (!/isIndexed|canSearch|hasSearch|isSearchEnabled/i.test(key)) continue
        try {
          search[key] = function () { return true }
          patched++
        } catch { /* */ }
      }

      return patched > 0 || !!search
    }
  })
})()
