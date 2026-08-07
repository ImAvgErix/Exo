/**
 * noTypingAnimation — kill typing-indicator dot animations (visual only).
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function ensureCss () {
    if (document.getElementById('exocord-no-typing-animation')) return
    const style = document.createElement('style')
    style.id = 'exocord-no-typing-animation'
    style.textContent = `
      [class*="typing"],
      [class*="typingDots"],
      [class*="isTyping"] {
        animation: none !important;
        transition: none !important;
      }
      [class*="typing"] span,
      [class*="typingDots"] span {
        animation: none !important;
        opacity: 1 !important;
        transform: none !important;
      }
    `
    document.documentElement.appendChild(style)
  }

  ExocordTools.register({
    id: 'noTypingAnimation',
    title: 'No Typing Animation',
    tier: 'default',
    defaultOn: true,
    apply () {
      ensureCss()
      return !!document.documentElement
    }
  })
})()
