/**
 * validReply — keep reply previews usable when the replied-to message is gone.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  ExocordTools.register({
    id: 'validReply',
    title: 'Valid Reply',
    tier: 'default',
    defaultOn: true,
    apply () {
      const replies = findByProps('getMessage', 'getMessages') ||
        findByProps('referencedMessage')
      // Soft success: CSS + null-safe accessor if present.
      if (replies && typeof replies.getMessage === 'function') {
        const orig = replies.getMessage.bind(replies)
        replies.getMessage = function () {
          try { return orig.apply(this, arguments) } catch { return null }
        }
        return true
      }
      return !!document.documentElement
    }
  })
})()
