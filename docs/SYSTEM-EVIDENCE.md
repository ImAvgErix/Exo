# Whole-machine optimization — what is actually reachable

Scope: every component, physical and OS. Same bar as every other module — a lever has to be
**effective**, **durable**, and **honestly reported**. This document exists because "optimize
everything" has a real boundary, and the useful thing is to say exactly where it is rather
than ship a module that pretends there isn't one.

Three tiers, and every row below is assigned to one:

- **APPLY** — Exo can set it from software, verify it, and undo it.
- **ADVISE** — it lives in firmware. Exo can *detect* it and tell you precisely what to
  change and where, but cannot set it. These are often the biggest wins on the machine.
- **REFUSE** — cannot be done safely from software, or cannot be done at all. Named here so
  nobody re-litigates it, and so the UI never implies otherwise.

---

## GPU (NVIDIA)

| Lever | Tier | Notes |
|---|---|---|
| Display: refresh, colour range, bit depth, scaling | **APPLY** | Fixed in 4.3.4 — the whole path had been wired to nothing. |
| DRS 3D profiles (Reflex/ULLM, prerendered frames, power mode, shader cache, texture filtering) | **APPLY** | Already shipping, verified by DRS re-export diff. |
| Power limit → max | **APPLY** — shipped 4.4.0 | NVAPI power policy. Exo requests the board's own reported maximum, never a number of its own, and reads the value back — a request the driver clamps is reported as not applied. On a locked board (most laptops, many Founders cards) the maximum *is* the default, which is reported as "already at ceiling" rather than as a win. |
| Thermal limit | **APPLY** — shipped 4.4.0 | NVAPI thermal policy, same bounded pattern: the board's reported maximum, read back after writing. |
| Fan: return a pinned cooler to driver control | **APPLY** — shipped 4.4.0 | Detects a cooler left on `CoolerPolicy.Manual` by another tuning tool — a fan pinned low is a silent thermal throttle — and hands it back to the VBIOS curve. This write can only raise cooling. |
| Fan: custom curve | **REFUSE for now** | NVAPI exposes it (`SetCoolerLevels`, `SetCoolerPolicyTable`) and it is the one lever here whose failure mode is thermal damage. Shipping a curve that has never run on real hardware can undercool a card. Exo will hand a fan back to the driver but will not take it over. Revisit only with hardware to test on. |
| Core / memory clock offset (OC) | **APPLY, opt-in + validated** — not shipped | NVAPI pstates20. Needs a stress-validate-and-revert loop to be applied honestly; a fire-and-forget write is shipping instability. Gated out in `Nvidia.Smoke` until that loop exists. |
| Undervolt | **APPLY, opt-in + validated** — not shipped | On NVIDIA this is a V/F curve operation — practically: positive core offset plus a power cap, which lands the same clock at lower voltage. Same validation requirement as OC. |
| Resizable BAR | **ADVISE** | Readable, but enabling it needs UEFI (plus Above 4G Decoding). Commonly off. Real gains in several engines. |

## CPU

