/**
 * Exocord layout — premium dark client frame (not cyberpunk wireframe).
 */
(function () {
  'use strict'

  function build () {
    const root = document.createElement('div')
    root.id = 'exocord-root'
    root.innerHTML = `
      <div class="exo-app">
        <header class="exo-top">
          <div class="exo-brand" aria-label="Exocord">
            <span class="exo-brand-mark" aria-hidden="true"></span>
            <span class="exo-brand-name">Exocord</span>
          </div>
          <div class="exo-top-meta">
            <span class="exo-context" data-exo-stage-sub>Home</span>
          </div>
          <nav class="exo-top-actions" aria-label="App">
            <button type="button" class="exo-nav" data-exo-drawer-toggle>Channels</button>
            <button type="button" class="exo-nav" data-exo-nav-friends>Friends</button>
            <button type="button" class="exo-nav exo-nav-accent" data-exo-settings>Settings</button>
          </nav>
          <div class="exo-account" data-exo-account>
            <img data-exo-av alt="" width="32" height="32" />
            <span class="exo-account-name" data-exo-name>You</span>
            <button type="button" class="exo-icon" data-exo-mute title="Mute">Mic</button>
            <button type="button" class="exo-icon" data-exo-deaf title="Deafen">Deaf</button>
          </div>
        </header>

        <div class="exo-body">
          <nav class="exo-rail" data-exo-rail aria-label="Servers"></nav>

          <aside class="exo-drawer" data-exo-drawer>
            <div class="exo-drawer-head" data-exo-panel-head>Direct messages</div>
            <div class="exo-panel-list" data-exo-panel-list></div>
          </aside>

          <main class="exo-stage">
            <div class="exo-stage-bar">
              <h1 class="exo-stage-title" data-exo-stage-title>Friends</h1>
            </div>
            <div class="exo-messages" data-exo-messages></div>
            <div class="exo-composer" data-exo-composer></div>
            <div class="exo-voice" data-exo-voice>
              <span class="exo-voice-dot" aria-hidden="true"></span>
              <span data-exo-voice-text>In call</span>
              <span class="exo-voice-spacer"></span>
              <button type="button" class="exo-nav" data-exo-voice-mute>Mute</button>
              <button type="button" class="exo-nav" data-exo-voice-deaf>Deafen</button>
            </div>
          </main>
        </div>
      </div>

      <section class="exo-settings" data-exo-settings-overlay>
        <div class="exo-settings-frame">
          <h1>Exocord</h1>
          <p class="exo-settings-sub">Tools and client controls. Discord handles auth and voice underneath.</p>
          <button type="button" class="exo-nav exo-nav-accent" data-exo-settings-close>Close</button>
          <ul class="exo-tools-list" data-exo-tools></ul>
        </div>
      </section>
    `
    return root
  }

  window.ExocordLayout = Object.freeze({ build })
})()
