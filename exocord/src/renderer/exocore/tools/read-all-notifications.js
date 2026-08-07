/**
 * readAllNotifications — expose mark-all-read helper on window.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function findStore (name) {
    if (!window.Exocore || typeof window.Exocore.findStore !== 'function') return null
    return window.Exocore.findStore(name)
  }

  ExocordTools.register({
    id: 'readAllNotifications',
    title: 'Read All Notifications',
    tier: 'default',
    defaultOn: true,
    apply () {
      const ack = findByProps('ack', 'ackChannel') ||
        findByProps('markAllRead', 'ackChannels')
      const notifStore = findStore('RecentMentionsStore') ||
        findStore('UnreadStore')

      function markAll () {
        let n = 0
        if (ack && typeof ack.ack === 'function') {
          try { ack.ack(); n++ } catch { /* */ }
        }
        if (ack && typeof ack.markAllRead === 'function') {
          try { ack.markAllRead(); n++ } catch { /* */ }
        }
        if (notifStore && typeof notifStore.getMentions === 'function') {
          try {
            const mentions = notifStore.getMentions()
            if (Array.isArray(mentions)) n += mentions.length
          } catch { /* */ }
        }
        return n > 0 || !!ack
      }

      window.ExocordReadAll = Object.freeze({ markAll, run: markAll })
      return !!ack || !!notifStore
    }
  })
})()
