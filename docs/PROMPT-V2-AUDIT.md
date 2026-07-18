# Exo master prompt v2 audit

Reviewed against Exo `v3.5.2` and the verified Internet/NVIDIA/UI work carried
forward from PR #48.

## Verdict

The prompt is a strong research brief but a weak single-pass implementation
prompt: **6.5/10 overall**.

| Area | Score | Assessment |
|---|---:|---|
| Product direction | 9/10 | Clear taste, clear scope, and unusually good honesty requirements. |
| Safety intent | 8/10 | Snapshot/verify/repair and anti-cheat boundaries are excellent foundations. |
| Testability | 6/10 | Many requirements are measurable, but several gates are manual, undefined, or placed too late. |
| Technical certainty | 5/10 | Verified facts, hypotheses, folklore verdicts, and estimates are written with the same confidence. |
| Executability | 3/10 | Eight major projects are coupled into one ordered run with no release boundaries or stop conditions. |
| Maintainability | 4/10 | Hard-coded versions, line numbers, dates, and product assumptions will age quickly. |

## What should be kept

- Live-system detection is the source of truth; state files are hints.
- Every mutation has a pre-state snapshot, post-apply verification, and exact
  Repair ownership.
- Network changes use a fail-closed canary and automatic rollback.
- Exo never touches game processes, game payloads, anti-cheat components,
  player input, or kernel drivers.
- Performance claims require before/after evidence and an inconclusive state.
- Dark-only UI, consistent tokens, accessible contrast, reduced motion, and a
  single shared optimizer story are the correct design direction.
- Folklore tweaks are deleted or labeled as unverified instead of marketed as
  gains.
- Downloads and updates require integrity verification.
- Real build, smoke, package, install, UI, and rollback gates belong in the
  definition of done.

## Problems corrected in v3

1. **Dark-only conflicts with Light resources.** The product direction and the
   user's explicit request are dark-only. High Contrast remains as an OS
   accessibility mode; a Light theme and theme toggle do not return.
2. **Scope conflicts with required actions.** V2 bans Windows-platform tweaks,
   then requires MMCSS, HAGS, registry, service, and adapter changes. V3 defines
   a narrow allowlist: a system setting is in scope only when an owned optimizer
   needs it and can snapshot, verify, and restore it.
3. **Unsafe big-bang sequencing.** Tests and runner hardening appear after UI,
   optimizer, and security rewrites. V3 establishes baseline gates first, keeps
   optimizer behavior frozen during the UI slice, and hardens the mutation
   boundary before adding new mutations.
4. **Impossible guarantees.** “Internet must never break” is not provable on an
   arbitrary machine or ISP. V3 requires dry-run planning, bounded canaries,
   rollback, offline rescue, and truthful failure reporting.
5. **Stale implementation coordinates.** File line numbers and fixed dependency
   versions are replaced by symbols, contracts, and a research decision log.
6. **Claims presented as facts.** Power, RAM, FPS, latency, cache, and endpoint
   estimates now require local evidence or copy explicitly labeled as an
   estimate/hypothesis.
7. **No release boundaries.** V3 uses reviewable release slices, each independently
   buildable, installable, repairable, and revertible.
8. **Manual gates mixed with automation.** Automated gates block CI. Manual DPI,
   Narrator, high-contrast, and real-hardware checks use recorded checklists and
   cannot be silently claimed.
9. **External prerequisites hidden as engineering work.** Code-signing identity,
   certificate enrollment, GitHub environment permissions, and artifact
   attestation are explicit external dependencies.
10. **Deletion authority is too broad.** V3 permits deleting dead Exo code only
    after reference search, parity inventory, and green gates; user data and
    system state are never treated as disposable.
11. **No migration contract.** V3 requires schema versions and backward-compatible
    reads for snapshots/settings before removing legacy data.
12. **Historical appendix contaminates the prompt.** The “cut everything below”
    section is removed from the paste-ready spec.

## Implementation interpretation

V3 is a program of small releases, not permission to claim all phases complete
after one large diff. A slice is complete only when its own acceptance criteria
and the global ship gates are green. Unverified or externally blocked items stay
open in the backlog with evidence; they are never converted into optimistic UI
copy.
