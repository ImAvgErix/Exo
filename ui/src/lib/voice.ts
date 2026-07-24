/**
 * The brain's optional spoken voice, via the Web Speech API. This works inside
 * WebView2 (Chromium/Edge) using the Windows OneCore voices, and in dev browsers
 * too — no network, no external service.
 *
 * It is OFF until the user turns it on. That's deliberate: the first time it
 * speaks must follow a real tap (browsers gate speech behind a user gesture),
 * and nobody wants surprise audio on launch. The preference persists in
 * localStorage so once it's on, it stays on. Everything fails silent where the
 * API isn't available.
 *
 * (When the Grok layer lands later, it can call `speak()` with generated lines —
 * the voice pipeline stays exactly the same.)
 */

const KEY = 'exo.voice'

let enabled = readPref()
let chosen: SpeechSynthesisVoice | null = null
let primed = false
const listeners = new Set<(on: boolean) => void>()

function supported(): boolean {
  return (
    typeof window !== 'undefined' &&
    'speechSynthesis' in window &&
    typeof SpeechSynthesisUtterance !== 'undefined'
  )
}

function readPref(): boolean {
  try {
    return localStorage.getItem(KEY) === 'on'
  } catch {
    return false
  }
}

export function voiceSupported(): boolean {
  return supported()
}

export function voiceEnabled(): boolean {
  return enabled
}

export function onVoiceChange(fn: (on: boolean) => void): () => void {
  listeners.add(fn)
  return () => listeners.delete(fn)
}

export function setVoiceEnabled(on: boolean): void {
  enabled = on
  try {
    localStorage.setItem(KEY, on ? 'on' : 'off')
  } catch {
    /* private mode — session-only is fine */
  }
  if (on) prime()
  else stopVoice()
  for (const fn of listeners) fn(on)
}

function prime(): void {
  if (!supported() || primed) return
  primed = true
  pickVoice()
  // Voice list often loads async; refresh the pick when it arrives.
  try {
    window.speechSynthesis.addEventListener('voiceschanged', pickVoice)
  } catch {
    /* older impls expose it as a property */
    window.speechSynthesis.onvoiceschanged = pickVoice
  }
}

function pickVoice(): void {
  if (!supported()) return
  const vs = window.speechSynthesis.getVoices()
  if (!vs.length) return
  // Prefer the most natural English voice on the box: Windows "Natural"/"Online"
  // neural voices first, then the classic local ones.
  const score = (v: SpeechSynthesisVoice): number => {
    const n = v.name.toLowerCase()
    let s = 0
    if (v.lang.toLowerCase().startsWith('en')) s += 5
    if (/natural|online|neural/.test(n)) s += 8
    if (/aria|jenny|guy|ryan|libby|sonia|michelle|ana/.test(n)) s += 4
    if (/zira|david|mark|hazel/.test(n)) s += 2
    if (v.localService) s += 1
    return s
  }
  chosen = vs.slice().sort((a, b) => score(b) - score(a))[0] ?? null
}

export function stopVoice(): void {
  if (!supported()) return
  try {
    window.speechSynthesis.cancel()
  } catch {
    /* ignore */
  }
}

/** Speak a line if voice is on. Cancels whatever it was saying first. */
export function speak(text: string): void {
  if (!enabled || !supported() || !text) return
  prime()
  try {
    window.speechSynthesis.cancel()
    const u = new SpeechSynthesisUtterance(cleanForSpeech(text))
    if (chosen) u.voice = chosen
    u.rate = 1.02
    u.pitch = 1.0
    u.volume = 0.9
    window.speechSynthesis.speak(u)
  } catch {
    /* ignore — never let TTS break the UI */
  }
}

function cleanForSpeech(t: string): string {
  return t
    .replace(/…/g, '. ')
    .replace(/—/g, ', ')
    .replace(/\s+/g, ' ')
    .trim()
}
