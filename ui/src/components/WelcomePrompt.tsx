import { useEffect, useState } from 'react'
import { host } from '../lib/host'

const FALLBACK_COFFEE = 'https://www.buymeacoffee.com/UhhErix'

/**
 * First-install soft pitch. Exo stays free — optional tip jar only.
 * Matches settings drawer: 16px pad, 12px stack gaps, equal CTA heights.
 */
export function WelcomePrompt() {
  const [open, setOpen] = useState(false)
  const [coffeeUrl, setCoffeeUrl] = useState(FALLBACK_COFFEE)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    void host
      .getSettings()
      .then((s) => {
        if (cancelled) return
        if (s.buyMeACoffeeUrl) setCoffeeUrl(s.buyMeACoffeeUrl)
        if (!s.welcomePromptSeen) setOpen(true)
      })
      .catch(() => {
        /* browser / host offline — skip */
      })
    return () => {
      cancelled = true
    }
  }, [])

  async function dismiss() {
    if (busy) return
    setBusy(true)
    try {
      await host.setSettings({ welcomePromptSeen: true })
    } catch {
      /* still close so we never trap the user */
    }
    setOpen(false)
    setBusy(false)
  }

  async function openCoffee() {
    if (busy) return
    setBusy(true)
    try {
      await host.openUrl(coffeeUrl)
      await host.setSettings({ welcomePromptSeen: true })
    } catch {
      try {
        window.open(coffeeUrl, '_blank', 'noopener,noreferrer')
      } catch {
        /* ignore */
      }
      try {
        await host.setSettings({ welcomePromptSeen: true })
      } catch {
        /* ignore */
      }
    }
    setOpen(false)
    setBusy(false)
  }

  if (!open) return null

  return (
    <div className="pointer-events-none absolute inset-0 z-[60]" role="presentation">
      <button
        type="button"
        aria-label="Dismiss welcome"
        className="pointer-events-auto absolute inset-0 bg-page"
        style={{ opacity: 0.78 }}
        disabled={busy}
        onClick={() => void dismiss()}
      />

      <div
        role="dialog"
        aria-labelledby="welcome-title"
        aria-describedby="welcome-body"
        className="glass specular pointer-events-auto absolute left-1/2 top-1/2 w-[min(360px,calc(100%-2rem))] -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl"
      >
        <div className="flex flex-col gap-3 p-4">
          <div>
            <p className="text-[10px] font-semibold tracking-[0.1em] text-muted">
              WELCOME TO EXO
            </p>
            <h2
              id="welcome-title"
              className="mt-1.5 text-[16px] font-semibold leading-snug tracking-tight text-text"
            >
              Free forever — tips keep it alive
            </h2>
          </div>

          <div
            id="welcome-body"
            className="space-y-2 text-[12px] leading-relaxed text-secondary"
          >
            <p>
              Exo is free. No ads, no account, no paywall. Building and shipping it still costs
              real money — tools, hosting, time, and every release.
            </p>
            <p>
              If it helps you, even <span className="font-semibold text-text">$1</span> on Buy Me
              a Coffee goes a long way. Totally optional — hit Continue either way.
            </p>
          </div>

          <div className="flex flex-col gap-3 pt-1">
            <button
              type="button"
              disabled={busy}
              onClick={() => void openCoffee()}
              className="flex h-11 w-full items-center justify-center rounded-xl bg-white text-[13px] font-semibold text-black shadow-[0_0_20px_rgb(255_255_255/0.08)] disabled:opacity-40"
            >
              Buy me a coffee
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => void dismiss()}
              className="glass-chip flex h-11 w-full items-center justify-center rounded-xl text-[13px] font-semibold text-secondary hover:text-text disabled:opacity-40"
            >
              Continue free
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
