# PC-aware contract (community app)

Exo is used on **many different PCs** — not one developer machine. Every optimizer
and game tweak must behave as if the next install has different drives, GPUs,
NICs, Windows SKUs, and software.

## Law

1. **Discover first.** Before writing, inventory what exists *on this PC* (live
   registry, filesystem, Task Scheduler, adapters, displays, installed clients).
2. **Act only on hits.** Never require a giant fixed path list to “succeed.”
   Missing targets are skips, not failures.
3. **Protect what must stay.** Anti-cheat, recovery, TPM/BitLocker, certs,
   user tooling (e.g. `cua-driver`), and core shell helpers are never quieted.
4. **Detect matches Apply.** The UI must re-read live state with the **same**
   discovery helpers Apply uses (no weaker detect sample).
5. **Installed means usable.** “Installed” for a game means configs (or a clear
   “launch once” state) — not just an empty folder.
6. **Honest copy.** Status text is about *this PC* (“not installed here”,
   “3 adapters found”), never a marketing checklist of every possible tweak.

## Fixed catalogs vs live inventory

| Allowed fixed catalog | Must be live per PC |
|----------------------|---------------------|
| Game **title** list in the hub | Install path, config files, Steam libraries |
| Known **policy values** (e.g. GpuPreference=2) | Which EXEs get them |
| Quiet **classifiers** (name/path patterns) | Which tasks/apps match on this PC |
| Optional feature **names** to try | DISM presence/state before disable |

A fixed *catalog of product surfaces* is fine. A fixed *assumption that every
path exists* is not.

## Future: richer live brain

Today: deterministic live inventory + classifiers (no cloud).  
Later: optional local “advisor” that ranks next steps from the **same** live
inventory (still offline-first; no silent telemetry).

## Module ownership (who discovers what)

| Module | Live inventory |
|--------|----------------|
| **Windows** | OS keys, power plan, Task Scheduler tree, optional features |
| **Internet** | Physical NICs, link, DNS path, adapter properties |
| **NVIDIA** | GPU + displays via NVAPI helper |
| **Discord** | Stable/PTB/Canary under LocalAppData |
| **Steam** | steam.exe + all `libraryfolders.vdf` libraries |
| **Riot / Epic** | Uninstall registry, manifests, process paths, all drives when possible |
| **Brave** | Stable/Beta/Nightly exe paths, User Data / Default profile, extensions, live policies |
| **Games** | Per-title probes: Steam/Epic/Riot roots + real config files |

## Implementation checklist (Apply / Detect)

- [ ] Discovery helper shared by Apply and Detect
- [ ] Budget/timeouts so enumeration cannot hang the UI
- [ ] Report counts: found / quieted / skipped / protected
- [ ] No write when inventory is empty for that module (hard fail with install guidance)
