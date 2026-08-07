/**
 * noReplyMention — replies default without @mention ping.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'noReplyMention',
    title: 'No Reply Mention',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0

      const reply = findByProps('replyToMessage', 'startReply') ||
        findByProps('setReplyingMessageId', 'createPendingReply')
      if (reply) {
        for (const key of Object.keys(reply)) {
          if (typeof reply[key] !== 'function') continue
          if (!/reply|mention|ping/i.test(key)) continue
          try {
            const orig = reply[key].bind(reply)
            reply[key] = function () {
              const args = Array.from(arguments)
              const last = args[args.length - 1]
              if (last && typeof last === 'object') {
                args[args.length - 1] = Object.assign({}, last, { shouldMention: false })
              } else {
                args.push({ shouldMention: false })
              }
              return orig.apply(this, args)
            }
            patched++
          } catch { /* */ }
        }
      }

      const composer = findByProps('setShouldMention', 'toggleShouldMention')
      if (composer && typeof composer.setShouldMention === 'function') {
        try {
          composer.setShouldMention = function () { return false }
          patched++
        } catch { /* */ }
      }

      return patched > 0
    }
  })
})()
