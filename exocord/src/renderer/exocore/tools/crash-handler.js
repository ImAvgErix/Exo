/**
 * crashHandler — log renderer errors locally without forwarding to Discord Sentry.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function eachModule (fn) {
    if (!window.Exocore || typeof window.Exocore.eachModule !== 'function') return undefined
    return window.Exocore.eachModule(fn)
  }

  const PREFIX = '[Exocord crash]'

  ExocordTools.register({
    id: 'crashHandler',
    title: 'Crash Handler',
    tier: 'default',
    defaultOn: true,
    apply () {
      let closed = 0
      eachModule(shape => {
        if (!shape || typeof shape.captureException !== 'function') return undefined
        try {
          shape.captureException = function () { return undefined }
          shape.captureMessage = typeof shape.captureMessage === 'function'
            ? function () { return undefined }
            : shape.captureMessage
          if (typeof shape.close === 'function') {
            const r = shape.close(0)
            if (r && typeof r.catch === 'function') r.catch(() => {})
          }
          closed++
        } catch { /* */ }
        return undefined
      })

      window.addEventListener('error', event => {
        try {
          console.error(PREFIX, event.message, event.filename, event.lineno, event.error)
        } catch { /* */ }
        event.preventDefault()
      })

      window.addEventListener('unhandledrejection', event => {
        try {
          console.error(PREFIX, 'unhandledrejection', event.reason)
        } catch { /* */ }
        event.preventDefault()
      })

      window.ExocordCrashLog = Object.freeze({ prefix: PREFIX })
      return closed > 0 || true
    }
  })
})()
