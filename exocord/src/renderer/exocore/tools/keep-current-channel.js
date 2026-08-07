/**
 * keepCurrentChannel — resist automatic channel jumps when switching guilds.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findStore (name) {
    if (!window.Exocore || typeof window.Exocore.findStore !== 'function') return null
    return window.Exocore.findStore(name)
  }

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'keepCurrentChannel',
    title: 'Keep Current Channel',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0
      const nav = findByProps('selectChannel', 'transitionTo') ||
        findByProps('transitionToGuildSync')
      const channelStore = findStore('SelectedChannelStore')

      if (nav && typeof nav.transitionToGuildSync === 'function') {
        try {
          const orig = nav.transitionToGuildSync.bind(nav)
          nav.transitionToGuildSync = function (guildId, channelId) {
            const keep = channelStore && typeof channelStore.getChannelId === 'function'
              ? channelStore.getChannelId()
              : null
            return orig(guildId, channelId || keep)
          }
          patched++
        } catch { /* */ }
      }

      if (nav && typeof nav.selectChannel === 'function') {
        try {
          const orig = nav.selectChannel.bind(nav)
          let lastGuild = null
          nav.selectChannel = function (guildId, channelId) {
            if (guildId !== lastGuild && channelId == null) {
              const keep = channelStore && channelStore.getLastSelectedChannelId
                ? channelStore.getLastSelectedChannelId(guildId)
                : null
              if (keep) channelId = keep
            }
            lastGuild = guildId
            return orig(guildId, channelId)
          }
          patched++
        } catch { /* */ }
      }

      return patched > 0
    }
  })
})()
