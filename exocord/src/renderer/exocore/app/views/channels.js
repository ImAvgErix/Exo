/**
 * Channel / DM list panel — real names + avatars
 */
(function () {
  'use strict'

  function channelLabel (ch) {
    if (!ch) return 'channel'
    if (ch.type === 2 || ch.type === 13) return (ch.name || 'voice')
    if (ch.type === 4) return ch.name || 'category'
    return (ch.name || 'channel')
  }

  function renderDmRow (ch, selectedCh, state, api) {
    const btn = document.createElement('button')
    btn.type = 'button'
    btn.className = 'exo-channel exo-dm'
    btn.setAttribute('aria-current', selectedCh === ch.id ? 'true' : 'false')

    const peer = api.dmAvatarUser(ch)
    const av = document.createElement('img')
    av.className = 'exo-channel-av'
    av.alt = ''
    const src = api.avatarUrl(peer)
    if (src) av.src = src
    else av.classList.add('is-empty')

    const meta = document.createElement('span')
    meta.className = 'exo-channel-meta'
    const title = document.createElement('span')
    title.className = 'exo-channel-name'
    title.textContent = api.dmLabel(ch)
    const sub = document.createElement('span')
    sub.className = 'exo-channel-sub'
    sub.textContent = ch.type === 3 ? 'Group' : 'Direct message'
    meta.appendChild(title)
    meta.appendChild(sub)

    btn.appendChild(av)
    btn.appendChild(meta)
    btn.addEventListener('click', () => {
      api.selectChannel(ch.id)
      state.view = 'dm'
      state.onNavigate && state.onNavigate()
    })
    return btn
  }

  function render (panelList, panelHead, state, api) {
    if (!panelList || !api) return
    const guildId = api.getSelectedGuildId()
    const selectedCh = api.getSelectedChannelId()
    panelList.innerHTML = ''

    if (state.view === 'friends' || state.view === 'dm' || !guildId) {
      if (panelHead) panelHead.textContent = 'Direct messages'
      const dms = api.getPrivateChannels()
      if (!dms.length) {
        const empty = document.createElement('div')
        empty.className = 'exo-empty exo-empty-panel'
        empty.innerHTML = '<strong>No DMs</strong>Message a friend to start.'
        panelList.appendChild(empty)
        return
      }
      for (const ch of dms) {
        if (!ch || !ch.id || ch.type === 4) continue
        panelList.appendChild(renderDmRow(ch, selectedCh, state, api))
      }
      return
    }

    const guilds = api.getGuilds()
    const guild = guilds.find(g => g && g.id === guildId)
    if (panelHead) panelHead.textContent = (guild && guild.name) || 'Channels'

    const channels = api.getChannels(guildId)
    for (const ch of channels) {
      if (!ch || !ch.id) continue
      if (ch.type === 4) {
        const cat = document.createElement('div')
        cat.className = 'exo-cat'
        cat.textContent = ch.name || 'Category'
        panelList.appendChild(cat)
        continue
      }
      if (ch.type !== 0 && ch.type !== 5 && ch.type !== 2 && ch.type !== 13) continue
      const btn = document.createElement('button')
      btn.type = 'button'
      btn.className = 'exo-channel'
      btn.setAttribute('aria-current', selectedCh === ch.id ? 'true' : 'false')
      const hash = document.createElement('span')
      hash.className = 'exo-channel-hash'
      hash.textContent = ch.type === 2 || ch.type === 13 ? '◈' : '#'
      const meta = document.createElement('span')
      meta.className = 'exo-channel-meta'
      const title = document.createElement('span')
      title.className = 'exo-channel-name'
      title.textContent = ch.name || 'channel'
      meta.appendChild(title)
      btn.appendChild(hash)
      btn.appendChild(meta)
      btn.title = channelLabel(ch)
      btn.addEventListener('click', () => {
        if (ch.type === 2 || ch.type === 13) {
          const V = window.ExocordBridgeVoice
          if (!(V && V.joinVoice(ch.id))) {
            const voice = api.findByProps('selectVoiceChannel')
            try {
              if (voice && typeof voice.selectVoiceChannel === 'function') voice.selectVoiceChannel(ch.id)
            } catch { /* */ }
          }
        } else {
          api.selectChannel(ch.id)
        }
        state.view = 'guild'
        state.onNavigate && state.onNavigate()
      })
      panelList.appendChild(btn)
    }
  }

  window.ExocordViewChannels = Object.freeze({ render })
})()
