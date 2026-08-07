/**
 * Exocord settings overlay (tools list + close). Replaces settings-shell framing.
 */
(function () {
  'use strict'

  function open () {
    document.documentElement.setAttribute('data-exocord-settings-open', '1')
    refresh()
  }

  function close () {
    document.documentElement.removeAttribute('data-exocord-settings-open')
  }

  function toggle () {
    if (document.documentElement.getAttribute('data-exocord-settings-open') === '1') close()
    else open()
  }

  function refresh () {
    const list = document.querySelector('#exocord-root [data-exo-tools]')
    if (!list) return
    list.innerHTML = ''
    const tools = window.ExocordTools && typeof ExocordTools.list === 'function'
      ? ExocordTools.list()
      : []
    if (!tools.length) {
      const li = document.createElement('li')
      li.className = 'exo-tool-row'
      li.textContent = 'No tools registered.'
      list.appendChild(li)
      return
    }
    for (const tool of tools) {
      const li = document.createElement('li')
      li.className = 'exo-tool-row'
      const meta = document.createElement('div')
      const strong = document.createElement('strong')
      strong.textContent = tool.title || tool.id
      const tier = document.createElement('div')
      tier.style.color = 'var(--exo-muted)'
      tier.style.fontSize = '12px'
      tier.textContent = tool.tier + (tool.failed ? ' — ' + tool.failed : '')
      meta.appendChild(strong)
      meta.appendChild(tier)
      const dot = document.createElement('span')
      dot.className = 'exo-dot' + (tool.applied ? ' on' : '')
      li.appendChild(meta)
      li.appendChild(dot)
      list.appendChild(li)
    }
  }

  function bind (root) {
    if (!root) return
    const closeBtn = root.querySelector('[data-exo-settings-close]')
    if (closeBtn) closeBtn.addEventListener('click', close)
  }

  window.ExocordViewSettings = Object.freeze({ open, close, toggle, refresh, bind })
})()
