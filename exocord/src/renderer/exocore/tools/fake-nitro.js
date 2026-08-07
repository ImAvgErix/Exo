/**
 * fakeNitro — first-party Equicord-class tool.
 * Unlocks external emoji/sticker send + nitro stream quality client-side.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function wp () {
    return window.Exocore && typeof Exocore.webpackRequire === 'function'
      ? null
      : (window.webpackChunkdiscord_app && window.Exocore)
  }

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function eachModule (fn) {
    if (!window.Exocore || typeof window.Exocore.eachModule !== 'function') return undefined
    return window.Exocore.eachModule(fn)
  }

  ExocordTools.register({
    id: 'fakeNitro',
    title: 'Fake Nitro',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0

      // Premium type / nitro entitlement checks often live on UserStore or PremiumUtils.
      const premium = findByProps('getPremiumType', 'canUseAnimatedEmojis') ||
        findByProps('canUseEmojisEverywhere') ||
        findByProps('canUseCustomStickersEverywhere')
      if (premium) {
        for (const key of Object.keys(premium)) {
          if (/^canUse/i.test(key) && typeof premium[key] === 'function') {
            try {
              premium[key] = function () { return true }
              patched++
            } catch { /* sealed */ }
          }
        }
        if (typeof premium.getPremiumType === 'function') {
          try {
            premium.getPremiumType = function () { return 2 }
            patched++
          } catch { /* sealed */ }
        }
      }

      // Stream quality bypass (also covered by limitlessScreenshare).
      const stream = findByProps('ApplicationStreamFPS', 'ApplicationStreamResolution') ||
        findByProps('getApplicationFramerate') ||
        eachModule(m => {
          try {
            if (m && m.ApplicationStreamFPSButtons && m.ApplicationStreamResolutionButtons) return m
          } catch { /* */ }
          return undefined
        })
      if (stream) {
        try {
          if (stream.ApplicationStreamFPS) {
            for (const k of Object.keys(stream.ApplicationStreamFPS)) {
              if (typeof stream.ApplicationStreamFPS[k] === 'number') {
                /* leave enums; unlock via canUse below */
              }
            }
          }
          for (const key of Object.keys(stream)) {
            if (/canUse|isPremium|getMax/i.test(key) && typeof stream[key] === 'function') {
              stream[key] = function () { return true }
              patched++
            }
          }
        } catch { /* */ }
      }

      // Message send: allow external emoji by rewriting permission predicate modules.
      const emoji = findByProps('getEmojiUnavailableReason') || findByProps('isEmojiDisabled')
      if (emoji) {
        try {
          if (typeof emoji.getEmojiUnavailableReason === 'function') {
            emoji.getEmojiUnavailableReason = function () { return null }
            patched++
          }
          if (typeof emoji.isEmojiDisabled === 'function') {
            emoji.isEmojiDisabled = function () { return false }
            patched++
          }
          if (typeof emoji.isEmojiPremiumLocked === 'function') {
            emoji.isEmojiPremiumLocked = function () { return false }
            patched++
          }
        } catch { /* */ }
      }

      void wp
      return patched > 0
    }
  })
})()
