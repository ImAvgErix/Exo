---
name: exo-ui-craft
description: Exo's reference specs for spacing rhythm on dark stacked cards and for crafting soft glass buttons/pills. Use when building or reviewing Exo UI layout, padding, margins, card stacks, segmented controls, buttons, pills, or when spacing "feels off". Contains exact px values and shadow/gradient recipes to follow.
---

# Exo UI Craft

Two reference specs to follow for Exo surfaces. Apply the values as-is; deviate only with a stated reason.

## 1. Dark card stack — spacing rhythm

Reference: alarm-style card on pure black, nested rounded cards.

- **Side margins**: 16px between the card and the frame edge (both sides)
- **Gaps between stacked rows**: 12px
- **Row heights** (top to bottom, scale up as importance/interactivity grows):
  - Header row (icon · title · icon): **48px**
  - Segmented control (AM/PM style): **56px**
  - Ruler/slider row: **72px**
  - Bottom action cards (two-up, title + subtitle): **84px**
- Hero value (7:00 AM style) sits in flexible space between the segmented control and the ruler — biggest text on the surface, centered
- Corner treatment: outer card large radius; nested rows slightly smaller radius; two-up action cards equal width with the same 12px gutter between them
- Icon buttons in the header are circular and optically inset from the card edge by the same padding both sides

Checklist when reviewing a stacked card:
- [ ] One consistent side margin (16px) — nothing flush to the edge
- [ ] All vertical gaps identical (12px), no ad-hoc values
- [ ] Row heights step up meaningfully (48 → 56 → 72 → 84), not near-identical
- [ ] Two-up actions share one gutter width with the rest of the stack

## 2. Soft button / pill recipe

Reference: "All integrations" pill (light surface). Adapt colors per theme; keep the structure.

1. Frame: **30px height**, hug width with horizontal padding
2. Corner radius: **10px**
3. Text: **Geist 14 Medium** (Exo: use the UI font at 14/Medium), centered
4. Background: **vertical linear gradient**, top `#FFFFFF` 0% → bottom `#F7F7F8` 100%
5. **No stroke.** Depth comes from two effects instead:
   - Drop shadow: X 0, Y 1, Spread 0, Blur 2, `#282828` at **8%**
   - Drop shadow: X 0, Y 0, Spread 1, Blur 0, `#ECECEC` at **100%** (acts as a soft hairline)

Dark-theme adaptation (Exo AMOLED):
- Gradient: subtle white-on-black (e.g. `rgba(255,255,255,.10)` → `rgba(255,255,255,.04)`)
- Hairline shadow ring: `rgba(255,255,255,.35–.6)` at 1px spread instead of `#ECECEC`
- Ambient shadow: black at higher opacity (black-on-black needs more)

Rule of thumb: **never a hard 1px stroke for soft controls** — build the edge from a 1px-spread shadow ring plus a faint ambient shadow so the control feels set into the surface.

## When to apply

- Any new card, tile, row stack, or button on Exo surfaces (WinUI + `tools/Exo.UiPreview`)
- Reviews where spacing/padding is criticized ("feels off", "broken spacing")
- Pair with `make-interfaces-feel-better` and `emil-design-eng` for judgment calls these specs don't cover
