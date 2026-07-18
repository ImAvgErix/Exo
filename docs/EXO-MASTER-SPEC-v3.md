# Exo master implementation spec v3

You are Exo's lead Windows engineer and product designer. Improve the existing
repository in reviewable release slices. Preserve verified behavior, remove
folklore, measure claims, and prefer fewer reliable features over a larger set
of fragile tweaks.

This document is the implementation contract. Repository `AGENTS.md`, supported
platform documentation, and live code are additional sources of truth. When
they conflict, stop the conflicting change, record the conflict, and resolve it
in the same slice before implementation continues.

## Product outcome

Exo is a private, fast, dark-only Windows performance hub for Discord, Steam,
Internet, and NVIDIA. Riot and Epic may be added only through the safety kernel
defined below. The UI is modern and distinctive but restrained: pure-black page
canvas, crisp opaque lifted surfaces, white primary actions, small module-color
accents, clear status, and short motion.

Optimize the complete product, including scripts, service code, UI, startup,
background use, packaging, documentation, GitHub workflows, update safety, and
Repair. Do not optimize benchmark numbers at the expense of stability,
compatibility, privacy, image quality, data integrity, or truthful reporting.

## Decision hierarchy

Use this order when goals compete:

1. User data, connectivity, system integrity, and exact Repair ownership.
2. Anti-cheat, platform policy, signing, and account safety.
3. Correct detection and truthful status.
4. Measured latency, responsiveness, memory, startup, and package improvements.
5. Simplicity, visual consistency, accessibility, and maintainability.
6. Feature count.

Never claim “best,” “guaranteed,” “zero risk,” or a performance improvement
without scoped evidence. Say what was measured, on which environment, and what
remains unknown.

## Global invariants

### Mutation contract

Every system mutation must follow:

`detect live state -> validate eligibility -> snapshot owned state -> plan ->
apply -> verify live state -> run health canary -> commit result`

On apply failure, verification failure, cancellation after mutation begins, or
canary failure:

`stop new work -> roll back the current transaction -> verify restored state ->
show a truthful report -> preserve diagnostics`

- A mutation with no tested snapshot and Repair path does not ship.
- Repair restores the most recent valid pre-Apply snapshot for Exo-owned state.
- Snapshot and settings schemas are versioned. Reads remain backward compatible
  until a documented migration removes the old schema.
- State files are UX hints. Detect scripts read live system state.
- Apply and Repair are idempotent.
- Plans list every intended mutation before elevation; unexpected targets fail
  closed.
- Exo never creates its own background service, startup entry, scheduled task,
  watchdog, tray resident, or always-running helper.

### Connectivity contract

Internet Apply must have an offline rescue path and a bounded canary that checks:

- selected physical adapter still exists and is up;
- address/gateway state is sane for the adapter configuration;
- DNS resolves through more than one independent hostname;
- HTTPS succeeds to at least two documented endpoints when the environment
  permits it;
- the applied configuration matches the planned configuration.

Do not use a single transient latency value as a rollback trigger. When the
canary cannot distinguish local breakage from ISP, captive-portal, firewall,
VPN, or endpoint failure, report “verification inconclusive,” restore only if
connectivity/configuration checks failed, and keep the rescue action available.

### Security and privacy contract

- The desktop app runs `asInvoker`. Elevation is per action through one audited
  runner.
- Elevated code never executes a user-writable script or helper that has not
  been verified against the shipped manifest inside the elevated boundary.
- Downloads are HTTPS-only and fail closed unless integrity can be verified
  through a pinned or independently trusted digest. Verify Authenticode and an
  allowlisted signer when the vendor provides signing.
- Never ship `irm | iex`, self-modifying scripts, hidden remote execution, or
  silent fallback to an unverified asset.
- Declare every outbound endpoint, purpose, method, approximate data volume,
  retention controlled by Exo, and disable/skip behavior in `PRIVACY.md`.
- No telemetry, analytics, advertising identifiers, account scraping, login
  automation, or upload of hardware/benchmark data.
- Logs redact usernames, tokens, cookies, SSIDs, public IPs, and user paths when
  those values are not required to diagnose the action.

### Anti-cheat and application-policy contract

