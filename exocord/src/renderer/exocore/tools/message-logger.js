/**
 * messageLogger — append-only local delete/edit log with age-fade UI.
 * Never auto-purges; user can clear via ExocordTools or Tools pane later.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  const KEY = 'exocord.messageLog.v1'
  const MAX_ENTRIES = 2000

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function load () {
    try {
      const raw = localStorage.getItem(KEY)
      if (!raw) return []
      const parsed = JSON.parse(raw)
      return Array.isArray(parsed) ? parsed : []
    } catch {
      return []
    }
  }

  function save (entries) {
    try {
      localStorage.setItem(KEY, JSON.stringify(entries.slice(-MAX_ENTRIES)))
    } catch { /* quota */ }
  }

  function append (entry) {
    const entries = load()
    entries.push(Object.assign({ at: Date.now() }, entry))
    save(entries)
  }

  function ensureFadeStyle () {
    if (document.getElementById('exocord-message-logger-fade')) return
    const style = document.createElement('style')
    style.id = 'exocord-message-logger-fade'
    style.textContent = `
      [data-exocord-logged="1"] {
        opacity: calc(0.35 + 0.65 * var(--exo-log-fresh, 1));
        transition: opacity 400ms ease;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'messageLogger',
    title: 'Message Logger',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureFadeStyle()
      const dispatcher = findByProps('dispatch', 'subscribe') || findByProps('dirtyDispatch')
      if (!dispatcher || typeof dispatcher.subscribe !== 'function') {
        // Fallback: wrap MessageStore actions if present.
        const msgs = findByProps('deleteMessage', 'editMessage')
        if (!msgs) return false
        if (typeof msgs.deleteMessage === 'function') {
          const del = msgs.deleteMessage.bind(msgs)
          msgs.deleteMessage = function (channelId, messageId) {
            append({ type: 'delete', channelId, messageId })
            return del(channelId, messageId)
          }
        }
        return true
      }

      const handler = (event) => {
        try {
          if (!event || !event.type) return
          if (event.type === 'MESSAGE_DELETE') {
            append({
              type: 'delete',
              channelId: event.channelId,
              messageId: event.id,
              guildId: event.guildId || null
            })
          } else if (event.type === 'MESSAGE_UPDATE' && event.message) {
            append({
              type: 'edit',
              channelId: event.message.channel_id,
              messageId: event.message.id,
              content: event.message.content
            })
          } else if (event.type === 'MESSAGE_DELETE_BULK' && Array.isArray(event.ids)) {
            for (const id of event.ids) {
              append({ type: 'delete', channelId: event.channelId, messageId: id })
            }
          }
        } catch { /* never break Discord dispatch */ }
      }

      try {
        dispatcher.subscribe('MESSAGE_DELETE', handler)
        dispatcher.subscribe('MESSAGE_UPDATE', handler)
        dispatcher.subscribe('MESSAGE_DELETE_BULK', handler)
      } catch {
        if (typeof dispatcher.subscribe === 'function') {
          dispatcher.subscribe(handler)
        } else {
          return false
        }
      }

      window.ExocordMessageLog = Object.freeze({
        list: load,
        clear: () => { save([]); return true },
        append
      })
      return true
    }
  })
})()
