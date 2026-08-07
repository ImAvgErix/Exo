/**
 * Exocord own-app boot host.
 *
 * This file owns three things and nothing else: deciding when the client is
 * past login, hiding stock Discord, and handing an empty container to the React
 * bundle (app/dist/app.bundle.js, built from app-ui/). Every screen lives in
 * that bundle now.
 *
 * Stock Discord stays mounted and running underneath - React, webpack and the
 * WebRTC stack are what actually carry login, voice and the gateway. Only its
 * paint is suppressed.
 */
(function () {
  'use strict'

  const ATTR = 'data-exocord-app'
  const state = { started: false, mounted: false }

  const KILL_CSS = `
html[data-exocord-app="1"] body > *:not(#exocord-root){
  display:none!important;visibility:hidden!important;pointer-events:none!important;
  opacity:0!important;position:fixed!important;inset:0!important;width:0!important;height:0!important;
  overflow:hidden!important;z-index:-9999!important;
}
html[data-exocord-app="1"] #app-mount,
html[data-exocord-app="1"] [class*="layerContainer"],
html[data-exocord-app="1"] [class*="layers_"],
html[data-exocord-app="1"] [class*="popouts_"],
html[data-exocord-app="1"] [class*="titleBar"]{
  display:none!important;visibility:hidden!important;pointer-events:none!important;opacity:0!important;
}
html[data-exocord-app="1"],html[data-exocord-app="1"] body{
  background:#000!important;overflow:hidden!important;margin:0!important;height:100%!important;
}
html[data-exocord-app="1"] #exocord-root{
  display:block!important;position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;
  z-index:2147483646!important;pointer-events:auto!important;visibility:visible!important;opacity:1!important;
}
`

  function api () {
    return window.ExocordBridgeData
  }

  function log (msg) {
    try { console.info('[exocord-app]', msg) } catch { /* */ }
    try {
      if (window.Exocore) {
        Exocore._appBoot = Exocore._appBoot || []
        Exocore._appBoot.push(String(msg).slice(0, 200))
      }
    } catch { /* */ }
  }

  function ensureKillCss () {
    if (document.getElementById('exocord-kill-inline')) return
    const style = document.createElement('style')
    style.id = 'exocord-kill-inline'
    style.textContent = KILL_CSS
    ;(document.head || document.documentElement).appendChild(style)
  }

  function hasSession () {
    const a = api()
    if (a && typeof a.isLoggedIn === 'function' && a.isLoggedIn()) return true
    try {
      const exo = window.Exocore
      if (exo && typeof exo.findByProps === 'function') {
        const auth = exo.findByProps('getToken', 'getId') || exo.findByProps('getToken')
        if (auth && typeof auth.getToken === 'function' && auth.getToken()) return true
      }
    } catch { /* */ }
    try {
      const path = String(location.pathname || '')
      if (/\/channels\//.test(path)) return true
    } catch { /* */ }
    // Guild rail present => logged in (last-resort gate only)
    try {
      if (document.querySelector('nav[aria-label="Servers sidebar"], [data-list-item-id^="guildsnav"]')) {
        return true
      }
    } catch { /* */ }
    return false
  }

  function waitForLogin (done) {
    let tries = 0
    function tick () {
      if (hasSession()) {
        log('session detected tries=' + tries)
        done()
        return
      }
      tries++
      if (tries > 360) {
        log('login wait gave up')
        return
      }
      setTimeout(tick, 500)
    }
    tick()
  }

  /**
   * Wait for Exocore to finish capturing Discord's module graph, not just for the
   * globals to exist.
   *
   * The globals are defined the moment the preload runs, which is long before any
   * store exists. Polling the session at that point asks Exocore for UserStore,
   * gets "not loaded yet", and every consumer downstream inherits that answer -
   * the client then sits on its connecting screen with a live session behind it.
   * Exocore no longer caches a miss before it is ready, and this makes sure the
   * question is not asked that early either.
   */
  function waitForBridge (done) {
    let tries = 0
    function tick () {
      if (window.ExocordBridgeData && window.ExocordAppUI &&
          window.Exocore && window.Exocore.ready) {
        done()
        return
      }
      tries++
      if (tries > 900) {
        log('bridge wait gave up (ui=' + !!window.ExocordAppUI +
          ' ready=' + !!(window.Exocore && window.Exocore.ready) + ')')
        return
      }
      setTimeout(tick, 100)
    }
    tick()
  }

  function ensureRoot () {
    let root = document.getElementById('exocord-root')
    if (!root) {
      root = document.createElement('div')
      root.id = 'exocord-root'
      document.body.appendChild(root)
      state.mounted = false
    }
    return root
  }

  function mountUi () {
    const root = ensureRoot()
    if (state.mounted && window.ExocordAppUI.mounted()) return true
    state.mounted = window.ExocordAppUI.mount(root)
    if (state.mounted) log('ui mounted')
    else log('ui mount failed')
    return state.mounted
  }

  function activate () {
    if (state.started) return
    state.started = true
    log('activate')

    ensureKillCss()
    document.documentElement.setAttribute(ATTR, '1')

    if (!window.ExocordAppUI) {
      log('app bundle missing - stock Discord left visible')
      state.started = false
      document.documentElement.removeAttribute(ATTR)
      return
    }

    mountUi()
    if (window.Exocore) Exocore._appActive = true

    // Watchdog: Discord remounts portals and can drop our container on route
    // changes. Re-assert the kill and re-mount rather than leaving a black page.
    setInterval(() => {
      if (!state.started) return
      document.documentElement.setAttribute(ATTR, '1')
      ensureKillCss()
      if (!document.getElementById('exocord-root') || !window.ExocordAppUI.mounted()) {
        state.mounted = false
        mountUi()
      }
    }, 2000)

    window.ExocordApp = Object.assign(window.ExocordApp || {}, {
      state,
      activate,
      remount: () => {
        state.mounted = false
        window.ExocordAppUI.unmount()
        return mountUi()
      }
    })
    log('mounted')
  }

  function boot () {
    log('boot')
    waitForBridge(() => {
      log('bridge ok')
      waitForLogin(() => {
        setTimeout(activate, 200)
      })
    })
  }

  // Force path for the inject probe.
  window.__exocordForceApp = function () {
    ensureKillCss()
    activate()
    return !!(window.ExocordAppUI && window.ExocordAppUI.mounted())
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot)
  } else {
    boot()
  }
})()
