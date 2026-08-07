/**
 * Exocord data bridge — Discord protocol via webpack stores/actions only.
 * No stock Discord DOM scraping.
 */
(function () {
  'use strict'

  function E () {
    return window.Exocore || null
  }

  function findByProps () {
    const exo = E()
    if (!exo || typeof exo.findByProps !== 'function') return null
    return exo.findByProps.apply(null, arguments)
  }

  function findStore (name) {
    const exo = E()
    if (!exo || typeof exo.findStore !== 'function') return null
    return exo.findStore(name)
  }

  function getCurrentUser () {
    const store = findStore('UserStore')
    if (!store) return null
    try {
      if (typeof store.getCurrentUser === 'function') return store.getCurrentUser()
    } catch { /* */ }
    return null
  }

  function isLoggedIn () {
    const u = getCurrentUser()
    if (u && u.id) return true
    try {
      const auth = findByProps('getToken', 'getId') || findByProps('getToken')
      if (auth && typeof auth.getToken === 'function' && auth.getToken()) return true
    } catch { /* */ }
    return false
  }

  function getGuilds () {
    const store = findStore('GuildStore')
    if (!store) return []
    try {
      const map = typeof store.getGuilds === 'function' ? store.getGuilds() : null
      if (map && typeof map === 'object') {
        return Object.keys(map).map(id => map[id]).filter(Boolean)
      }
      if (typeof store.getGuildIds === 'function') {
        return store.getGuildIds().map(id => store.getGuild(id)).filter(Boolean)
      }
    } catch { /* */ }
    return []
  }

  function getSelectedGuildId () {
    const sel = findStore('SelectedGuildStore')
    try {
      if (sel && typeof sel.getGuildId === 'function') return sel.getGuildId()
    } catch { /* */ }
    return null
  }

  function selectGuild (guildId) {
    // Router first. selectGuild's signature has changed shape between builds
    // (id, then {guildId}), and a URL transition is the one path that has meant
    // the same thing on every one of them.
    try {
      const router = findByProps('transitionTo')
      if (router && typeof router.transitionTo === 'function') {
        router.transitionTo('/channels/' + guildId)
        return true
      }
    } catch { /* */ }
    const nav = findByProps('selectGuild') || findByProps('transitionToGuildSync')
    if (!nav) return false
    try {
      if (typeof nav.selectGuild === 'function') {
        try { nav.selectGuild(guildId) } catch { nav.selectGuild({ guildId }) }
        return true
      }
      if (typeof nav.transitionToGuildSync === 'function') {
        nav.transitionToGuildSync(guildId)
        return true
      }
    } catch { /* */ }
    return false
  }

  /**
   * Members of a guild, for the member list.
   *
   * GuildMemberStore only holds the members this client has actually been sent -
   * Discord streams them lazily and never sends the full roster of a large guild
   * - so this is "who we know about", not "everyone". The count comes from a
   * separate store that does know the real total, which is why the two are
   * returned separately rather than pretending the list is complete.
   */
  function getGuildMemberIds (guildId) {
    if (!guildId) return []
    try {
      const store = findStore('GuildMemberStore')
      if (store && typeof store.getMemberIds === 'function') {
        return store.getMemberIds(guildId) || []
      }
      if (store && typeof store.getMembers === 'function') {
        return (store.getMembers(guildId) || []).map(m => m && m.userId).filter(Boolean)
      }
    } catch { /* */ }
    return []
  }

  function getGuildMemberCount (guildId) {
    if (!guildId) return 0
    try {
      const store = findStore('GuildMemberCountStore')
      if (store && typeof store.getMemberCount === 'function') {
        return store.getMemberCount(guildId) || 0
      }
    } catch { /* */ }
    return getGuildMemberIds(guildId).length
  }

  /** Per-guild display name and role colour, when the client has the member. */
  function getGuildMember (guildId, userId) {
    if (!guildId || !userId) return null
    try {
      const store = findStore('GuildMemberStore')
      if (store && typeof store.getMember === 'function') return store.getMember(guildId, userId)
    } catch { /* */ }
    return null
  }

  function selectHome () {
    const nav = findByProps('selectGuild') || findByProps('transitionTo')
    try {
      if (nav && typeof nav.selectGuild === 'function') {
        nav.selectGuild(null)
        return true
      }
    } catch { /* */ }
    try {
      const router = findByProps('transitionTo')
      if (router && typeof router.transitionTo === 'function') {
        router.transitionTo('/channels/@me')
        return true
      }
    } catch { /* */ }
    return false
  }

  function getChannels (guildId) {
    const store = findStore('ChannelStore')
    const guild = findStore('GuildChannelStore') || findStore('SortedGuildChannelStore')
    const out = []
    try {
      if (guildId && guild && typeof guild.getChannels === 'function') {
        const packed = guild.getChannels(guildId)
        const lists = []
        if (Array.isArray(packed)) {
          lists.push(...packed)
        } else if (packed && typeof packed === 'object') {
          // Shape is { count, id, SELECTABLE: [...], VOCAL: [...], <type>: [...] }.
          // Categories live in a numeric-keyed bucket, so take every array value
          // rather than the two named ones - reading only SELECTABLE/VOCAL drops
          // categories and the channel list renders flat.
          for (const value of Object.values(packed)) {
            if (Array.isArray(value)) lists.push(...value)
          }
        }
        const seen = new Set()
        for (const item of lists) {
          const ch = item && (item.channel || item)
          // guild_id/type present distinguishes a channel from the guild-level
          // wrapper, which also carries an id.
          if (!ch || !ch.id || seen.has(ch.id)) continue
          if (typeof ch.type !== 'number') continue
          seen.add(ch.id)
          out.push(ch)
        }
        if (out.length) return out
      }
      if (store && typeof store.getMutableGuildChannelsForGuild === 'function') {
        const map = store.getMutableGuildChannelsForGuild(guildId)
        if (map) return Object.values(map).filter(Boolean)
      }
      if (store && typeof store.getGuildChannels === 'function') {
        return store.getGuildChannels(guildId) || []
      }
    } catch { /* */ }
    return out
  }

  function getPrivateChannels () {
    const store = findStore('ChannelStore') || findStore('PrivateChannelStore')
    try {
      if (store && typeof store.getSortedPrivateChannels === 'function') {
        return store.getSortedPrivateChannels() || []
      }
      if (store && typeof store.getPrivateChannels === 'function') {
        const map = store.getPrivateChannels()
        return map ? Object.values(map) : []
      }
    } catch { /* */ }
    return []
  }

  function getSelectedChannelId () {
    const sel = findStore('SelectedChannelStore')
    try {
      if (sel && typeof sel.getChannelId === 'function') return sel.getChannelId()
    } catch { /* */ }
    return null
  }

  function selectChannel (channelId) {
    try {
      const router = findByProps('transitionTo')
      const ch = getChannel(channelId)
      const guildId = (ch && ch.guild_id) || (ch && ch.guildId) || getSelectedGuildId() || '@me'
      if (router && typeof router.transitionTo === 'function') {
        router.transitionTo('/channels/' + guildId + '/' + channelId)
        return true
      }
    } catch { /* */ }
    const nav = findByProps('selectChannel') || findByProps('selectVoiceChannel')
    try {
      if (nav && typeof nav.selectChannel === 'function') {
        // Newer Discord builds expect an object; older expect (channelId)
        try {
          nav.selectChannel({
            guildId: getSelectedGuildId() || null,
            channelId,
            channelType: (getChannel(channelId) && getChannel(channelId).type) || undefined
          })
          return true
        } catch {
          nav.selectChannel(channelId)
          return true
        }
      }
    } catch { /* */ }
    return false
  }

  function getMessages (channelId) {
    const store = findStore('MessageStore')
    if (!store || !channelId) return []
    try {
      if (typeof store.getMessages === 'function') {
        const bucket = store.getMessages(channelId)
        if (!bucket) return []
        if (typeof bucket.toArray === 'function') return bucket.toArray()
        if (Array.isArray(bucket._array)) return bucket._array.slice()
        if (Array.isArray(bucket)) return bucket.slice()
        if (bucket && typeof bucket[Symbol.iterator] === 'function') return Array.from(bucket)
      }
    } catch { /* */ }
    return []
  }

  function jumpToPresent (channelId) {
    const fetch = findByProps('fetchMessages') || findByProps('jumpToPresent')
    try {
      if (fetch && typeof fetch.jumpToPresent === 'function') {
        fetch.jumpToPresent(channelId)
        return true
      }
      if (fetch && typeof fetch.fetchMessages === 'function') {
        fetch.fetchMessages({ channelId })
        return true
      }
    } catch { /* */ }
    return false
  }

  function sendMessage (channelId, content) {
    if (!channelId || !content || !String(content).trim()) return false
    const actions = findByProps('sendMessage') || findByProps('sendBotMessage')
    if (!actions || typeof actions.sendMessage !== 'function') return false
    try {
      const result = actions.sendMessage(channelId, {
        content: String(content).trim(),
        tts: false,
        invalidEmojis: [],
        validNonShortcutEmojis: []
      }, true /* wait for nonce / return promise depending on build */)
      if (result && typeof result.catch === 'function') result.catch(() => {})
      return true
    } catch {
      try {
        actions.sendMessage(channelId, String(content).trim())
        return true
      } catch {
        return false
      }
    }
  }

  function getFriends () {
    const rel = findStore('RelationshipStore')
    const users = findStore('UserStore')
    const out = []
    try {
      const ids = rel && typeof rel.getFriendIDs === 'function'
        ? rel.getFriendIDs()
        : (rel && typeof rel.getFriendIds === 'function' ? rel.getFriendIds() : [])
      for (const id of ids || []) {
        const u = users && typeof users.getUser === 'function' ? users.getUser(id) : null
        if (u) out.push(u)
      }
    } catch { /* */ }
    return out
  }

  function getUser (id) {
    const store = findStore('UserStore')
    try {
      if (store && typeof store.getUser === 'function') return store.getUser(id)
    } catch { /* */ }
    return null
  }

  function getChannel (id) {
    const store = findStore('ChannelStore')
    try {
      if (store && typeof store.getChannel === 'function') return store.getChannel(id)
    } catch { /* */ }
    return null
  }

  function coerceIdList (raw) {
    if (raw == null) return []
    try {
      if (typeof raw.toArray === 'function') raw = raw.toArray()
      else if (typeof raw.toJS === 'function') raw = raw.toJS()
      else if (!Array.isArray(raw) && typeof raw[Symbol.iterator] === 'function') raw = Array.from(raw)
    } catch { /* */ }
    return Array.isArray(raw) ? raw : []
  }

  function getChannelRecipients (ch) {
    if (!ch) return []
    const me = getCurrentUser()
    const meId = me && me.id
    const out = []
    const seen = new Set()

    function pushUser (u) {
      if (!u || !u.id || u.id === meId || seen.has(u.id)) return
      seen.add(u.id)
      out.push(u)
    }

    function pushId (id) {
      if (id == null) return
      if (typeof id === 'object' && id.id) {
        pushUser(id.username || id.globalName || id.avatar !== undefined ? id : (getUser(id.id) || id))
        return
      }
      pushUser(getUser(String(id)))
    }

    try {
      // Live Discord: rawRecipients holds user objects; recipients is id list;
      // getRecipientId() works for type-1 DMs even when UserStore is cold.
      coerceIdList(ch.rawRecipients).forEach(pushId)
      if (typeof ch.getRecipientId === 'function') pushId(ch.getRecipientId())
      if (typeof ch.getRecipientIds === 'function') coerceIdList(ch.getRecipientIds()).forEach(pushId)

      let raw = null
      if (typeof ch.get === 'function') {
        raw = ch.get('rawRecipients') || ch.get('recipients') || ch.get('recipientIds') || ch.get('recipientId')
      }
      if (raw == null) raw = ch.recipients || ch.recipientIds || ch.recipientId
      if (raw != null && (typeof raw === 'string' || typeof raw === 'number')) pushId(raw)
      else coerceIdList(raw).forEach(pushId)

      if (!out.length && ch.id) {
        const msgs = getMessages(ch.id)
        for (let i = msgs.length - 1; i >= 0; i--) {
          const a = msgs[i] && msgs[i].author
          if (a && a.id && a.id !== meId) {
            pushUser(a)
            break
          }
        }
      }
    } catch { /* */ }
    return out
  }

  function dmLabel (ch) {
    if (!ch) return 'Message'
    if (ch.name) return ch.name
    const peers = getChannelRecipients(ch)
    if (ch.type === 3) {
      if (!peers.length) return 'Group DM'
      return peers.map(u => displayName(u)).join(', ')
    }
    if (peers[0]) return displayName(peers[0])
    return 'Message'
  }

  function dmAvatarUser (ch) {
    const peers = getChannelRecipients(ch)
    return peers[0] || null
  }

  function displayName (user) {
    if (!user) return 'Unknown'
    return user.globalName || user.displayName || user.username || 'Unknown'
  }

  function guildIconUrl (guild) {
    if (!guild || !guild.id || !guild.icon) return null
    return 'https://cdn.discordapp.com/icons/' + guild.id + '/' + guild.icon + '.webp?size=80'
  }

  function avatarUrl (user) {
    if (!user || !user.id) return null
    if (user.avatar) {
      return 'https://cdn.discordapp.com/avatars/' + user.id + '/' + user.avatar + '.webp?size=80'
    }
    const idx = (() => {
      try { return Number((BigInt(user.id) >> 22n) % 6n) } catch { return 0 }
    })()
    return 'https://cdn.discordapp.com/embed/avatars/' + idx + '.png'
  }

  // Mutable event registry: data modules (voice/presence/unreads) push their
  // dispatcher events in their IIFEs, which run before app boot subscribes.
  // Deliberately NOT frozen and NOT including SPEAKING (packet-rate cadence).
  if (!Array.isArray(window.ExocordBridgeEvents)) {
    window.ExocordBridgeEvents = [
      'CONNECTION_OPEN',
      'CHANNEL_SELECT',
      'GUILD_SELECT',
      'MESSAGE_CREATE',
      'MESSAGE_UPDATE',
      'MESSAGE_DELETE',
      'RELATIONSHIP_ADD',
      'LOAD_MESSAGES_SUCCESS'
    ]
  }

  function subscribe (callback, events) {
    // Flux Dispatcher if available; else poll.
    const dispatcher = findByProps('subscribe', 'dispatch', 'wait')
    const names = Array.isArray(events) && events.length
      ? events.slice()
      : window.ExocordBridgeEvents.slice()
    if (dispatcher && typeof dispatcher.subscribe === 'function') {
      const handler = (action) => { try { callback(action) } catch { /* */ } }
      try {
        for (const name of names) dispatcher.subscribe(name, handler)
        return () => {
          try {
            for (const name of names) dispatcher.unsubscribe(name, handler)
          } catch { /* */ }
        }
      } catch {
        try {
          dispatcher.subscribe(handler)
          return () => { try { dispatcher.unsubscribe(handler) } catch { /* */ } }
        } catch { /* fall through */ }
      }
    }
    const id = setInterval(callback, 1500)
    return () => clearInterval(id)
  }

  // ---- Message pagination -------------------------------------------------

  const olderInFlight = new Set()

  function oldestMessageId (channelId) {
    const msgs = getMessages(channelId)
    return msgs.length && msgs[0] && msgs[0].id ? msgs[0].id : null
  }

  function hasOlder (channelId) {
    const store = findStore('MessageStore')
    try {
      if (store && typeof store.getMessages === 'function') {
        const bucket = store.getMessages(channelId)
        if (bucket) {
          if (typeof bucket.hasMoreBefore === 'boolean') return bucket.hasMoreBefore
          if (typeof bucket._hasMoreBefore === 'boolean') return bucket._hasMoreBefore
        }
      }
    } catch { /* */ }
    // Unknown: report true — loadOlder simply no-ops at history start.
    return true
  }

  function loadOlder (channelId) {
    if (!channelId || olderInFlight.has(channelId)) return false
    const before = oldestMessageId(channelId)
    if (!before) return false
    const fetch = findByProps('fetchMessages')
    if (!fetch || typeof fetch.fetchMessages !== 'function') return false
    olderInFlight.add(channelId)
    const clear = () => olderInFlight.delete(channelId)
    setTimeout(clear, 5000)
    try {
      const result = fetch.fetchMessages({ channelId, limit: 50, before })
      if (result && typeof result.then === 'function') {
        result.then(clear, clear)
      }
      return true
    } catch {
      clear()
      return false
    }
  }

  // ---- Ordering -----------------------------------------------------------

  function getGuildsSorted () {
    const sorted = findStore('SortedGuildStore')
    const guilds = findStore('GuildStore')
    try {
      if (sorted && guilds && typeof guilds.getGuild === 'function') {
        if (typeof sorted.getFlattenedGuildIds === 'function') {
          const out = sorted.getFlattenedGuildIds()
            .map(id => guilds.getGuild(id))
            .filter(Boolean)
          if (out.length) return out
        }
        if (typeof sorted.getFlattenedGuilds === 'function') {
          const out = []
          for (const entry of sorted.getFlattenedGuilds() || []) {
            if (!entry) continue
            if (entry.id && entry.name) { out.push(entry); continue }
            const ids = entry.guildIds || (entry.guild && [entry.guild.id]) || []
            for (const id of ids) {
              const g = guilds.getGuild(id)
              if (g) out.push(g)
            }
          }
          if (out.length) return out
        }
      }
    } catch { /* */ }
    return getGuilds()
  }

  function getChannelsSorted (guildId) {
    const all = getChannels(guildId)
    if (!all.length) return all
    try {
      const pos = ch => (typeof ch.position === 'number' ? ch.position : 0)
      const categories = all.filter(ch => ch.type === 4).sort((a, b) => pos(a) - pos(b))
      const byParent = new Map()
      for (const ch of all) {
        if (ch.type === 4) continue
        const key = ch.parent_id || ch.parentId || ''
        if (!byParent.has(key)) byParent.set(key, [])
        byParent.get(key).push(ch)
      }
      const sortBucket = bucket => {
        const text = bucket.filter(ch => ch.type !== 2 && ch.type !== 13).sort((a, b) => pos(a) - pos(b))
        const voice = bucket.filter(ch => ch.type === 2 || ch.type === 13).sort((a, b) => pos(a) - pos(b))
        return text.concat(voice)
      }
      const out = []
      out.push(...sortBucket(byParent.get('') || []))
      for (const cat of categories) {
        out.push(cat)
        out.push(...sortBucket(byParent.get(cat.id) || []))
      }
      // Anything under an unknown category id (drifted data) still shows.
      const known = new Set(['', ...categories.map(c => c.id)])
      for (const [key, bucket] of byParent) {
        if (!known.has(key)) out.push(...sortBucket(bucket))
      }
      return out.length ? out : all
    } catch { /* */ }
    return all
  }

  window.ExocordBridgeData = Object.freeze({
    findByProps,
    findStore,
    getCurrentUser,
    isLoggedIn,
    getGuilds,
    getSelectedGuildId,
    selectGuild,
    selectHome,
    getChannels,
    getPrivateChannels,
    getSelectedChannelId,
    selectChannel,
    getMessages,
    jumpToPresent,
    loadOlder,
    hasOlder,
    getGuildsSorted,
    getChannelsSorted,
    sendMessage,
    getFriends,
    getGuildMemberIds,
    getGuildMemberCount,
    getGuildMember,
    getUser,
    getChannel,
    getChannelRecipients,
    dmLabel,
    dmAvatarUser,
    displayName,
    guildIconUrl,
    avatarUrl,
    subscribe
  })
})()