| Lever | Tier | Notes |
|---|---|---|
| Processor **max** state, boost mode | **APPLY** — shipped 4.4.0 | Set inside a **named Exo power plan** duplicated from Balanced, not written onto the plan the user chose. Mains profile only. |
| Processor **min** state → 100% | **REFUSE** | The classic "max performance" pin, and it is not one. It raises idle clocks and temperature continuously, and on any CPU that boosts in single-digit milliseconds — everything since Skylake and Zen — it buys no frametime. On a thermally limited laptop it actively costs boost headroom. Excluded, and the exclusion is pinned in `Contracts.Smoke` so it cannot drift back in. |
| Core parking (`CPMINCORES`) | **APPLY** — shipped 4.4.0 | Unparking helps frametime consistency on high-core-count chips. |
| Utility distribution (`DISTRIBUTEUTIL`) | **APPLY** — shipped 4.4.0 | Off. It spreads load thinly so cores *can* park, which works against the row above. |
| Hybrid thread scheduling (`SCHEDPOLICY`, `SHORTSCHEDPOLICY`) | **APPLY** — shipped 4.4.0, Intel hybrid only | The one genuinely CPU-specific pair. Short bursty threads — what a frame loop is made of — are pinned to P-cores; longer work prefers them but may spill to E-cores. These settings only *exist* on heterogeneous parts, and Exo gates them on measured `EfficiencyClass`, not on a parsed model name: an i5-14400 and an i9-14900K are both hybrid and neither string carries the core split. |
| AMD cross-CCD game placement | **ADVISE** | Real, and no power setting addresses it. Detected by counting L3 domains. Windows decides die placement from the AMD chipset driver, so Exo reports the topology and points there rather than pretending a `powercfg` value fixes it. |
| PCIe link power management (ASPM) | **APPLY** — shipped 4.4.0 | Off on mains. Stops the GPU and NVMe links sleeping. |
| USB selective suspend | **APPLY** — shipped 4.4.0 | Off on mains. No wake-up delay on mouse and keyboard. |
| Per-device USB power opt-in (`MSPower_DeviceEnable`) | **APPLY** — shipped 4.4.6 | The Device Manager checkbox "Allow the computer to turn off this device to save power", in `root\wmi`. Distinct from the row above: that one is global power policy, this is the per-device opt-in, and a device with it ticked can still be suspended whatever the plan says. Scoped to `USB\` instances and no others — the same class covers PCI NICs, and NIC power management belongs to the Internet module. Recorded per device before the change, so Repair restores the ones that were ticked rather than blanket-enabling. |
| Fullscreen optimizations (`GameDVR_FSEBehavior`, `GameDVR_FSEBehaviorMode`) | **APPLY** — shipped 4.4.6, a de-tweak | Left **on** (0, the Windows default). Nothing changes on a stock machine; the point is the machine where a guide set 2 to "disable fullscreen optimizations". That forces legacy exclusive fullscreen, gives up the flip-model present path, and costs frame pacing and latency on a modern compositor while breaking alt-tab and overlays. Both spellings are written because Windows has shipped both and reads whichever it finds. Not paired with `GameDVR_HonorUserFSEBehaviorMode`, which only matters when honouring a request to *disable* FSO. |
| Idle demote/promote thresholds | **APPLY** — not shipped | Same subsystem and same safety, but the right values are machine-specific and Exo has no measurement to choose them from. Left alone rather than guessed at. |
| C-states, PBO / Curve Optimizer, SMT | **ADVISE** | Firmware. Exo can read the current state and tell you what to set. |
| CPU undervolt (MSR 0x150 / AMD SMU mailbox) | **REFUSE** | Requires a signed kernel driver writing model-specific registers. Intel closed this after Plundervolt; shipping a ring-0 driver to undervolt is not a risk Exo takes. Use your board's firmware. |
| `Win32PrioritySeparation` | **REFUSE** | Already banned in the apply rails. Real registry key, cargo-culted value, no measurable frametime effect. Stays banned. |

## Memory

| Lever | Tier | Notes |
|---|---|---|
| XMP / EXPO enabled | **ADVISE** | **The single most common large miss on a gaming PC.** RAM running at JEDEC base instead of its rated profile costs real frames in CPU-bound titles. Detectable by comparing configured speed to the SPD rated speed. Firmware to fix. |
| Timings / sub-timings | **ADVISE** | Firmware. |
| Paging file configuration | **APPLY** — not shipped | Real when a machine is set to a tiny fixed page file, but the fix depends on how much RAM and free disk the machine has. Needs a measurement pass before it can be applied honestly. |
| Memory compression / SysMain | **APPLY, measured** — not shipped | Only where the machine has RAM headroom; on a 16 GB box disabling compression hurts. Requires live memory-pressure input Exo does not yet feed into this module, so it is left alone rather than applied blanket. |
| Standby list "cleaning" | **REFUSE** | Folklore. The standby list is cache; dropping it makes the next reads slower. No. |

## Motherboard / firmware

| Lever | Tier | Notes |
|---|---|---|
| Above 4G Decoding, Resizable BAR, XMP/EXPO, C-states, PBO, fTPM stutter fix | **ADVISE** | All firmware. Exo reads what it can and gives exact instructions. |
| Chipset / storage driver currency | **APPLY (chipset software)** — shipped 4.5.4 | AMD Chipset Software / Intel Chipset Device Software: three-stage check → prepare (strip optional apps) → install, same consent model as NVIDIA. Catalog targets live in `Exo/Data/chipset-catalog.json`. Vendor CDNs often block automated downloads — drop folder + official support page are first-class paths, not afterthoughts. Never flashes BIOS. Storage vendor packs still **ADVISE**. |
| BIOS version | **ADVISE** | Read it; never flash it. Flashing from an app is how machines die. |

## Storage

| Lever | Tier | Notes |
|---|---|---|
| NTFS last-access, 8.3 name creation | **APPLY** — shipped 4.4.0 | Small but real and free. Set through the registry values `fsutil` edits, so Exo can verify and undo them the same way as everything else. |
| TRIM enabled | **APPLY** — shipped 4.4.0 | Verified, and enabled if off. |
| Write caching | **ADVISE** | Per-device, and on a drive without power-loss protection turning it on trades data safety for speed. Not a decision Exo makes for you. |
| Defrag on SSD | **REFUSE** | Windows already does the right thing. Forcing it wears the drive. |

## Fans / cooling

| Lever | Tier | Notes |
|---|---|---|
| GPU fan: hand a pinned cooler back to the driver | **APPLY** — shipped 4.4.0 | See the GPU table above. |
| GPU fan: custom curve | **REFUSE for now** | See the GPU table above. Failure mode is thermal, and there is no hardware to validate a curve against. |
| CPU / chassis fans | **ADVISE** | Motherboard EC. Vendor-specific WMI at best, and writing EC registers blind can stop a fan. Exo will read what it can and tell you to set the curve in firmware or the vendor tool. |

## PSU

| | |
|---|---|
| **REFUSE — nothing to optimize.** | A power supply has no software surface. What is actually adjacent, and *is* covered, is Windows power **policy**: PCIe link state power management, USB selective suspend, and the disk/processor idle settings above. If a tool claims to "optimize your PSU", it is doing one of those and mislabelling it. |

## Windows / OS

| Lever | Tier | Notes |
|---|---|---|
| Hardware-accelerated GPU scheduling (HAGS) | **APPLY** — shipped 4.4.0 | Real, and required for some Reflex paths. Takes effect on reboot, and a driver that does not support it ignores the value — so the row says "pending reboot" rather than claiming a win at write time. |
| Game Mode | **APPLY** — shipped 4.4.0 | Real on modern builds. |
| Game DVR / background recording | **APPLY** — shipped 4.4.0 | Real capture overhead when left on. Three keys, because Windows reads three: the per-user capture toggle, the game-config store, and the machine policy that pins both. |
| MMCSS `SystemResponsiveness` | **APPLY** — shipped 4.4.0 | Set to 10. `0` stays banned — starving background work entirely causes audio dropouts, which is the opposite of the point. |
| MMCSS `NetworkThrottlingIndex` | **APPLY** — set to `10`, the OS default | 4.4.0 shipped this as `0xFFFFFFFF` ("disabled"), on the reasoning that the throttle caps non-multimedia traffic while an MMCSS task is running. That reversed a call this project had already made: 3.13.7 removed `ffffffff` as folklore, `docs/TWEAK-AUDIT.md` and `docs/INTERNET-GOLDEN-PATH.md` both specify `10` because `ffffffff` can raise DPC latency and cause audio dropouts, and `NetworkLogic.ForbiddenApplyPatterns` bans both `-1` and `4294967295` outright — a ban Contracts.Smoke enforces, but only against PowerShell text, so the C# lever walked past it. It also made Steam and System overwrite each other on the same key with no order in which both could read applied. Settled at `10`: one owner (this module), one value, and every other part of the repo already agreed on it. |
| Power throttling (EcoQoS) `PowerThrottlingOff` | **APPLY** | Was written by Steam's host-latency restamp with no snapshot, no detect row and no repair path anywhere — a machine-wide key Exo set and could not account for. Now a normal lever here, snapshotted and reversible like the rest. |
| MMCSS per-task `Games` priority values | **REFUSE** | `GPU Priority=8`, `Priority=6`, `Scheduling Category=High` are already the Windows defaults for that task. Writing them is restoring a default dressed up as a tweak. |
| Power throttling (EcoQoS) for background apps | **APPLY** | Already used by the Steam guard; generalise. |
| MSI mode + interrupt affinity (GPU, NIC, storage) | **APPLY** | Already done for the GPU; extend. |
| VBS / HVCI / Memory Integrity | **APPLY, consent-first with a real warning** | Measurably costs frames in some titles. It is also a genuine security feature. Exo must state the trade-off plainly and default to leaving it alone — this is the user's call, not a silent optimization. |
| Timer resolution / `useplatformclock` | **REFUSE** | Semantics changed in Windows 10 2004+. The classic advice is now folklore or actively harmful. |
| Telemetry / service debloat | **APPLY, narrow** | Only services with a named, verifiable cost. Mass service-disabling is how machines break. |

---

## The honest summary

The biggest single wins on a typical gaming PC are **XMP/EXPO off** and **Resizable BAR off**,
and both are firmware — Exo cannot set either. It can tell you, precisely, that they are off
and what to change. A tool that quietly skips them because it cannot fix them is hiding the
two things that would help most.

Everything Exo *can* set, it sets, verifies, and can undo. Everything it cannot, it names.
