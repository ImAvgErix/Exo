/**
 * Exocord-owned composer
 */
(function () {
  'use strict'

  function mount (host, state, api) {
    if (!host || host.dataset.mounted === '1') return
    host.dataset.mounted = '1'
    host.innerHTML = `
      <div class="exo-composer-box">
        <textarea class="exo-composer-input" data-exo-input rows="1" placeholder="Message"></textarea>
        <button type="button" class="exo-composer-send" data-exo-send>Send</button>
      </div>
    `
    const input = host.querySelector('[data-exo-input]')
    const sendBtn = host.querySelector('[data-exo-send]')

    function send () {
      const channelId = api.getSelectedChannelId()
      const content = input.value
      if (!channelId || !content.trim()) return
      if (api.sendMessage(channelId, content)) {
        input.value = ''
        state.forceScroll = true
        state.onNavigate && state.onNavigate()
      }
    }

    sendBtn.addEventListener('click', send)
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault()
        send()
      }
    })
  }

  function syncVisibility (host, state, api) {
    if (!host) return
    const channelId = api.getSelectedChannelId()
    const hide = state.view === 'friends' && !channelId
    host.style.display = hide ? 'none' : ''
  }

  window.ExocordViewComposer = Object.freeze({ mount, syncVisibility })
})()
