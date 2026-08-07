/**
 * Guild rail view
 */
(function () {
  'use strict'

  function render (railEl, state, api) {
    if (!railEl || !api) return
    const guilds = api.getGuilds()
    const selected = api.getSelectedGuildId()
    const home = state.view === 'friends' || !selected

    railEl.innerHTML = ''

    const homeBtn = document.createElement('button')
    homeBtn.type = 'button'
    homeBtn.className = 'exo-rail-home'
    homeBtn.textContent = '⌂'
    homeBtn.title = 'Home / Friends'
    homeBtn.setAttribute('aria-current', home ? 'true' : 'false')
    homeBtn.addEventListener('click', () => {
      api.selectHome()
      state.view = 'friends'
      state.onNavigate && state.onNavigate()
    })
    railEl.appendChild(homeBtn)

    for (const g of guilds) {
      if (!g || !g.id) continue
      const btn = document.createElement('button')
      btn.type = 'button'
      btn.className = 'exo-rail-guild'
      btn.title = g.name || 'Server'
      btn.setAttribute('aria-current', !home && selected === g.id ? 'true' : 'false')
      const icon = api.guildIconUrl(g)
      if (icon) {
        const img = document.createElement('img')
        img.src = icon
        img.alt = ''
        btn.appendChild(img)
      } else {
        btn.textContent = (g.name || '?').slice(0, 1).toUpperCase()
      }
      btn.addEventListener('click', () => {
        api.selectGuild(g.id)
        state.view = 'guild'
        state.onNavigate && state.onNavigate()
      })
      railEl.appendChild(btn)
    }

    const brand = document.createElement('div')
    brand.className = 'exo-rail-brand'
    brand.textContent = 'EXOCORD'
    railEl.appendChild(brand)
  }

  window.ExocordViewGuilds = Object.freeze({ render })
})()