Exo may tune Windows, supported driver settings, launchers, and Discord client
settings. Exo never:

- mutates or reads game process memory;
- injects into a game or anti-cheat process;
- patches, replaces, renames, or deletes game payloads;
- stops, disables, reconfigures, or deletes Vanguard, VAC, EAC, BattlEye,
  FACEIT, or their services/drivers/files;
- automates input, macros, binds, SOCD/Snap-Tap, or self-bot behavior;
- ships or loads a kernel driver;
- applies process priority, affinity, memory priority, suspension, or termination
  to a game executable.

Process-targeting features use a centralized feature-specific allowlist plus a
central hard-deny check. Paths inside detected game libraries and known game or
anti-cheat executable names always lose to the deny check. Repository gates scan
source and generated scripts for injection, driver-loading, and input-automation
APIs.

Discord binary/client modification is not a default optimization. If an optional
third-party client modification remains, it requires a current policy review,
explicit informed opt-in, integrity verification, separate status, and one-click
stock restore. Exo never describes it as risk-free.

### Scope contract

The default scope is Discord, Steam, Internet, and NVIDIA. A Windows-wide setting
is allowed only when all of these are true:

1. it is directly required by an owned optimizer feature;
2. supported OS/vendor documentation or reproducible local evidence justifies it;
3. affected hardware/software is detected;
4. pre-state is snapshotted and exact restore is tested;
5. the UI identifies the system-wide effect before Apply.

Do not add general Windows debloat, power-plan, timer-resolution, scheduler,
security-feature, or unrelated service tweaks.

## Windows 11 compatibility contract

Exo is not tuned for one developer machine. Every optimizer must derive its plan
from a live capability profile and remain safe across materially different
Windows 11 PCs.

### Supported-machine model

- Support all currently supported public Windows 11 releases on x64. Test the
  oldest supported build, the current build, and the next release preview before
  claiming compatibility.
- Treat Windows Insider builds as detected best-effort environments: never force
  a missing API/property, clearly report skipped capabilities, and do not mistake
  an API difference for a failed optimization.
- Track ARM64 as a real architecture target. Do not claim “any Windows 11 PC”
  until the app, packaged PowerShell runtime, native helpers, installer, and all
  smoke tests have an ARM64 build. Until that gate is green, ARM64 must receive
  an explicit unsupported-architecture message rather than a partial x64 Apply.
- Do not assume English Windows, fixed drive letters, default install locations,
  one user, an administrator account, consumer Windows policy, or unrestricted
  PowerShell execution.

### Capability profile

Before showing recommendations or building an Apply plan, collect and normalize:

- OS edition, build, architecture, servicing channel, relevant API/cmdlet
  availability, policy/management state, and reboot-pending state;
- CPU vendor/family, physical/logical cores, hybrid topology when exposed,
  virtualization state, and laptop/desktop power context;
- GPU vendors/devices, NVIDIA architecture and driver branch when present, VRAM,
  hardware scheduling state, display topology, refresh rates, transports, and
  notebook/Optimus context;
- physical and virtual network adapters, active route, Ethernet/Wi-Fi, vendor and
  driver, negotiated link speed, RSS/RSC/offload support, available advanced
  property names/IDs, DHCP/static configuration, IPv4/IPv6, VPN, VLAN/team/bridge,
  Hyper-V/WSL/ICS, captive portal, and metered connection state;
- installed application channels, versions, install roots, running processes,
  active downloads/calls/games, multi-account conditions, and existing Exo
  snapshot schema;
- accessibility state, display scale, text scale, High Contrast, animation
  preference, and available work area.

Detection must be bounded, cancelable, cached for the current session where safe,
and refreshed before mutation. Sensitive values that are not required for policy
selection are not stored.

### Plan generation rules

- Optimizer policy is a capability matrix, not one universal preset. Each action
  declares required OS build/API, hardware/app match, conflicts, risk tier,
  snapshot fields, verification probe, Repair operation, and fallback.
- Use stable identifiers and vendor property IDs where available. Localized
  display strings are a fallback only after controlled matching; ambiguous
  adapter properties are skipped.
- Never manufacture support by writing an absent registry value or unsupported
  driver property. `unsupported`, `not installed`, `blocked by policy`,
  `temporarily busy`, and `verification inconclusive` are distinct states—not
  generic failures.
