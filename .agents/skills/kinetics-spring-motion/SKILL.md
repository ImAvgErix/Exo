---
name: kinetics-spring-motion
description: Spring-physics motion recipes distilled from Kinetics (kinetics.colorion.co) — tuned cubic-bezier curves that mimic springs, plus interaction patterns (press squish, stagger reveal, error shake, toast, sliding pill, expanding search). Use when implementing or reviewing animations, transitions, hover/press feedback, easing curves, or when motion feels stiff, linear, or lifeless.
---

# Kinetics Spring Motion

Motion built on spring feel instead of flat duration+easing. Source: [kinetics.colorion.co](https://kinetics.colorion.co/) (117 recipes, CSS + React + prompt per effect).

## Core idea

A spring is two numbers — **stiffness** (pull toward target) and **damping** (resistance to oscillation) — not a duration. Interrupted mid-motion it keeps integrating from current velocity instead of snapping. In pure CSS, approximate springs with the tuned beziers below.

## The five curves

| Curve | Feel | Use for |
|---|---|---|
| `cubic-bezier(0.34, 1.56, 0.64, 1)` | Spring pop, gentle overshoot (~spring 320/24) | Press release, chip/tag pop, toggle knobs, counter bumps |
| `cubic-bezier(0.16, 1, 0.3, 1)` | Fast glide, long settle, no overshoot | Reveals, height/width expand, list entrances, progress |
| `cubic-bezier(0.65, 0, 0.35, 1)` | Symmetric move | Sliding pills/indicators between positions, underlines |
| `cubic-bezier(0.18, 1.25, 0.4, 1)` | Big overshoot arrival | Toasts / sheets sliding in from off-screen |
| `cubic-bezier(0.36, 0.07, 0.19, 0.97)` | Decaying oscillation (with shake keyframes) | Error shake |

## Recipes Exo actually uses

**Press squish (asymmetric = physical):** down fast, up springy.

```css
.btn:active { transform: scale(0.88); transition: transform 0.08s ease-out; }
.btn { transition: transform 0.5s cubic-bezier(0.34, 1.56, 0.64, 1); }
```

**Staggered list reveal:** rise + fade, `index * 90ms` delay.

```css
.item { opacity: 0; transform: translateY(14px); }
.item.in {
  opacity: 1; transform: none;
  transition: opacity .45s cubic-bezier(.16,1,.3,1) var(--d),
              transform .45s cubic-bezier(.16,1,.3,1) var(--d); /* --d: i*90ms */
}
```

**Sliding pill indicator (segmented control / nav):** animate `left` + `width` measured from the target, `cubic-bezier(.65,0,.35,1)` ~0.4s; label color crossfades as the pill arrives.

**Error shake:** decaying translateX keyframes (±4 → ±2 → ±1px) over 0.45s with the shake curve; retrigger by removing/re-adding the class via a reflow.

**Toast arrival:** `translateY(140%) scale(0.9)` → `none` with the toast curve ~0.55s; opacity fades separately at 0.3s.

**Expanding search / card resize:** animate one dimension only (width or height) with the glide curve; `overflow: hidden`; never animate `max-height` hacks when a real value is known.

**Fan-out menu (matches the top-bar reference):** each child scales 0.4 → 1 with the spring-pop curve, `transition-delay` staggered ~50ms, trigger icon rotates to × concurrently.

## Rules

- **Asymmetric timing** — in fast (~0.08–0.15s), out springy (~0.4–0.55s). Symmetric = mechanical.
- **Overshoot only on small interactive elements** (buttons, chips, knobs, toggles). Exo shell rule stands: **no spring bounce on page content, plates, or logo bitmaps** — those use the glide curve.
- Animate `transform`/`opacity` first; dimensions only with the glide curve; never `top/left` for element motion (sliding pill indicator is the exception — it's a layout-tracking element).
- One property per feel: don't stack overshoot on both scale and translate of the same element unless imitating the counter-bump recipe.
- Interruptibility: restart animations cleanly (reflow trick) and disable transitions during drag, re-enable on release with the spring-pop curve.

## WinUI mapping

XAML has no cubic-bezier; approximate: spring pop → `BackEase` is banned in Exo, so use `CubicEase EaseOut` with slight scale-past values baked into keyframes; glide → `CubicEase`/`QuinticEase EaseOut`; keep durations from the table.
