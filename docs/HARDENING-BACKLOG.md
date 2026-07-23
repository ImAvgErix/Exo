# Hardening backlog

Known keeper-module defects mined from the July 2026 rewrite research (now deleted from
`docs/rewrite/`) plus items identified during the 2026 cleanup audit (`docs/CLEANUP-PLAN.md`).
Each item should ship with a detect row / smoke marker when fixed.

## Internet — detect/apply contract mismatches

1. ~~Congestion row is always OK~~ — **fixed.** `NetworkLogic.CongestionMatches` compares the
   live-read provider against CUBIC (the only provider Apply ever sets); unknown/unread still
   skips (same rationale as `AutotuneMatches`) so a probe gap never reads as "not checked".
2. **Verified already correct** — Preferred Band detect (`NetworkOptimizerService.cs` "Wi‑Fi
   capability" row) already flags 2.4-only/No-Preference as a miss post-apply when the target
   is 5/6 GHz; not a bug.
3. ~~Success message can still claim "Wi-Fi disabled when Ethernet has a real IP"~~ — **fixed.**
   Rewritten to "Ethernet preferred (Wi‑Fi stays up; metrics only, never disabled)."
4. ~~`NetworkMediaProfile` XML comment still mentions Client/LLDP off (never done)~~ — **fixed**
   in `NetworkSnapshot.cs` (`AdapterBindingsOk` doc comment).
5. **Investigated, not a bug** — the golden-path "disabled adapters are recorded / re-enabled"
   language describes real defensive snapshot/restore behavior for adapter-enabled *state*
   (in case something other than Exo disabled an adapter), not a claim that Exo's own Apply
   ever disables one. `Disable-NetAdapter` (whole-adapter) does not appear anywhere in Apply;
   only `Disable-NetAdapterBinding`/`-Lso`/`-Rsc` (scoped feature toggles).
6. ~~Latency roaming aggressiveness: code prefers Low; golden-path table says Medium~~ — **fixed**
   (`docs/INTERNET-GOLDEN-PATH.md`: Low for latency, stable BSS; Medium for throughput).
7. ~~Ring buffers: golden-path says Max for latency; code uses mid~~ — **fixed** (golden-path now
   says mid-high/~75th percentile for latency, matching the code's own jitter-avoidance comment).
8. DSCP QoS: verify marking actually applies on non-domain networks
   (`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS` → `"Do not use NLA"="1"`),
   snapshotted + repairable. **Still open** — needs a real non-domain-network test rig to verify
   safely; not attempted in this pass.

## Discord — honesty issues

1. Exo-hosted Apply never runs `Confirm-DiscordBootsAfterMods`; detect cannot prove bootability.
   Add boot-verify to the in-app apply path.
2. Kernel proxy soft-skip + apply exit 0 can disagree with the kernel feature row / `isApplied`.
3. QoS / variant partial failures may not fail the apply throw path while detect requires
   perfection for `isApplied`.
4. Heuristic detect fallback (when the detect script fails) can diverge from live PS detect.
5. `AppSettings.DiscordKitVersion` fallback constant can drift from `Scripts/Discord/VERSION` —
   read the kit VERSION instead.
6. Surface a "Reapply needed" state when a Discord client update wipes the kit
   (ffmpeg-proxy kernel breaks on host updates).

## Steam

1. CEF launch flags churn with Steam client updates — add a detect row that flags an unknown
   Steam build instead of silently applying stale flags.
2. Post-apply verify: re-run `NativeLiveDetect.DetectSteam` after apply and record per-feature
   verified state.

## NVIDIA

1. **Investigated — literal version pinning is deliberately rejected by the project.**
   `Nvidia.Smoke` enforces "NPI no hard-pinned old tag constant": a stale pin can't understand
   newer GPU/driver DRS schemas, so "always GitHub Latest" is the considered policy, not an
   oversight. The download is already SHA-256-verified against GitHub's published asset
   digest, cached, and reused offline with a clear error on total failure. **Fixed instead:**
   `Resolve-LatestNpiRelease` now detects GitHub's unauthenticated API rate limit (403/429 —
   the realistic failure mode on shared/corporate NAT, 60 req/hr/IP) and surfaces a specific,
   actionable warning instead of a generic network-error message.
2. Verify DLSS override preset IDs against the current driver branch when regenerating packs.
   **Still open** — needs a live driver install to verify safely; not attempted in this pass.

## Games (config-only since the pak/bypass removal)

1. Per-title config-schema probe: when a game patch changes the config format, detect
   "unknown schema" and skip gracefully instead of writing stale keys.
2. Post-apply verify: re-read configs and diff against intended writes.
3. ~~Legacy cleanup: Games Repair should remove leftover `dsound.dll` / `.asi` / `Exo*.pak`
   bypass files from installs made by ≤3.16.13~~ — **fixed** in the pak/bypass removal itself
   (`GameOptimizerService.RepairAsync` now purges legacy bypass files and the pack cache).
4. Prune any title whose install/config detection can't be made reliable on real machines.

## Brave

1. Confirm managed-policy names against current Brave stable (Chromium policy churn).
2. Post-apply verify via `DetectBrave` re-run.
3. ~~No CI coverage for the promoted module~~ — **fixed**: `tools/Brave.Smoke` added, asserting
   the telemetry policy pack, the `ComponentUpdatesEnabled` security exception, GPU
   high-perf preference, full snapshot/restore Repair, and no full-profile-wipe.

## Platform

1. Post-apply verification standardized across modules: apply → re-detect → persist
   per-feature verified state → UI shows "Applied ✓ verified" vs "Applied — verify failed".
   **Still open** — cross-cutting change across all four apply paths; not attempted in this pass.
2. Native apply paths should write the same `applyReport[]` step lines the PowerShell paths do.
   **Still open.**
3. ~~Script manifest freshness is unchecked by CI~~ — **fixed**: both workflows now regenerate
   the manifest and fail the build on drift.
