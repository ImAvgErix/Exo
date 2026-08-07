/**
 * hideChatButtons — remove gift / apps / sticker buttons from the chat bar.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function ensureCss () {
    if (document.getElementById('exocord-hide-chat-buttons')) return
    const style = document.createElement('style')
    style.id = 'exocord-hide-chat-buttons'
    style.textContent = `
      button[aria-label*="Gift" i],
      button[aria-label*="Send a gift" i],
      button[aria-label*="Sticker" i],
      button[aria-label*="Apps" i],
      button[aria-label*="Open App" i],
      [class*="giftButton"],
      [class*="stickerButton"],
      [class*="appsButton"],
      [class*="applicationCommandButton"] {
        display: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'hideChatButtons',
    title: 'Hide Chat Buttons',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      let patched = 0
      const bar = findByProps('GiftButton', 'StickerPicker') ||
        findByProps('openStickerPicker', 'openGiftModal')
      if (bar) {
        for (const key of Object.keys(bar)) {
          if (typeof bar[key] !== 'function') continue
          if (!/gift|sticker|app|command|picker/i.test(key)) continue
          try {
            bar[key] = function () { return null }
            patched++
          } catch { /* */ }
        }
      }
      return true
    }
  })
})()
