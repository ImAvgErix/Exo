/**
 * gameActivityToggle — expose quick toggle to disable game activity sharing.
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

  let enabled = false

  ExocordTools.register({
    id: 'gameActivityToggle',
    title: 'Game Activity Toggle',
    tier: 'default',
    defaultOn: true,
    apply () {
      const settings = findByProps('updateAsync', 'saveSettings') ||
        findStore('UserSettingsProtoStore')
      const activity = findByProps('updateActivity', 'clearActivity')

      function setGameActivity (on) {
        enabled = !!on
        if (activity && typeof activity.clearActivity === 'function' && !on) {
          try { activity.clearActivity() } catch { /* */ }
        }
        if (settings && typeof settings.updateAsync === 'function') {
          try {
            settings.updateAsync({ activity: { showCurrentGame: on } })
          } catch { /* */ }
        }
        return enabled
      }

      window.ExocordGameActivity = Object.freeze({
        isOn: () => enabled,
        enable: () => setGameActivity(true),
        disable: () => setGameActivity(false),
        toggle: () => setGameActivity(!enabled)
      })

      setGameActivity(false)
      return true
    }
  })
})()