- Multi-GPU, multi-monitor, multi-NIC, docked/undocked, and VPN machines are
  planned per device. Exo never applies the primary device's assumptions to all
  devices.
- Laptops preserve battery, thermal, display-switching, sleep, and OEM control
  behavior unless the user explicitly opts into a documented performance cost.
- Managed/domain PCs respect enforced policy. Exo reports the policy owner and
  skips the conflicting action rather than repeatedly fighting it.
- Re-detect immediately before elevation and again during post-apply verification
  so hot-plug, driver updates, app updates, or route changes invalidate stale
  plans safely.

### Compatibility evidence

- Unit tests cover a matrix of synthetic capability profiles: Intel/AMD/Qualcomm,
  desktop/laptop, NVIDIA generations and no-NVIDIA systems, common NIC vendors,
  Wi-Fi/Ethernet from sub-gigabit through multi-gigabit, VPN/virtual adapters,
  static/DHCP DNS, multi-user paths, policy blocks, and missing/renamed APIs.
- Script generation tests assert that unsupported profiles produce no mutation
  for the unsupported feature.
- Maintain anonymized, hand-authored hardware fixtures in the repository; never
  upload a user's live inventory.
- Live E2E coverage uses a documented test matrix. A feature is default-on only
  for hardware/build combinations actually verified or supported by authoritative
  vendor documentation; other combinations remain safe-skip or experimental.
- Every release notes the tested Windows builds, architectures, GPU/NIC classes,
  application channels, and known limitations.

## Working method

### Baseline before change

For each release slice:

1. Rebase/merge the current main branch and preserve already verified changes.
2. Record repository status and current product version.
3. Run the existing repository, UI, relevant optimizer, build, and publish gates.
4. Capture current UI and relevant live-system state without mutating it.
5. Create a parity inventory: keep, replace, remove with reason, or defer.
6. Define the slice's acceptance checks before editing behavior.

### Research discipline

- Use current primary sources: Microsoft, NVIDIA, application vendors, standards,
  source repositories, and source code.
- Record each tweak in `docs/TWEAK-AUDIT.md` as `Shipped`, `Experimental`,
  `Enforcement`, `Excluded`, or `Unverified`.
- Each row includes target, eligibility, source/evidence, expected effect,
  side effects, snapshot, verification, Repair, anti-cheat verdict, and UI copy.
- Community reports may generate a hypothesis but cannot support a default
  performance claim.
- Exact registry/DRS values require live export verification on supported
  hardware or authoritative documentation.

### Change discipline

- Use small conventional commits grouped by one behavioral purpose.
- Do not combine a UI rewrite, runner rewrite, optimizer policy change, and
  release-pipeline change in one commit.
- Delete dead Exo code only after reference search and parity inventory.
- Preserve unrelated user changes.
- Do not bump the version until the release slice and release notes are defined.
- Do not publish or merge a slice until every applicable gate is green.

## Release slice 1: dark-only UI foundation

This is the visible priority. Optimizer policies and mutation scripts are frozen
except for fixes required to preserve their existing contract.

### Design system

Create composable resource files:

- `Styles/Tokens.Colors.xaml`
- `Styles/Tokens.Type.xaml`
- `Styles/Tokens.Metrics.xaml`
- `Styles/Controls/*.xaml`

Use one dark palette and one High Contrast accessibility dictionary. Do not add
a Light theme, Light dictionary, theme toggle, or “AMOLED” product copy.

Palette intent:

- page `#000000`;
- sunken `#050506`;
- base `#08080A`;
- card `#0E0E11`;
- raised `#111114`;
- overlay `#0C0C0C`;
- primary text `#F5F5F4`;
- secondary text `#D6D3D1`;
- muted text no darker than `#A1A1AA` on black;
- success `#34D399`, warning `#FBBF24`, error `#F87171`;
- primary action: stone-white fill with black text;
- Discord, Steam, Internet, and NVIDIA colors appear only in icons, rails,
  compact badges, or focus accents—not paragraph text or large fills.

All color literals live in the color-token file except OS-required manifest or
code fallback values explicitly allowlisted by the repository gate. Use theme
resources in templates and styles.

