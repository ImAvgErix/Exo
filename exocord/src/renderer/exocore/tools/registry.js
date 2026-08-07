/**
 * Exocord tools registry — our own Equicord-class plugin system.
 * Tools register with { id, title, tier, defaultOn, apply }.
 * Loaded into Discord's main world after Exocore.
 */
(function () {
  'use strict'

  const tools = new Map()
  const applied = new Set()
  const failed = new Map()

  function register (tool) {
    if (!tool || !tool.id || typeof tool.apply !== 'function') return false
    tools.set(tool.id, {
      id: tool.id,
      title: tool.title || tool.id,
      tier: tool.tier || 'optional',
      defaultOn: tool.defaultOn !== false,
      apply: tool.apply,
      teardown: typeof tool.teardown === 'function' ? tool.teardown : null
    })
    return true
  }

  function list () {
    return Array.from(tools.values()).map(t => ({
      id: t.id,
      title: t.title,
      tier: t.tier,
      defaultOn: t.defaultOn,
      applied: applied.has(t.id),
      failed: failed.has(t.id) ? failed.get(t.id) : null
    }))
  }

  function runOne (id) {
    const tool = tools.get(id)
    if (!tool) return false
    try {
      const ok = !!tool.apply()
      if (ok) {
        applied.add(id)
        failed.delete(id)
      } else {
        failed.set(id, 'apply returned false')
      }
      return ok
    } catch (err) {
      failed.set(id, (err && err.message) ? err.message : String(err))
      return false
    }
  }

  function runDefaults () {
    let n = 0
    for (const tool of tools.values()) {
      if (!tool.defaultOn) continue
      if (runOne(tool.id)) n++
    }
    return n
  }

  window.ExocordTools = Object.freeze({
    register,
    list,
    runOne,
    runDefaults,
    applied: () => Array.from(applied),
    failed: () => Object.fromEntries(failed)
  })
})()
