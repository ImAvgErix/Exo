/**
 * quickReply — Ctrl+ArrowUp/Down cycles recent messages for quick reply.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'quickReply',
    title: 'Quick Reply',
    tier: 'default',
    defaultOn: true,
    apply () {
      const actions = findByProps('replyToMessage', 'startReply') ||
        findByProps('setReplyingMessageId')
      if (!actions) return false

      const reply = actions.replyToMessage || actions.startReply
      if (typeof reply !== 'function') return false

      let index = 0
      let messages = []

      function refresh () {
        const list = document.querySelector('[class*="messagesWrapper"] [class*="messageListItem"]')
        if (!list) return
        messages = Array.from(
          document.querySelectorAll('[class*="messagesWrapper"] [id^="chat-messages-"]')
        ).slice(-20)
      }

      window.addEventListener('keydown', event => {
        if (!event.ctrlKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return
        const tag = document.activeElement && document.activeElement.tagName
        if (tag !== 'BODY' && tag !== 'DIV') return
        event.preventDefault()
        refresh()
        if (!messages.length) return
        if (event.key === 'ArrowUp') index = Math.min(messages.length - 1, index + 1)
        else index = Math.max(0, index - 1)
        const el = messages[messages.length - 1 - index]
        const id = el && el.id && el.id.split('-').pop()
        if (!id) return
        try {
          reply({ messageId: id, channelId: null })
        } catch { /* */ }
      }, true)

      return true
    }
  })
})()