Use a 4 px spacing grid with named tokens. Allowed radii are 8, 12, 14, 16, and
pill. Default body text is 14/20; readable text is never below 12 px. Use Segoe
UI Variable, Semibold for emphasis, no decorative italics, integer font sizes,
and layout rounding on custom surfaces.

### Shell and interaction

- Preserve the top circular navigation concept with at most six destinations.
- Make the window resizable with minimum content size 960 x 600, clamp the
  initial size to the current display work area, and center content up to a
  maximum readable width.
- Use the supported title-bar integration and keep caption buttons unobstructed.
- Use one shared optimizer plate structure: identity/status, concise explanation,
  feature rows, live result/report, and a fixed action area.
- Every page uses the same verbs: `Analyze & Apply`, `Repair`, and `Refresh` only
  where Refresh actually re-reads live state.
- Settings remains a dark flyout/sheet consistent with the same tokens and
  surfaces. No transparency control.
- Avoid decorative metric cards that cannot be supported by live evidence. The
  dashboard answers: what is installed, what Exo changed, whether it is still
  applied, the last verified result, and the next useful action.
- Primary Apply actions are white. Repair and Refresh are quiet secondary actions.
- Internet keeps concise “what this does” information without exposing internal
  benchmark noise as product copy.

### Motion and performance

- Storyboard-only UI motion; no hand-off composition visuals.
- Entrance 200–250 ms, micro feedback 80–167 ms, exit at most 167 ms with fade.
- Only a progress indicator may animate indefinitely.
- Honor OS animation and High Contrast preferences. Reduced motion uses a tested
  no-animation path.
- No UI-thread blocking, layout animation, repeated image resampling, or startup
  network/disk work before first rendered frame.
- Defer updater checks, kit preparation, optional runtime checks, and large panel
  construction until after first frame or first use.

### Accessibility and UI acceptance

- Full keyboard traversal, visible focus, meaningful automation names, non-color
  status text, live-region progress/status, and text scaling remain enabled.
- Automated UI gates enforce token ownership, removed Light-theme UI, white
  primary actions, minimum hit targets, no forbidden composition APIs, and no
  unsupported startup acrylic.
- Record manual checks at 100%, 125%, and 150% scale; 125% text size; keyboard;
  High Contrast; and a Narrator Apply walkthrough. Do not claim a manual check
  that was not performed.
- Update the lightweight preview only as a layout QA companion. It does not prove
  real WinUI behavior.

## Release slice 2: safety kernel

No new system mutation ships before this slice is complete.

- Create an embedded manifest for shipped scripts/helpers with relative path,
  SHA-256, and length.
- Verify before elevation and re-verify inside the elevated boundary.
- Replace user-writable wrapper execution with one compiled-in bootstrap that
  accepts data as arguments, sets `ErrorActionPreference=Stop`, validates the
  interactive-user SID, locks module lookup to trusted machine paths, and writes
  protected logs.
- Route all elevation through the shared runner; remove direct hard-coded
  PowerShell elevation paths and silent re-run fallbacks.
- Use a protected machine store with validated owner/DACL and fail-closed repair.
  Treat installer/signing design as a separate reviewed threat-model decision;
  do not copy an SDDL from this spec without an ACL test.
- Cancellation terminates the elevated child tree from inside the boundary,
  then reports that Repair may be required if mutation began.
- Add a centralized verified-download helper and migrate each current download.
- Add early documented DLL search hardening without breaking packaged WinUI or
  shipped helper loading.
- Implement real NVIDIA Repair for every Exo-owned MSI, audio, service, task,
  Run-key, HAGS, and DRS mutation before adding more NVIDIA changes.
- Add single-instance activation and full fatal startup diagnostics with privacy
  redaction.

Acceptance: threat model, unit tests for manifest/plan/ACL validation, tamper
tests, cancellation tests, backwards snapshot tests, and real Apply/Repair smoke
on a supported Windows test machine.

## Release slice 3: testable core and CI

- Extract platform-independent models, serialization, policy, detection parsing,
  and script-plan generation into a plain .NET class library only where the move
  reduces coupling. Do not perform a mechanical move that creates wrappers with
  no test value.
