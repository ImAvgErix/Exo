/**
 * anonymiseFileNames — rename uploads to random names before send.
 */
(function () {
  'use strict'
  if (!window.ExocordTools) return

  function findByProps () {
    if (!window.Exocore || typeof window.Exocore.findByProps !== 'function') return null
    return window.Exocore.findByProps.apply(null, arguments)
  }

  function randomName (original) {
    const ext = (original && original.includes('.')) ? original.split('.').pop() : 'bin'
    const id = Math.random().toString(36).slice(2, 10)
    return 'file_' + id + '.' + ext.replace(/[^a-z0-9]/gi, '').slice(0, 8)
  }

  ExocordTools.register({
    id: 'anonymiseFileNames',
    title: 'Anonymise File Names',
    tier: 'default',
    defaultOn: true,
    apply () {
      let patched = 0

      const upload = findByProps('upload', 'instantUpload') ||
        findByProps('uploadFiles', 'promptToUpload')
      if (upload) {
        for (const key of Object.keys(upload)) {
          if (typeof upload[key] !== 'function') continue
          if (!/upload|file|attachment/i.test(key)) continue
          try {
            const orig = upload[key].bind(upload)
            upload[key] = function () {
              const args = Array.from(arguments)
              for (let i = 0; i < args.length; i++) {
                const a = args[i]
                if (a instanceof File) {
                  args[i] = new File([a], randomName(a.name), { type: a.type })
                  patched++
                } else if (Array.isArray(a)) {
                  args[i] = a.map(f => f instanceof File
                    ? new File([f], randomName(f.name), { type: f.type })
                    : f)
                }
              }
              return orig.apply(this, args)
            }
            patched++
          } catch { /* */ }
        }
      }

      document.addEventListener('change', event => {
        const input = event.target
        if (!input || input.tagName !== 'INPUT' || input.type !== 'file') return
        if (!input.files || !input.files.length) return
        try {
          const dt = new DataTransfer()
          for (const f of input.files) {
            dt.items.add(new File([f], randomName(f.name), { type: f.type }))
          }
          input.files = dt.files
          patched++
        } catch { /* */ }
      }, true)

      return patched > 0 || !!document.documentElement
    }
  })
})()
