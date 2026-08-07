/**
 * In-call voice strip
 */
(function () {
  'use strict'

  function sync (root, api) {
    const inCall = window.Exocore && typeof Exocore.isInCall === 'function'
      ? Exocore.isInCall()
      : null
    if (inCall === true) {
      document.documentElement.setAttribute('data-exocord-in-call', '1')
    } else {
      document.documentElement.removeAttribute('data-exocord-in-call')
    }
    const text = root && root.querySelector('[data-exo-voice-text]')
    if (text) {
      text.textContent = inCall === true ? 'In call' : inCall === false ? 'Voice idle' : 'Voice'
    }
  }

  function bind (root) {
    if (!root || root.dataset.bound === '1') return
    root.dataset.bound = '1'
    const mute = root.querySelector('[data-exo-voice-mute]')
    const deaf = root.querySelector('[data-exo-voice-deaf]')
    if (mute) {
      mute.addEventListener('click', () => {
        if (window.Exocore) Exocore.toggleMute()
      })
    }
    if (deaf) {
      deaf.addEventListener('click', () => {
        if (window.Exocore) Exocore.toggleDeafen()
      })
    }
  }

  window.ExocordViewVoice = Object.freeze({ sync, bind })
})()
