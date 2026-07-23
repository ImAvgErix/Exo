# Hardening backlog

Known keeper-module defects mined from the July 2026 rewrite research (now deleted from
`docs/rewrite/`) plus items identified during the 2026 cleanup audit (`docs/CLEANUP-PLAN.md`).
Each item should ship with a detect row / smoke marker when fixed.

## Internet — detect/apply contract mismatches

1. Congestion row is always OK — verify CUBIC is actually the active provider or drop the row.
2. Preferred Band is applied but never checked as applied.
3. Success message can still claim "Wi-Fi disabled when Ethernet has a real IP"
   (`NetworkOptimizerService` policy string) — false; apply never disables Wi-Fi.
4. `NetworkMediaProfile` XML comment still mentions Client/LLDP off (never done).
5. Golden-path doc: residual "Wi-Fi disable gated on probe" language vs code never disables.
6. Latency roaming aggressiveness: code prefers Low; golden-path table says Medium — doc bug.
7. Ring buffers: golden-path says Max for latency; code uses mid — doc bug.
8. DSCP QoS: verify marking actually applies on non-domain networks
   (`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS` → `"Do not use NLA"="1"`),
   snapshotted + repairable.

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

1. ~~Apply-time live GitHub download of nvidiaProfileInspector~~ — pin exact version + SHA-256,
   cache in the kit, hard-fail offline with a clear error. (Addressed in cleanup Phase 2.)
2. Verify DLSS override preset IDs against the current driver branch when regenerating packs.

## Games (config-only since the pak/bypass removal)

1. Per-title config-schema probe: when a game patch changes the config format, detect
   "unknown schema" and skip gracefully instead of writing stale keys.
2. Post-apply verify: re-read configs and diff against intended writes.
3. Legacy cleanup: Games Repair should remove leftover `dsound.dll` / `.asi` / `Exo*.pak`
   bypass files from installs made by ≤3.16.13.
4. Prune any title whose install/config detection can't be made reliable on real machines.

## Brave

1. Confirm managed-policy names against current Brave stable (Chromium policy churn).
2. Post-apply verify via `DetectBrave` re-run.

## Platform

1. Post-apply verification standardized across modules: apply → re-detect → persist
   per-feature verified state → UI shows "Applied ✓ verified" vs "Applied — verify failed".
2. Native apply paths should write the same `applyReport[]` step lines the PowerShell paths do.