- Add seams for paths/filesystem, registry planning, process execution, HTTP,
  time, and hardware/system probes.
- Keep WinUI, dispatcher, storage activation, and the real elevated runner in the
  Windows app layer.
- Add data-driven unit tests for policy tables, schema migration, JSON round
  trips, script AST/output, update validation, and Repair plans.
- Keep real PowerShell execution and live-system behavior in dedicated smoke/E2E
  gates, not unit tests.
- CI order: repository/static gates -> unit tests -> optimizer smokes -> release
  build -> package validation. Publish cannot run when an earlier gate fails.

## Release slice 4: measurement and Internet truth

Build the harness before changing network policy.

- Store append-only versioned benchmark runs and the latest UI summary separately.
- Record environment: Exo/OS version, interface type/link speed, VPN/virtual
  adapter state, gateway signal, local time bucket, endpoints, stream count,
  duration, bytes, and rejection reason.
- Use open-loop scheduled latency probes, separate download and upload load, ramp
  discard, p50/p90/p99, successive-difference jitter, and explicit loss.
- Use at least three complete runs per comparison arm. Show `inconclusive` unless
  a predeclared effect-size/noise rule is met. Statistical testing is secondary
  to effect size and is used only with a defensible sample count.
- Reject comparisons across stale baselines, adapter types, VPN state, material
  gateway shifts, or excessive unrelated traffic.
- Label ICMP as a path signal. Do not convert loaded-probe misses into idle packet
  loss.
- Benchmark the resolver transport actually applied. Include the current resolver
  as control. Change resolver only when it wins by a predeclared practical margin
  without worse tail behavior and Windows can verify the requested encrypted
  mode. Otherwise preserve the current resolver and explain why.
- `Analyze & Apply` chooses one balanced policy. Advanced mode may expose policy
  details, but the default UI does not ask users to choose “latency” versus
  “download speed.”

Each candidate tweak is applied one variable at a time on eligible hardware,
canaried, measured, and auto-restored if invalid or not beneficial. Enforcement
of a safe OS default is labeled as enforcement, not a speed improvement.

## Release slice 5: optimizer policy audit

Audit current features before adding new ones. Exact policy values live in data
tables with eligibility, source, snapshot, verification, and Repair metadata.

### NVIDIA

- Detect GPU family, notebook/desktop, VRAM, driver branch, display topology,
  refresh rate, transport, NVIDIA App/GFE state, supported DRS settings, HAGS,
  and live exported profiles.
- The G-SYNC/VRR choice is explicit. Do not infer the monitor OSD state from EDID,
  refresh rate, or DisplayPort.
- Keep global low-latency policy only where current NVIDIA documentation and live
  DRS verification support it. Reflex-capable games display an in-game guidance
  note; never claim Exo can enable Reflex through DRS.
- Apply API-specific settings only to APIs where they have an effect. Do not
  write or verify irrelevant pins.
- Prefer Maximum Performance is per-game unless the user explicitly accepts
  global desktop idle-power cost.
- Shader-cache, HAGS, MSI, telemetry, audio, service, and task actions require
  hardware eligibility plus exact live Repair.
- Never disable the display container required for NVIDIA Control Panel or DRS
  persistence, patch display drivers, strip signing/anti-cheat data, or ship an
  unsigned driver.

### Steam

- Reduce launcher background cost without working-set purges, suspension, hard
  memory caps, continuous Exo watchdogs, game-process targeting, or GPU-disabling
  CEF switches.
- Memory priority and foreground/game-aware policy may target Steam client
  helpers only through the centralized allowlist and must recover cleanly.
- Clear only proven orphaned or launcher caches. Do not delete installed-game or
  driver shader caches as routine optimization.
- Preserve downloads, login, libraries, cloud saves, controller configuration,
  and game launch behavior. Snapshot any edited VDF/registry value.
- Report actual helper count/RSS change when available; never promise a fixed MB
  reduction.

### Discord

- Keep hardware acceleration on by default. Offer an eligible explicit opt-out
  only when the UI explains the tradeoff and Repair restores the prior value.
- Do not ship switches known to blank or destabilize current Electron/CEF.
- Do not remove first-party voice/noise modules as a performance claim.
- Keep stock-safe settings separate from optional third-party modification and
  its consent/restore contract.
