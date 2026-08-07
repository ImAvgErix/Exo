/**
 * imageZoom — Ctrl+wheel or click to zoom images in chat (lightweight overlay).
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  let scale = 1
  let overlay = null

  function ensureOverlay () {
    if (overlay) return overlay
    overlay = document.createElement('div')
    overlay.id = 'exocord-image-zoom'
    overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,0.85);display:none;align-items:center;justify-content:center;z-index:100000;cursor:zoom-out;'
    overlay.innerHTML = '<img style="max-width:95vw;max-height:95vh;transform-origin:center;" />'
    overlay.addEventListener('click', () => { overlay.style.display = 'none'; scale = 1 })
    document.documentElement.appendChild(overlay)
    return overlay
  }

  function isChatImage (el) {
    return el && el.tagName === 'IMG' && el.closest('[class*="messageContent"], [class*="embedImage"], [class*="imageContent"]')
  }

  ExocordTools.register({
    id: 'imageZoom',
    title: 'Image Zoom',
    tier: 'default',
    defaultOn: true,
    apply () {
      document.addEventListener('click', event => {
        const img = event.target
        if (!isChatImage(img)) return
        if (event.ctrlKey || event.metaKey) {
          event.preventDefault()
          const o = ensureOverlay()
          const big = o.querySelector('img')
          big.src = img.src
          big.style.transform = 'scale(1)'
          o.style.display = 'flex'
        }
      }, true)

      document.addEventListener('wheel', event => {
        if (!event.ctrlKey && !event.metaKey) return
        const img = event.target
        if (!isChatImage(img)) return
        event.preventDefault()
        scale = Math.min(4, Math.max(0.5, scale + (event.deltaY < 0 ? 0.1 : -0.1)))
        img.style.transform = 'scale(' + scale + ')'
        img.style.transformOrigin = 'center'
      }, { passive: false, capture: true })

      return true
    }
  })
})()
