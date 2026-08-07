/**
 * characterCounter — show live character count near the message composer.
 * Do NOT observe the whole document tree — that freezes Discord's React mount.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  const LIMIT = 2000

  function ensureCounter () {
    let el = document.getElementById('exocord-char-counter')
    if (el) return el
    el = document.createElement('div')
    el.id = 'exocord-char-counter'
    el.style.cssText = 'position:fixed;bottom:8px;right:12px;font:11px/1.2 var(--font-primary);color:var(--text-muted);pointer-events:none;z-index:9999;opacity:0.85;'
    document.documentElement.appendChild(el)
    return el
  }

  function findComposer () {
    return document.querySelector('[class*="channelTextArea"] [role="textbox"]') ||
      document.querySelector('[class*="slateTextArea"] [role="textbox"]') ||
      document.querySelector('[data-slate-editor="true"]')
  }

  ExocordTools.register({
    id: 'characterCounter',
    title: 'Character Counter',
    tier: 'default',
    defaultOn: true,
    apply () {
      const counter = ensureCounter()
      let timer = 0

      function update () {
        const box = findComposer()
        const len = box ? (box.textContent || '').length : 0
        counter.textContent = len ? (len + ' / ' + LIMIT) : ''
        counter.style.color = len > LIMIT * 0.9 ? 'var(--status-danger)' : 'var(--text-muted)'
      }

      function schedule () {
        if (timer) return
        timer = setTimeout(() => { timer = 0; update() }, 120)
      }

      document.addEventListener('input', schedule, true)
      document.addEventListener('keyup', schedule, true)
      document.addEventListener('focusin', schedule, true)
      setInterval(update, 2000)
      update()
      return true
    }
  })
})()
