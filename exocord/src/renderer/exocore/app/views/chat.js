/**
 * Virtualized-ish message list (window last N messages for first ship).
 */
(function () {
  'use strict'

  const WINDOW = 80
  let lastFetchChannel = null

  function formatTime (ts) {
    try {
      const d = new Date(ts)
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    } catch {
      return ''
    }
  }

  function render (messagesEl, state, api) {
    if (!messagesEl || !api) return
    const channelId = api.getSelectedChannelId()

    if (state.view === 'friends' && !channelId) {
      messagesEl.innerHTML = ''
      return
    }

    if (!channelId) {
      messagesEl.innerHTML = '<div class="exo-empty"><strong>Nothing selected</strong>Pick a conversation from Channels, or hit Friends.</div>'
      return
    }

    if (lastFetchChannel !== channelId) {
      lastFetchChannel = channelId
      api.jumpToPresent(channelId)
    }
    let msgs = api.getMessages(channelId)
    if (msgs.length > WINDOW) msgs = msgs.slice(-WINDOW)

    const ch = api.getChannel(channelId)
    const stickBottom = messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 80

    messagesEl.innerHTML = ''
    if (!msgs.length) {
      messagesEl.innerHTML = '<div class="exo-empty"><strong>No messages loaded</strong>Say something below, or wait for history.</div>'
      return
    }

    const frag = document.createDocumentFragment()
    for (const m of msgs) {
      if (!m || !m.id) continue
      const author = m.author || (m.authorId && api.getUser(m.authorId)) || {}
      const row = document.createElement('div')
      row.className = 'exo-msg'
      row.dataset.msgId = m.id

      const av = document.createElement('img')
      av.className = 'exo-msg-av'
      av.alt = ''
      const src = api.avatarUrl(author)
      if (src) av.src = src

      const body = document.createElement('div')
      const meta = document.createElement('div')
      meta.className = 'exo-msg-meta'
      const name = document.createElement('span')
      name.className = 'exo-msg-author'
      name.textContent = (api.displayName && api.displayName(author)) || author.globalName || author.username || author.nick || 'User'
      const time = document.createElement('span')
      time.className = 'exo-msg-time'
      time.textContent = formatTime(m.timestamp || m.editedTimestamp)
      meta.appendChild(name)
      meta.appendChild(time)

      const text = document.createElement('div')
      text.className = 'exo-msg-body'
      text.textContent = m.content || (m.attachments && m.attachments.length ? '[attachment]' : '')

      body.appendChild(meta)
      body.appendChild(text)
      row.appendChild(av)
      row.appendChild(body)
      frag.appendChild(row)
    }
    messagesEl.appendChild(frag)
    void ch
    if (stickBottom || state.forceScroll) {
      messagesEl.scrollTop = messagesEl.scrollHeight
      state.forceScroll = false
    }
  }

  window.ExocordViewChat = Object.freeze({ render })
})()
