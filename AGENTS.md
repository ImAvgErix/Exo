# Exo agent and product rules

## Hard stops

1. Do not brick the PC or network (snapshot + canary + Repair; never permanently kill Wi-Fi).
2. Do not ban the user (never inject/kill/trim game anti-cheat or protected game shipping processes).
3. No folklore (no invented FPS registry, no fake claims).
4. Never EmptyWorkingSet thrash Steam CEF (steamwebhelper). Soft reclaim on non-foreground CEF is allowed.

## Windows tweak ownership

App optimizers keep app-scoped Windows integration. Machine-wide host policy (MMCSS, HAGS, Game Mode, power) is owned by the System / PC module — Internet and Steam must not restamp it.

## Known god-file exceptions

`Steam-Optimizer.ps1` and `Nvidia-Optimizer.ps1` are single-file scripts well over the usual 80 KB tidiness threshold (each covers detect, apply, repair, and safety rails for its module in one runner). This is accepted god-file debt, not an oversight — a thin-runner + named-steps split is future work, not a ship blocker. `Contracts.Smoke` asserts a note like this one exists whenever either file crosses 80 KB, so the exception stays documented rather than silently growing.
