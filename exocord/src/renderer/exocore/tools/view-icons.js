/**
 * viewIcons — click avatar / icon to open full CDN URL in browser.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function fullSize (url) {
    if (!url || typeof url !== 'string') return url
    return url.replace(/\?.*$/, '').replace(/\.webp(\?|$)/, '.png$1') + '?size=4096'
  }

  function findAvatarUrl (el) {
    if (!el) return null
    if (el.tagName === 'IMG' && el.src) return el.src
    const img = el.querySelector('img[src*="cdn.discordapp.com"], img[src*="media.discordapp.net"]')
    return img ? img.src : null
  }

  ExocordTools.register({
    id: 'viewIcons',
    title: 'View Icons',
    tier: 'default',
    defaultOn: true,
    apply () {
      document.addEventListener('click', event => {
        const target = event.target.closest('[class*="avatar"], [class*="icon"], img[src*="cdn.discordapp.com"]')
        if (!target) return
        if (!event.ctrlKey && !event.altKey) return
        const url = fullSize(findAvatarUrl(target))
        if (!url) return
        event.preventDefault()
        event.stopPropagation()
        try {
          if (window.DiscordNative && DiscordNative.window && DiscordNative.window.openExternal) {
            DiscordNative.window.openExternal(url)
          } else {
            window.open(url, '_blank', 'noopener')
          }
        } catch { /* */ }
      }, true)

      window.ExocordViewIcon = Object.freeze({ open: url => fullSize(url) })
      return true
    }
  })
})()
