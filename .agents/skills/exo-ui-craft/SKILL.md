---
name: exo-ui-craft
description: Exo's reference spacing rhythm for dark stacked-card surfaces — exact margins, gaps, and stepped row heights. Use when building or reviewing Exo layout, padding, margins, card stacks, or when spacing "feels off" or looks broken.
---

# Exo UI Craft — Spacing Rhythm

Reference spec for stacked cards on Exo's AMOLED surfaces. Apply the values as-is; deviate only with a stated reason. For shadows-instead-of-borders, button surfaces, and press feedback, use the `better-ui` skill; for motion curves use `kinetics-spring-motion`.

## Dark card stack

- **Side margins**: 16px between the card and the frame edge (both sides)
- **Gaps between stacked rows**: 12px — one value everywhere, no ad-hoc gaps
- **Row heights step up with importance/interactivity**:
  - Header row (icon · title · icon): **48px**
  - Segmented control: **56px**
  - Ruler/slider row: **72px**
  - Action cards (two-up, title + subtitle): **84px**
- Hero value sits in the flexible space — biggest text on the surface
- Outer card large radius; nested rows slightly smaller radius
- Two-up cards share equal width and the same 12px gutter as the stack
- Circular icon buttons inset from the card edge by equal padding both sides

## Review checklist

- [ ] One consistent side margin (16px) — nothing flush to the edge
- [ ] All vertical gaps identical (12px)
- [ ] Row heights step meaningfully (48 → 56 → 72 → 84), not near-identical
- [ ] Two-up actions share one gutter width with the rest of the stack