- Preserve updates, voice, screen share, notifications, login, and crash recovery.

### Internet

- Keep documented safe defaults and vendor-specific adapter changes only when
  live property mapping is verified.
- Do not ship experimental autotuning, forced MTU/jumbo frames, BBR variants,
  DNS cache TTL overrides, DNS service-provider priority folklore, timer tweaks,
  or undocumented congestion/scheduler disables.
- Multi-gigabit adapters get interrupt/DPC guardrails; do not assume the most
  aggressive latency setting scales safely to 5/10 GbE.
- DSCP and TCP-only changes use honest scope copy and never claim universal game
  ping improvement.

## Release slice 6: Riot and Epic housekeeping

Add these only after slices 2 and 3 are green. These pages optimize launchers and
housekeeping, not games.

### Riot boundary

- Read-only health can report Riot/Vanguard presence and supported security
  prerequisites without treating regional variants as broken.
- Safe actions may close allowlisted Riot client UI processes when no protected
  game session is active, restore/remove the client startup value, clear only
  validated old log/crash paths, and create reversible launcher shortcuts.
- Never touch Vanguard services/drivers/files, game/client payloads, game memory,
  security prerequisites, endpoints, firewall blocks, or protected sessions.
- DNS and generic Windows tweaks are not marketed as VALORANT ping fixes.

### Epic boundary

- Safe actions may restore/remove launcher startup, guardedly close launcher UI
  processes when no game/download is active, clear validated launcher web/log/
  crash caches, and reversibly move only a vendor-supported overlay file set.
- Never mutate EOS service configuration, manifests, secrets, login data, game
  binaries, saves, or game cache paths.
- UI claims are limited to measured launcher process/RAM change, conflict repair,
  and bytes reclaimed—not FPS.

Both modules require not-installed states, versioned snapshots, exact Repair,
process hard-deny coverage, and real smoke tests.

## Release slice 7: startup, size, docs, and release

- Publish reports top files, extension rollup, compressed package size, and
  first-interactive timing. Commit history per release.
- Strip release symbols from the public package while retaining private symbol
  artifacts where configured.
- Fail if known unused ML/AI payloads return.
- Compare compression and ReadyToRun choices with measured package/startup data;
  keep the better product tradeoff. Do not use UPX or require a separately
  installed runtime for the default package.
- Restore and write real README, LICENSE, SECURITY, and CONTRIBUTING content.
- Keep `PRIVACY.md`, `TWEAK-AUDIT.md`, screenshots, and changelog synchronized
  with shipped behavior.
- Release preflight requires VERSION/project/changelog/tag agreement, installed
  GitHub tooling, a clean intentional diff, green gates, package checksum, and
  explicit release notes. Old release deletion is opt-in only.
- Signing and attestations are mandatory once credentials/infrastructure exist;
  until then the release UI/docs truthfully identify unsigned development builds
  and the pipeline cannot claim signed provenance.

## Global ship gates

A slice is done only when all applicable items are true:

- clean Release build with zero warnings;
- repository/static checks green;
- unit, UI, and affected optimizer smokes green;
- Apply, repeat Apply, cancel, Repair, repeat Repair, and offline rescue tested
  for each changed mutation path;
- publish/package succeeds and installs over the prior supported version;
- cold launch, second launch, update check, navigation, settings, and uninstall
  tested on Windows;
- real UI inspected at required scale/accessibility settings;
- no unexplained new outbound endpoint or persistent background component;
- performance claims include reproducible evidence or are removed/labeled;
- changelog, privacy, security, tweak audit, and overhaul summary updated;
- branch is pushed, CI is green, review conflicts are resolved, then merge and
  release are performed intentionally.

## Required final report

Report:

1. shipped behavior and deliberate removals;
2. files/architecture changed;
3. automated and manual tests actually run;
4. live-hardware results and environment;
5. before/after performance and package measurements;
6. Repair/rescue evidence;
7. remaining unverified, experimental, blocked, or externally dependent items;
8. commit, pull request, merge, release, checksum, and installation status.

Never substitute “should work” for a missing test and never mark an externally
blocked item complete.
