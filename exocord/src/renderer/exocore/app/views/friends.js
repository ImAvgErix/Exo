/**
 * Friends / home stage
 */
(function () {
  'use strict'

  function render (messagesEl, stageTitle, stageSub, state, api) {
    if (!messagesEl) return false
    const onHome = state.view === 'friends' || (!api.getSelectedGuildId() && !api.getSelectedChannelId())
    if (!onHome) return false
    if (api.getSelectedChannelId()) return false

    const friends = api.getFriends()
    if (stageTitle) stageTitle.textContent = 'Friends'
    if (stageSub) stageSub.textContent = friends.length + ' connected'

    messagesEl.innerHTML = ''

    const hero = document.createElement('div')
    hero.className = 'exo-home-hero'
    hero.innerHTML = '<div class="exo-home-kicker">HOME</div><h2>Who\'s around</h2><p>Open a DM from the drawer, or start a chat from your friends list.</p>'
    messagesEl.appendChild(hero)

    if (!friends.length) {
      const empty = document.createElement('div')
      empty.className = 'exo-empty'
      empty.innerHTML = '<strong>No friends synced</strong>Discord relationships will land here once the gateway settles.'
      messagesEl.appendChild(empty)
      return true
    }

    const grid = document.createElement('div')
    grid.className = 'exo-friend-grid'
    for (const u of friends) {
      const btn = document.createElement('button')
      btn.type = 'button'
      btn.className = 'exo-friend'
      const img = document.createElement('img')
      img.className = 'exo-msg-av'
      img.alt = ''
      const src = api.avatarUrl(u)
      if (src) img.src = src
      const meta = document.createElement('span')
      meta.className = 'exo-friend-meta'
      const name = document.createElement('span')
      name.className = 'exo-friend-name'
      name.textContent = api.displayName(u)
      const handle = document.createElement('span')
      handle.className = 'exo-friend-handle'
      handle.textContent = u.username ? '@' + u.username : ''
      meta.appendChild(name)
      meta.appendChild(handle)
      btn.appendChild(img)
      btn.appendChild(meta)
      btn.addEventListener('click', () => {
        const dm = api.findByProps('openPrivateChannel') || api.findByProps('ensurePrivateChannel')
        try {
          if (dm && typeof dm.openPrivateChannel === 'function') {
            dm.openPrivateChannel(u.id)
          } else if (dm && typeof dm.ensurePrivateChannel === 'function') {
            const p = dm.ensurePrivateChannel(u.id)
            if (p && typeof p.then === 'function') {
              p.then(id => { if (id) api.selectChannel(id) })
            }
          }
        } catch { /* */ }
        state.view = 'dm'
        state.onNavigate && state.onNavigate()
      })
      grid.appendChild(btn)
    }
    messagesEl.appendChild(grid)
    return true
  }

  window.ExocordViewFriends = Object.freeze({ render })
})()
