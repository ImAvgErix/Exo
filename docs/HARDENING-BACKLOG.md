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

All four items below were re-investigated against current source (not just the mined research
summary) and found already correctly implemented — no change made, documented here so a future
pass doesn't have to re-derive this:

1. **Boot-verify.** `Disc-Optimizer.ps1`'s hosted-Apply path (`$env:EXO -eq '1' -and $NoLaunch`)
   deliberately takes a disk-only verify branch instead of `Confirm-DiscordBootsAfterMods` — the
   comment states why ("Exo Apply never auto-reopens Discord; user opens it from Start Menu"),
   a considered UX trade-off, not an oversight. It already reports honestly: the apply-report
   step is `Add-ExoReport 'boot-check' 'ok' 'disk verify only - no auto-open'`, which reaches
   the UI as-is through the generic `applyReport[]` step-line mechanism. `-Quick` and the
   non-quiet full path both still call the real boot check. Forcing a real boot check into the
   default hosted path would mean auto-launching Discord on every Apply — a behavior change
   with real UX risk that needs a live Windows+Discord rig to validate safely; not attempted.
2. **Kernel soft-skip vs `isApplied`.** `Exo-Discord-Detect.ps1`'s `$kernelOk` starts `$false`
   and only flips true after real file-hash/size/config validation of the installed kernel
   proxy; `isApplied` ANDs it in. A skipped or failed kernel install correctly reads as
   `isApplied=false` — already consistent.
3. **QoS / variant partial failures.** Same pattern: `$qosOk`/`$variantsOk` are computed from
   real per-variant `Test-DiscOptQosPolicyMap` checks and ANDed into `isApplied` — a partial QoS
   failure already flips the overall applied state honestly.
4. Heuristic detect fallback — by design, not a bug: `OptimizerStateService.DetectDiscordAsync`
   only returns the lightweight heuristic for `fastOnly` calls or when the full PowerShell
   detect fails outright; a normal module-page open always re-runs the full/real detect.

Still open:

5. ~~`AppSettings.DiscordKitVersion` fallback constant can drift from `Scripts/Discord/VERSION`~~
   — **fixed**: synced to 1.3.74 alongside the kit VERSION bump (both now read the same value;
   still two separately-maintained literals, not a single source of truth — a future pass could
   have `AppServices`/`SettingsService` read `Scripts/Discord/VERSION` directly at startup).
6. Surface a "Reapply needed" state when a Discord client update wipes the kit
   (ffmpeg-proxy kernel breaks on host updates). **Still open** — needs a live Discord
   auto-update to reproduce/verify; not attempted in this pass.

## Steam

1. CEF launch flags churn with Steam client updates — add a detect row that flags an unknown
   Steam build instead of silently applying stale flags. **Still open** — needs current,
   verified data on what "known good" Steam build/CEF-flag combinations look like right now;
   inventing a threshold without a live Steam install to check against would just be a second
   guess layered on the first. Revisit with real version data in hand.
2. Post-apply verify: re-run `NativeLiveDetect.DetectSteam` after apply and record per-feature
   verified state. **Folded into the Platform item below** — doing this for Steam alone would
   be an inconsistent one-off; it belongs in the same standardized apply→re-detect→persist
   pass as Discord/Brave/NVIDIA.

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
   "unknown schema" and skip gracefully instead of writing stale keys. **Partially fixed** —
   implemented for Marvel Rivals (`GameOptimizerService.PatchGameUserSettings`/
   `MarvelConfigSchemaKnown`): `Engine.ini`/`Scalability.ini` are Exo-owned files it writes
   wholesale (no schema-drift risk there — they're not parsed/merged), but
   `GameUserSettings.ini` is merged into an existing user file, so it now checks the file
   already contains a section it recognizes before patching; a rename/restructure surfaces as
   an honest "didn't match the sections Exo expects" note in the apply result instead of
   silently landing keys the game's current build won't read. **Still open** for the 9 titles
   in `GameOptimizerService.MultiGame.cs` (Fortnite/Valorant/League/CS2/Apex/Helldivers 2/The
   Finals/Predecessor/Black Ops 7) — each has its own config format and needs verification
   against a real, current install per title; not attempted blind in this pass.
2. Post-apply verify: re-read configs and diff against intended writes. **Folded into the
   Platform item below** for consistency with the other three modules.
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
