# Discord — evidence audit (keep / drop / be honest)

Same bar as Games and Network: does each lever do something real and durable, or is it
theatre? Discord is a **maintenance-sensitive** target — it self-updates aggressively, so
"durable" is the hard part here, not "effective".

## What actually helps a *gaming* PC (and the honest framing)

| Lever | Verdict | Reality |
|---|---|---|
| **`chromiumSwitches`**: breakpad/crash-reporter off, domain-reliability off, component-update off, background-networking off, `no-pings`, renderer-backgrounding off | **KEEP** | These cut real background work and phone-home traffic from an app that's open during every session. Small but genuine, and the kit **verifies them after apply** (a real verify step, not a claim). |
| **Voice QoS (DSCP 46)** | **KEEP** | Real prioritization for voice packets on your own network — the same mechanism as the Internet module. Matters in a match. |
| **Windows quiet: Run-key autostart off, toasts off, tray off** | **KEEP** | Removes a startup process and mid-game popups. Boring, real, zero risk. |
| **`DISABLEDXMAXIMIZEDWINDOWEDMODE`** | **KEEP** | Legit legacy flag that stops DX maximized-windowed interfering with fullscreen games. |
| **Hardware acceleration** | **KEEP ON — but frame it honestly** | This is a **trade-off, not a win**. HW accel puts Discord's UI/video on the **GPU** (contends with your game) but uses **less CPU**; disabling it frees GPU at CPU cost and cuts RAM. Evidence says impact on modern dedicated-GPU systems is **minimal either way**. So: don't sell it as an FPS win in either direction. Leave it on by default (Discord's own default, fewer support surprises) and, if we ever expose it, say plainly "frees a little GPU, costs a little CPU." |
| **Kernel proxy `PriorityClass = 3` (AboveNormal)** | **KEEP** | Correct level for a voice app — audible benefit under load without starving the game. Raising a chat app to High would be the folklore mistake; we don't. |

## The two honesty problems (fix these)

1. **`SKIP_HOST_UPDATE: true` has a real cost we don't state.** It stops Discord's host
   auto-update — which is exactly what keeps the Exo kit from being wiped — but it also means
   **you stop getting Discord security/feature updates** until something re-enables it. That's a
   legitimate trade the user should be *told* about, not silently opted into. **Fix:** say it in
   the module copy, and surface a "Discord is N versions behind" nudge.
2. **The ffmpeg-proxy kernel is the single most fragile thing we ship.** It's the piece that
   breaks when Discord updates, and a broken kernel is worse than no kernel. **Fix:** the
   detect row must distinguish "kernel active" from "kernel was applied but this Discord build
   moved on → Reapply needed", and never report `isApplied` on a soft-skipped kernel.

## Refused (folklore)

- **Discord "FPS boost" registry packs** — there is no registry key that makes Discord faster.
- **Setting Discord to High/Realtime priority** — starves the game; AboveNormal is the ceiling
  for a background voice app.
- **Killing/suspending Discord processes mid-game** — banned in our own rails (`ForbiddenApplyPatterns`);
  it breaks voice, which is the entire point of the app.
- **Blanking the client (`--disable-gpu` style blanket flags)** — breaks screenshare/video for a
  theoretical saving.

## Net

Discord's tuning is already close to right; the work here is **honesty and durability**, not new
tweaks: state the update trade-off, make kernel state truthful, and stop implying hardware
acceleration is a performance win in either direction.
