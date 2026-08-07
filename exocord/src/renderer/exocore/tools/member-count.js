/**
 * memberCount — show guild member count in the channel header when stores exist.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findStore (name) {
    if (!window.Exocore || typeof window.Exocore.findStore !== 'function') return null
    return window.Exocore.findStore(name)
  }

  function ensureBadge () {
    let el = document.getElementById('exocord-member-count')
    if (el) return el
    el = document.createElement('span')
    el.id = 'exocord-member-count'
    el.style.cssText = 'margin-left:8px;font:12px var(--font-primary);color:var(--text-muted);opacity:0.85;'
    return el
  }

  function mount () {
    const header = document.querySelector('[class*="title"] [class*="children"]') ||
      document.querySelector('header[class*="container"] h1') ||
      document.querySelector('[class*="chatHeader"]')
    if (!header) return null
    const badge = ensureBadge()
    if (!badge.parentElement) header.appendChild(badge)
    return badge
  }

  ExocordTools.register({
    id: 'memberCount',
    title: 'Member Count',
    tier: 'default',
    defaultOn: true,
    apply () {
      const guildStore = findStore('GuildStore')
      const selected = findStore('SelectedGuildStore')
      if (!guildStore || !selected) return false

      function refresh () {
        const badge = mount()
        if (!badge) return
        try {
          const guildId = selected.getGuildId && selected.getGuildId()
          const guild = guildId && guildStore.getGuild ? guildStore.getGuild(guildId) : null
          const count = guild && (guild.memberCount || guild.approximate_member_count)
          badge.textContent = count ? count.toLocaleString() + ' members' : ''
        } catch {
          badge.textContent = ''
        }
      }

      refresh()
      setInterval(refresh, 5000)
      return true
    }
  })
})()
