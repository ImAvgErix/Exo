## 1.0.1

**Distinct Exo Hub brand mark.**

- New app icon and logo: cyan orbital hub (no longer the shared monogram).
- README brand mark; favicon update for the WebView shell.

## 4.8.14

**AMD chipset NVCleanstall-style silent install on Apply.**

- AMD Apply (Ryzen, no Radeon): unpack package → strip junk → pnputil core W11 INFs + silent MSI → re-enable disabled devices (fixes SMBus Code 22).
- No interactive installer UI; uses cached/drop package or MUC prepare.
- Detect requires package + healthy PSP/SMBus (not log text alone).
- This machine: SMBus 5.12.0.44 OK, PSP 5.46 OK after clean path.
## 4.8.13

**Live AMD chipset newest + status dots not brand-red.**

- AMD "newest" is resolved live (Microsoft Update Catalog + AMD support pages), cached 12h; catalog is the offline floor.
- Status dots: green for OK/info (CPU), red only for real misses — not the AMD logo red on every row.
## 4.8.12

**NVIDIA status rows honest; AMD chipset 8.xx; dashboard cleanup.**

- NVIDIA: Base-only DRS status exit 3 no longer means Drifted; full Apply lights profile/game rows green (fixes SS: Applied badge with grey profile rows).
- AMD: catalog target **8.07.16.1035**; detects package version from install logs (not INF 5.17).
- Dashboard: short CPU (no 6-Core Processor); OS card without Exo version.
## 4.8.11

**AMD chipset detection + NVIDIA stays Applied.**

- AMD: detect real `AMD Chipset IO Drivers` install (registry), not only Add/Remove package names. No more fake "newest 7.01" against INF driver versions.
- AMD: Applied when chipset IO drivers are installed; short CPU name (5600X).
- NVIDIA: detect no longer demotes full Apply to Partial because Base-only DRS status has 0 game profiles; honor `applyStatus=applied`.
## 4.8.10

**NVIDIA full applied, AMD chipset currency, short CPU, centered icons.**

- NVIDIA: native DRS write-time verify counts as Base Profile verified (no more Partial after 46/46 game profiles).
- AMD (CPU only): shows short CPU (e.g. 5600X) + chipset driver only; Applied only when newest AMD Chipset Software is installed.
- Module rail icons are truly centered in the title bar.
## 4.8.9

**NVIDIA integrity fix, real numbers on module pages, no minimize.**

- NVIDIA Apply: regenerated shipped script manifest (length mismatch after 4.8.8 optimizer edit).
- Module Status list shows title + detail with driver/BIOS/chipset numbers (not titles only).
- AMD: CPU, board/BIOS, and chipset package (or inbox device count) listed for chipset-only PCs.
- Windows: stop leading with vague firmware noise; only real UEFI hits + board/BIOS identity.
- Removed minimize caption button.
## 4.8.8

**NVIDIA per-game profiles + AMD chipset-only honesty.**

- NVIDIA: native DRS now creates/writes per-game profiles (FindProfileByName threw before, so every app profile failed). Settings land on the stock profile that owns each game exe when one exists.
- NVIDIA: Profile Inspector fallback live-verifies per-game profiles via NVAPI status instead of always marking Partial.
- AMD: Ryzen + NVIDIA (no Radeon) is chipset-only — clear copy, no fake Radeon debloat claims; Apply is a clean no-op.
- Other optimizers audited: Discord/Steam/Windows applied OK; Brave/Spotify correctly missing when uninstalled.
## 4.8.7

**Text size scales type only; dashboard cards unstretched.**

- Text size no longer zooms the whole window — only font sizes change.
- Home meters are content-sized again (not stretched to fill the window).

## 4.8.6

**One-button Update with percent only.**

- Settings gear: single **Update** button — checks, downloads, and installs automatically.
- While updating, the button shows **only a percent** (no step text).

## 4.8.5

**Missing apps greyed out, NVIDIA apply fixed, text color/size work.**

- **Missing apps.** Brave / Discord / Steam report MISSING when not installed and show greyed on the rail (not hidden).
- **NVIDIA apply.** `Nvidia/tools/*` (Exo.NvDisplay FDD) is allowed runtime material so integrity no longer blocks Apply.
- **AMD copy.** Chipset-only machines get honest Radeon-vs-chipset messaging (chipset is info-only).
- **Apply UI.** Progress shows **percent only** — no step text, no icons on Apply/Verify/Repair.
- **Text color / size.** Labels use American spelling; changing them updates ink and UI zoom for real.

## 4.8.4

**Larger fixed canvas — 1400×900.**

- Window still forced (not resizable / not maximizable), sized **1400×900** so home meters and module pages breathe.
- UI spacing restored to match the bigger frame (header, cards, module detail).

## 4.8.3

**Fixed window, no scroll, caption controls.**

- **Forced canvas.** Window is locked at **960×640** (not resizable / not maximizable). Home and module pages are authored to that size with **zero scroll**.
- **Caption.** Settings + **Minimize** + **Close** on the right of the rail (Home stays left; modules stay center).
- **Compact layout.** Tighter header, meters, and module detail so everything fits the fixed frame.

## 4.8.2

**Clickable optimizers + settings menu is self-contained.**

- **Module rail clicks.** Native WinUI drag strip no longer covers the icon plates (height 8, side margins only). Module buttons stay clickable even before dashboard state lands.
- **Settings.** Gear menu owns View logs, Check for updates / Install, text colour/size — no more opening the old SettingsPopover.
- **Old UI removed.** Shell, HomePage, SettingsPopover, ProfileSheet, UpdateOffer, BrandMark, StartupServicesPanel deleted from the product tree.

## 4.8.1

**AMOLED tweaks UI + optimizers fixed.**

- **UI.** Grok Build AMOLED shell: home resource meters, brand icon-plate module nav, module detail with Apply / Repair / Verify, settings gear with **View logs**.
- **Optimizers.** Elevated Apply/Repair no longer fails with `The filename or extension is too long` — bootstrap launches via `-File` on a short path instead of a multi-KB `-EncodedCommand` (ShellExecute ~8191 limit).
- **Kits.** Shared `scripts/lib` sync includes `Exo.RunHidden.vbs` (was `*.ps1` only), which blocked integrity on Discord/Steam/NVIDIA deep packs.

## 4.8.0

**A hardened, catalog-driven foundation under the same shell.**

- **Privileged execution is hardened end to end.** All PowerShell launches resolve a trusted host (Program Files / AppX / Exo-managed portable, Microsoft-signed, no PATH, no reparse points) and the elevated path revalidates it right before `runas`. Shipped scripts execute from an ACL-protected staged payload after the full module + shared-library closure is verified against the signed manifest by length and SHA-256, re-hashed inside the elevated bootstrap. The `DISCOPT_SKIP_MANIFEST` integrity bypass is gone from the host and the Discord runner. `tools/Security.Smoke` (33 contracts) gates this in CI.
- **Updates are check-only until you consent.** `updates.peek` reads GitHub and never downloads; installing is a separate explicit action that names the version and release summary first. A failed check is an error, never "you're on latest". Launch-time checking is opt-in.
- **Repair, Verify and Stop are first-class.** Every module row can force a re-detect, repair restores available snapshots with a confirmation that says the restore can be partial, and a long Apply/Repair can be stopped without corrupting the run's result reporting.
- **A real tweak engine.** `TweakCatalog` is wired into the composition root with the System module's 22 registry levers as the single source of truth (`system.*`), plus eight evidence-based privacy levers (`privacy.*`), each with Detect → Plan → Snapshot → Apply → Verify → Restore, catalog validation, and an observational tracer that refuses mutating plans. `tools/Engine.Smoke` exercises the whole lifecycle against an in-memory registry store — including CRLF normalization parity.
- **Versioned tweak profiles.** `TweakProfile` models a profile as a versioned collection of catalog bindings with construction-time validation (unknown ids, duplicates, non-positive versions fail fast).
- **Startup, services and storage management.** A native panel lists Run-key/Startup-folder entries (toggle moves values to Exo's disabled backup key — nothing is deleted, every change is snapshotted), enumerates services through the Service Control Manager API (no WMI), scans and cleans known temp roots with a deletion journal, and shows a read-only system diagnostics summary.
- **The NVIDIA profile path is native-first and verified.** `Exo.NvDisplay --drs-apply` writes every `.nip` setting through NVAPI with per-setting read-back; Profile Inspector is a documented fallback only, with a concrete removal gate. Recorded live: 70/70 settings verified on an RTX 3070, backup/restore byte-exact.
- **The 2678-line bridge is split.** `WebHostBridge` is now per-domain partials (dashboard, settings, updates, drivers, modules, startup) with identical behavior; the smokes scan all partials so the string contracts hold across the split.
- **Immutable releases.** Stable and prerelease workflows build one installer from the tagged SHA, publish that exact artifact, and keep write permission isolated to the publication job.

## 4.7.1

**The optimizer cards stop growing with the window.**

- **Fixed: cards inflated on bigger windows.** The eight optimizer rows were `1fr` and absorbed every pixel the telemetry band left over, while the band itself is content-sized and never grew. So the ratio was purely a function of window height — a card was 2.8x a metric tile at 700px tall, 1.5x at 1080, and close to 1:1 maximized on a 1440p panel. Rows are a bounded height now and the slack goes to the gutters, so the list still spans the window and still never scrolls. At the 1200x800 default the card drops from 66px to 43px and the metric tile is unchanged at 149px.
- **Fixed: clipping at the minimum window size.** The telemetry band's padding was a flat 30px, which on a 620px-tall window — the host's own minimum — pushed the eighth card off the bottom. It scales now, and resolves to the same ~30px at the default size.
- **Removed: the second settings surface.** A native WinUI settings sheet, its view model and the host's open/close animation glue were still in the tree, hanging off a `Visibility="Collapsed"` zero-size button no user could reach. Nothing but this repo's own smoke-test string pins kept it alive. Settings is the React popover and now only the React popover, so a change to it cannot land in one of two places and be invisible in the other.
- **CI: Discord e2e flake.** The install step waits for Discord to fetch its modules on first run, but Squirrel auto-launches the client, so the step's own launch branch never fired — and when that auto-launched instance came up wedged, nothing restarted it and the job burned the full eight minutes before failing. It restarts the client once at the four-minute mark instead. The deadline is unchanged.

## 4.7.0

**Official brand marks, an AMD row that tells the truth, and a much smaller repo.**

- **Real logos.** Every optimizer row carries its official full-colour mark (AMD, Brave, Discord, NVIDIA, Spotify, Steam, Windows) instead of a monochrome glyph subset. Bundled as assets, so `Exo/wwwroot` fell to 536 KB — less than half the 4.6.0 package.
- **AMD covers both halves of AMD.** A Ryzen box with a GeForce card used to read NOT INSTALLED, which is not true of that machine. The row now detects the CPU as well as the GPU, reports the installed chipset driver for information, and explains itself instead of claiming absence. Chipset and other read-only findings no longer hold a module at "partial" forever for not being applied.
- **Bigger cards, smaller rows.** Each optimizer is its own separated card; the telemetry band keeps its size while the rows within tightened up. The wordmark in the title strip lost its hexagon.
- **Games is gone.** The Games hub, its RPCs, its service and every branch that fed it are removed from the host, the bridge and the UI — not hidden, deleted.
- **Repo debloat.** Ten unused icon assets, five orphaned logos, a duplicate `ui/public/logos` tree, six status/backlog docs and the separate CONTRIBUTING file are gone. The evidence docs live code actually cites — TWEAK-AUDIT, SYSTEM-EVIDENCE, INTERNET-GOLDEN-PATH — stay.
- **README matches the app.** It described Detect/Apply/Repair buttons on every module, AMD chipset updates and NVIDIA clean driver installs — none of which this shell has. It now describes the screen that ships, states plainly that Repair has no button, and carries the no-folklore rule inline.
- **Fixed: stale script manifest.** `ShippedScriptManifest.g.cs` had recorded the wrong length for `Discord/VERSION` since 5d0aa79, failing the freshness gate on every CI run on main for eight runs.
- **Fixed: fourteen recovered fixes.** PR #108's changes had never reached main; they are cherry-picked in, keeping main's independently better fixes for AMD apply.

## 4.6.0

**Exo becomes one screen.**

- **One screen.** A title strip, a live telemetry band, and one row per optimizer, each with its own button. The nine-item nav dropdown, the nine module pages, Driver Center, Games and "Verify all" are gone. NVIDIA and Internet ask which profile in a sheet after the button is pressed; the rest run immediately.
- **DISK telemetry.** The band reads CPU, GPU, MEMORY and the system drive.
- **Text colour and size.** White/Grey and Small/Normal/Large, persisted by the host and applied before first paint so a grey/large setup never flashes white.
- **Honest rows.** A row's state comes from the host's own status after an apply, not from the fact the call did not throw — a partial or failed apply reads STUCK with a Retry, never ON.
- **Smaller package.** Brand marks ship as a six-glyph webfont subset; `Exo/wwwroot` is 1.2 MB, down from 1.4 MB.
- **Note.** Repair has no button in this shell. `module.repair` is still on the bridge and typed on the client, so undo remains available to the engine and re-exposing it is a UI change alone.

## 4.5.8

**Tauri 2 + Rust + Svelte 5 migration foundation on main (WinUI host still the default ship).**

- **Native desktop path.** `apps/desktop` Tauri 2 shell + `exo-core` / `exo-modules` with Detect → Snapshot → Apply → Verify → Repair for Games, Home, System, Internet, NVIDIA, Discord, Steam, Brave, Spotify.
- **Games hub.** Multi-title Potato/Optimized profiles (display left default); kit staging to `%LocalAppData%\Exo\scripts`.
- **NVIDIA.** Deep detect + apply via `Exo.NvDisplay` (display, GPU, DRS).
- **Internet.** Host path + NIC latency pack + Wi-Fi band prefer.
- **CI.** `desktop-native` job, chipset catalog shipped, smoke hardened without requiring NvDisplay.exe at CI, Discord kit **1.3.88**.
- **Docs.** Migration checklist and status updated; AGENTS.md restored for contracts smoke.

## 4.5.7

**PowerShell 7 Preview preferred. Never uninstall Terminal Preview or PowerShell Preview.**

- **Dependency doctor** no longer uninstalls `Microsoft.PowerShell.Preview` or `Microsoft.WindowsTerminal.Preview` on install/update.
- **Host resolution** (app + Discord / Steam / NVIDIA scripts) prefers Preview, then stable. Windows PowerShell 5.1 still rejected.
- **Install hints** and auto-install try Preview first via winget, then stable, then portable stable zip.
- **SteamLogic** no longer forbids Preview winget IDs (that conflicted with the new policy).
- Shared `Get-ExoPwsh` in `Exo.Common.ps1` for consistent resolution.
## 4.5.6

**Discord Optimizer actually boots after Apply (Equicord loader + live boot check).**

- **Equicord loader asar.** Direct install now writes an Equilotl-compatible `app.asar` stub (no 4-byte JSON padding). The old padded stub made Discord exit 1 immediately while the disk verify still passed.
- **Loader validation.** `Test-EquicordLoaderPatched` and a new byte self-check reject corrupt/padded stubs before Discord is opened; Apply rewrites a bad loader instead of shipping it.
- **Equilotl.** Install runs with redirected stdio and `-branch stable` so the CLI cannot hang on the interactive menu and force the broken direct path.
- **Live boot check under Exo.** Exo Apply no longer treats "files exist on disk" as success. After Apply it opens Discord, proves a real page loads (or layered-rolls back kernel/mods), then closes it again.
- **Kit.** Discord kit version **1.3.88**.

## 4.5.5

**Local test ship: full-auto chipset + flow polish.**

- **Chipset.** Fully automatic download via Microsoft Update Catalog + Windows Update; strip + install with three yeses. Offered once per scan for AMD/Intel CPUs (not only when System is in the queue). No drop-folder primary path. Progress narrates long downloads.
- **Orb.** Chipset voice/progress wired; long driver jobs show stage lines so the brain does not look hung.
- **Honesty.** Chipset does not re-nag every session when catalog target is already met unless Windows Update has pending packages.
## 4.5.4

**Close the remaining honesty gaps - nothing soft left on purpose.**

- **CPU chipset drivers (AMD + Intel) — fully automatic.** Same three yeses as NVIDIA (check → prepare → install). Prepare downloads itself via **Microsoft Update Catalog** + **Windows Update** (vendor CDNs block bots; we do not ask you to drop files). Strips Ryzen Master / DSA-class junk; installs via silent setup, pnputil INFs, or WUA. Orb asks before System apply and from wrap-up. Never flashes BIOS.
- **System / Spotify.** Post-apply live Detect; fail if levers did not stick.
- **Steam.** Deep pack launcher matches native: CEF+/HIGH only, **no** wscript guard start. Soft-fail deep pack annotates state (`deepPackOk=false`, applyReport partial) and no longer finishes the log as a clean win.
- **Games.** CS2 + Apex Steam launch-option merge; Dota Repair removes Exo `exo.cfg`. Detect reads autoexec/exo.cfg when video primary is missing (CS2 never-launched path). Schema gates require real format tokens, not bare `[`/`=`.
- **AMD.** Snapshot is `amd-snapshot.json` (no collision with applyReport state); partial task disable fails Apply.
- **Spotify.** Repair aborts if client will not close; Detect verifies GPU routing live; success copy does not claim GPU when skipped.
- **NVIDIA.** No more "Apply installs Control Panel" lies; dead winget CPL installer removed. Display via NVAPI only. `advanced3dImageSettings` state follows real profile apply.
- **Discord.** Home AMOLED row requires Equicord theme enabled in settings, not just a theme file on disk.
- **Platform.** `ModuleTweakVersion` no longer double-keys internet. NVIDIA apply defaults to Max-FPS pack when `useGsync` is omitted (orb still asks). Script sources ASCII-clean for `Test-Repository`.

## 4.5.3

**Nuclear audit P0s: real NVIDIA game profiles, thrash-free Steam, honest System/Internet/Games.**

- **NVIDIA.** Native DRS now writes Base **and** per-application profiles from the combined NIP
  (GpuDrs multi-profile). Apply no longer stamps gameProfilesApplied from catalog prep alone.
  Detect runs `Exo.NvDisplay --drs-status` by default.
- **Steam.** SoftReclaim/SetProcessWorkingSetSize thrash removed from guard (native + PS) and
  banned by classifiers/smokes. State stamps no longer claim fullApply/cache clean without steps.
- **System.** Elevated pack fails closed if any op fails (no partial elev → all ok rewrite).
- **Internet.** Analyze DNS winner is passed into Apply. MapInternet requires MatchesPreset live.
- **Games.** Rocket League + Dota detect wired. Dota merges `+exec exo.cfg` into Steam launch options.
- **Discord.** Evidence/TWEAK-AUDIT match retired renderer-backgrounding; smoke fails if re-added.

## 4.5.2

**Living-machine voice. Harder NVIDIA. More strip on System/AMD.**

- **Orb.** Speaks as the PC — curious, hungry for frames, first-person. While optimizing it
  **does not** read kit steps ("Stripping NVI2…"); only UAC/reboot/close-app lines break
  through. Idle, scan, claim, and finish lines rewritten for that mind.
- **NVIDIA (kit 1.16.5 / profiles 1.8.0).** Much larger per-game DRS catalog (esports + popular
  hybrids), extra exe aliases, harder telemetry/task strip, startup-folder junk purge. Still
  never kills the display container service.
- **System.** Windows tips / soft-landing / silent Store installs / Start suggestions off.
- **AMD.** More updater/telemetry tasks and AUEP keys when present.
- **Stamps.** nvidia / system / steam / amd re-offer.

## 4.5.1

**Speed, privacy, AMOLED, declutter, gaming performance — full free local pass. No paid AI.**

- **Orb.** Sticky UAC / “close app” lines no longer get buried under personality. Mission-aligned greet. Games: display mode chips (leave / borderless / exclusive), Stop while tuning, host cancel token for Games apply/repair. Repair/open-install no longer default silently to Marvel Rivals. Scan progress speaks module names. Update ask shows release summary; failed updates show the real reason + Open logs.
- **Platform.** System / Spotify / AMD write `*-optimizer.json` applyReport after apply. Native elev failures rewrite `Ok=false` from step statuses. Dashboard module list matches verifyAll (9 modules).
- **System.** Game Bar widget + startup tip off; historical capture off. HAGS row notes reboot. USB unreadable row no longer mislabeled “(firmware)”.
- **Discord (1.3.87).** Shell `BACKGROUND_COLOR=#000000`, 2s kernel trim, Equicord auto-update off, max Declutter, extra privacy Chromium switches, pure-black QuickCSS. C# QoS detect requires NLA bypass. Home kernel check uses config+layout (not size-only fake VERIFIED).
- **Steam.** Library localconfig no longer soft-passes green on stock installs.
- **Internet.** MatchesPreset checks congestion + bindings + Ethernet metric + feature majority; incomplete live verify is not full success.
- **Games.** Marvel success text reports real display mode. Config schema gate for Fortnite/Valorant/COD/etc. before writing. Games repair cancel token.
- **Stamps.** `system` / `discord` / `steam` / `games` / `amd` tweak versions bumped.
- **Live verify tool.** Dropped folklore `Win32PrioritySeparation=38`; asserts Game Mode + Game Bar quiet.
- **Grok voice / API.** Not shipped — paid. Local voice lines only.

## 4.4.4

**AMD Radeon debloat, a screen that stays on, and an orb that stops reading out its own registry writes.**

- **AMD Radeon debloat.** New module: turns off Radeon auto-start, updater and crash-reporter
  scheduled tasks, and switches off analytics values that are actually on. Everything is
  disabled rather than deleted, snapshotted before it moves, and Repair puts it back. No
  driver install, no service disabling, no removal of the Radeon panel - see the commit for
  why each of those is a decision and not an omission.
- **AMD and Intel are detected properly now**, CPU and GPU, in one classifier that also
  retires the fourth copy of the discrete/integrated rules that had been disagreeing with
  itself.
- **Your screen no longer turns itself off.** The Exo power plan tuned the CPU, PCIe, USB and
  disks and then let Windows blank the display and sleep the machine on its own timer. Monitor
  off, sleep and hibernate are now all "never" on mains, inside Exo's plan only.
- **The hidden power settings are visible.** Windows ships most of them hidden, so you could
  not see or verify a single one in Power Options even though Exo was setting them.
- **The orb stops narrating registry writes.** It talks in its own voice while working.
  Anything you have to act on - administrator approval, a restart, "close Steam" - is still
  shown word for word, and the driver flow keeps its play-by-play.
- **The orb and the text share a centre line again**, and a long message no longer nudges it
  sideways.
- **Games stopped greening off a file Exo wrote to itself.** The tile read the preset as
  applied from Exo's own marker, so when Valorant or CS2 rewrote their config and wiped the
  tuning, the tile still said applied and the module could never be re-offered.
- **Brave Repair stopped claiming success while every machine-wide policy stayed put**, and
  now has the elevation path it never had.

## 4.4.3

**Modules that could never go green, two ways Exo destroyed your shortcuts, and a Stop button.**

- **Steam could never read "applied".** A registry read threw under StrictMode whenever a
  value was absent, a bare `catch {}` ate it, and the enforcement counter it was meant to
  increment stayed at zero. A clean, exit-zero Steam apply still reported "needs Apply".
- **"Library games high-perf GPU" was red on every single-GPU PC, permanently.** Apply
  deliberately *clears* the per-game GPU override when there is only one adapter — there is
  no second GPU to prefer — and detect demanded the stamp anyway. It now asks the same
  question Apply asks, and the row says what actually happened on your hardware.
- **Steam Repair rewrote Exo's own Start Menu shortcut to launch Steam.** The match for
  "Exo's launcher" was unanchored, so it also matched `...\Exo\app\Exo.exe`. Clicking Exo
  opened Steam, and nothing could undo it.
- **Discord PTB and Canary shortcuts were rewritten to launch stable Discord**, and their
  desktop shortcuts were deleted outright. Exo does not optimize those builds and now
  leaves them alone.
- **Discord Repair could leave the client unable to start.** Discord's installer state can
  end up in `InconsistentInstallerState`, where every launch dies instantly — so the client
  never installed its modules and Apply failed after six minutes telling you to "open
  Discord manually", which hit the same error. Repair now restores the pristine installer
  database from the installed package.
- **The NVIDIA apply died at the debloat stage** on an uninitialised variable, and
  **"Low-latency interrupts" was reported green without reading a single value** — the check
  threw on the first device and landed in the "can't see anything, skip" branch.
- **You can stop a running Apply or Repair.** The runner has always supported cancellation;
  nothing ever gave it a live token or gave you a button. Stopping is reported as stopping,
  not as a failure, and it kills the whole process tree rather than just the top process.
- **The conversation could clip its own buttons** with no scrollbar and no way to resize the
  window. The text now scrolls, the buttons stay reachable, and the window accounts for
  display scaling.
- Honesty fixes throughout: NVIDIA no longer records "driver tweaks verified" one line after
  warning they were not, Steam's native pass no longer claims a shader check it never ran,
  Spotify Repair no longer says it restored settings when there was no backup, the home
  dashboard no longer tags NVIDIA "VERIFIED" when the driver read-back found drift, and the
  two network steps that report "partial" are no longer parsed as nothing at all.
- The in-app changelog ships 8 releases instead of 277 (152 KB → 26 KB); older notes moved to
  `docs/CHANGELOG-ARCHIVE.md`.

## 4.4.2

**Your logs found six things. Every one of them was visible in the log text.**

- **The NVIDIA app kept coming back because Exo put it back.** It removed the app properly --
  eleven uninstalls, twelve folders, nine registry entries -- and then, three lines later,
  installed a Microsoft Store package. Every run. That step is gone. Exo applies display
  settings directly, so it never needed NVIDIA's own panel to be there.

- **The display step reported work it had not done.** A run whose own colour check came back
  `color=False` logged `SKIP: already matches panel policy`, then `SUCCESS`, then
  `Display prefs applied` -- three lines, and the last one was untrue. The optimizer only
  looked at the exit code, and a skip exits 0 exactly like a real apply does. It now
  distinguishes them and says "already matched policy - nothing needed changing".

  The skip *gate* itself is deliberately unchanged for now. It treats a set Full RGB registry
  value as sufficient even when NVAPI reports the live pipeline has not adopted it, and
  tightening that risks a permanent re-apply loop on any panel whose NVAPI read never agrees --
  three separate consumers read that flag and would have to move together. That needs testing
  against real monitors rather than a guess.

- **Brave opened twice, and showed a scary tab.** Three separate launches 200ms apart raced
  each other; now it's one window. The Proton Pass tab was pointed at the extension's internal
  address, which shows Brave's "blocked by administrator" page when the extension hasn't
  loaded yet -- an alarming message about an extension that installed perfectly. Gone.

- **Discord's voice priority was doing nothing on home networks.** The setting that makes
  Windows honour traffic marking was never written: the code crashed reading the old value one
  line before writing the new one, on any PC where it didn't already exist -- which is every PC
  that needed it. It said so every run, in a line that looked like a minor warning.

- **Internet still wasn't offered after its tuning changed.** That was my fix from 4.4.1 being
  too cautious: a PC with no record of what it last applied was treated as up to date. It now
  reads as "unknown, so ask" -- which costs one prompt per module, once.

**New: Exo can clean out NVIDIA driver leftovers.**

The job people use DDU for. First it looks -- driver packages Windows has stacked up over
years of updates, NVIDIA services left behind by drivers that are gone, a display device that
won't start. **If none of that is wrong, it says so and offers nothing**, because doing this to
a healthy PC is risk for no reason.

If it is worth doing, you see the exact list of what goes before you agree to anything. Then
it reboots into Safe Mode, removes it, and puts your boot back to normal.

The part that got the most care isn't the deleting -- that's recoverable, you just install a
driver. It's the Safe Mode flag: if the app died at the wrong moment, your PC would boot into
Safe Mode forever with no working Exo to fix it. So Exo clears that flag on every startup
before it does anything else, whatever happened last time, and tells you the exact command to
run if it ever can't.

**Then the whole thing was audited on a real Windows machine, and it did not compile.**

Everything above had been written where Exo cannot be built. Exo is a Windows-only WinUI app
and its XAML compiler does not run on Linux, so the commit tagged 4.4.2 had never once been
compiled by anything. It failed with ten errors. `dotnet format` was also failing -- and had
been failing on `main` since before this branch existed -- and `Discord.Smoke` was red on
`main` too. All three are green now.

**The app was working against itself, and here is exactly how.**

- **Every module's first successful Apply reported itself already out of date.** The check that
  reads a module's status ran *before* the record of what was just applied got written, so a
  module you had only this second finished was described as "Applied, but Exo's tuning changed
  since" -- and offered again.

- **Games could never be finished.** It returned early, before the line that records what was
  applied, so it was permanently "partial" and re-offered on every single launch. Saying yes
  could never clear it.

- **"Optimize Games" tuned a game you never named.** The orb asked one question about Games and
  sent no game with it, so the host filled in a fixed default -- Marvel Rivals -- and wrote to
  that, on the Optimized preset. It now asks which game and how hard to push it, the way Repair
  already asked. Three separate places had that same hard-coded default.

- **A failed apply was summed up as "good to go".** A module was marked as attempted before it
  ran, and the wrap-up reads attempted-and-partial as "as far as this machine will take it".

- **Steam and Your PC were overwriting each other.** Both wrote the same Windows scheduling
  value, to different numbers, and each checks for an exact match -- so applying either turned
  the other's tile red, and there was no order in which both could be green. Underneath it, the
  project held five contradictory positions on that one value across its own docs and gates.
  One owner now, at the value four of the five already agreed on.

- **49 QoS policies that never did anything and could never be removed.** Steam wrote one per
  installed game, but never the machine-wide switch that makes Windows honour them, so all of
  them were inert from the moment they were written. Nothing could remove them either -- the
  removal helper existed with no callers. Repair now sweeps them, and repairing Internet no
  longer silently kills Discord's and Steam's marking.

- **NVIDIA's tile described a module you were not running.** It reported displays and GPU power
  "left as-is by the Control-Panel-only policy" while the same state file recorded that Exo had
  set max refresh, Full RGB, colour override and the power ceiling -- and it showed no debloat
  row at all. Sixteen truthful rows now.

- **Your driver profiles were being rewritten with no backup.** The snapshot was behind a flag
  the shipped runner never passes, so on a fresh install nothing was saved -- and Reset then
  said "no driver profiles were changed", which was not true.

- **Profile Inspector ran on every single apply.** The native path only counted success if all
  61 settings verified, but 9 of them do not exist on a stock driver and never can. Those are
  now reported as unsupported instead of failed, so a healthy machine stays on the native path.

- **The orb looked hung, then gave up on work that was still running.** It threw away the
  host's progress narration for every module except the driver, never subscribed to the scan's
  progress at all, and abandoned any apply after three minutes while the host allows
  twenty-five -- cancelling nothing, and telling you it had broken while it was still going.

- **The React UI could not be built from a clean clone.** The exact commands the README gives
  fail on a missing type package. Nobody could have rebuilt the orb.

**Docs and CI.** The privacy page listed six network endpoints; the code contains twenty-six,
two of which download and run installers. `AGENTS.md` still specified a UI that no longer
exists. The tweak audit promised a pinned Profile Inspector version that a passing test
requires *not* to exist. Prereleases shipped installable builds with no smoke gates. And CI
could not catch the red Discord gate: the test asks for a different row depending on whether
Discord is installed, and the job running it never had Discord.

## 4.4.1

**The NVIDIA module was switched off in production. This turns it back on.**

Real logs from a real machine found things no test here could.

- **The whole NVIDIA apply was crashing.** It called a logging function that does not
  exist, and PowerShell only notices that when the line runs — so the apply died partway
  through, at exactly the step that writes the driver profile. That step had never once
  completed on anyone's PC. Fixed, and there is now a check that every function the
  scripts call is one that exists.

- **Exo was keeping the NVIDIA app on purpose.** A safety flag left switched on in the
  shipped build disabled NVIDIA App and GeForce Experience removal, bloat cleanup, the
  overlay, the GPU power ceiling and the display settings — and its app branch would
  *install* the Control Panel if it was missing. That is why an NVIDIA app appeared on a PC
  that never had one. All of that runs now. The two things that genuinely needed a guard
  kept one: driver installs stay with the new driver manager, and HD-audio removal stays
  opt-in because it can cost you sound over DisplayPort.

- **Driver stripping now removes anything it is not told to keep**, instead of removing
  only the seven things that were listed. The old way meant every new component NVIDIA
  ships was kept by default. DLSS, Vulkan, PhysX, HD-audio and the runtimes are protected
  by name *and* by pattern, so a rename between driver branches cannot quietly cost you
  DLSS. The full list of what was left out and what was kept now goes in the log, not just
  on screen.

- **"Applied" now means applied with the current tweaks.** If Exo changes what a module
  does, that module is offered again instead of sitting there looking finished on a
  configuration that has moved on. Internet is where this showed; every module had it.

- **Discord says which check wants a reapply** instead of just asking again.

- **Brave's new tab is dark instead of purple.** Turning the background image off did not
  give black — it gave Brave's default purple gradient, the brightest thing left on an
  otherwise black profile. Brave's own dark photography is back on; sponsored and branded
  images stay off.

- **The log now tells you what actually changed.** Every setting Exo writes records what
  the value was beforehand, so "already correct", "Exo changed it" and "Exo wrote it and it
  did not stick" stop looking identical. Each run also opens with the machine it ran on --
  Windows build, CPU, GPU and driver version, free disk, and which script kits were staged --
  so a pasted log answers the obvious questions without a round trip.

- **One log file instead of twenty.** Every apply used to leave two or three files behind.
  Now there is a single `exo-apply.log`, newest at the bottom, and the old scattered files
  are cleaned up on first run.

- **The dependency check no longer fails over a runtime Exo ships with itself.** It was
  trying to download WebView2, failing, and reporting a critical dependency missing —
  while the app was already running fine on its own bundled copy.

## 4.4.0

**Two new modules, and the AMOLED fix that was one line away the whole time.**

- **Brave is properly black now.** The darker-theme setting Exo already wrote is
  real, but Brave only honours it *when dark mode is on* — and Exo never turned
  dark mode on. So on any PC following a light Windows theme, the whole thing did
  nothing. Dark mode is now pinned on, the darker theme rides on top of it, and
  the browser chrome is switched to a neutral grey instead of a tinted one. That
  is as close to true black as Brave goes without a downloaded theme.

- **Four more Brave settings were being written into thin air.** Top sites, the
  search widget, sponsored images, and the Brave Talk widget. All four were spelled
  in ways Brave has never read — the Talk one especially, where the internal name
  says one thing and the actual setting says another. Checked every one against
  Brave's own source this time instead of trusting the names.

- **New: your PC itself.** Everything that was previously "use another tool for
  Windows". Hardware GPU scheduling, Game Mode, background game recording, the
  multimedia scheduler, NTFS timestamps, short filenames, and TRIM.

- **An Exo power plan, built for your actual CPU.** Rather than editing the plan
  you already use, Exo creates its own — named for what it found, like
  "Exo - Intel 8P16E" or "Exo - AMD 16C 2CCD" — and switches to it. Inside it:
  maximum processor state, boost mode, core parking, PCIe and USB power policy,
  drive spin-down.

  On **Intel 12th gen and newer** it also sets hybrid thread scheduling, so the
  short bursty threads a game's frame loop is made of stay on your performance
  cores instead of landing on efficiency cores. It works this out by reading the
  real core layout, not by reading your CPU's model number.

  On **multi-die AMD** (7950X, 9950X and friends) it tells you that games spilling
  across both dies lose frametime consistency — and that this is the chipset
  driver's job, not something a power plan can fix. It won't pretend otherwise.

  It starts from Ultimate Performance — Windows' own fastest plan, hidden on most
  installs — and falls back to High Performance then Balanced if your edition
  doesn't carry it. On top of that: energy preference pinned fully to performance,
  clocks jumping straight to maximum under load instead of stepping up, slow
  step-down so a gap between frames doesn't drop them, every core available, and
  latency-sensitive work running flat out.

  **Minimum processor state is the one value that depends on your machine.** On a
  desktop it's pinned to 100% — clocks never drop, so there's no ramp-up delay at
  all. On a laptop it's left low *even when plugged in*, because a thin chassis is
  power- and heat-limited and holding base clocks while idle burns the exact
  headroom boost needs when a game actually loads. Pinning it there is slower in
  practice, not faster.

  One thing it deliberately does **not** do: disable processor idle states. It's
  the most-recommended "max performance" tweak online and it backfires — stopping
  cores idling removes the thermal headroom modern boost relies on, so peak clocks
  come down. A performance setting that costs performance.

  Only settings your machine actually exposes get written. On laptops it only ever
  touches the plugged-in profile — battery is left exactly as it was.
  Repair switches you back to your old plan and deletes the Exo one.

- **Two NVIDIA settings were fighting each other — and one was fighting Exo.**
  Auditing the profile packs against each other and against the rest of the app
  turned up two real conflicts, both live in all ten packs:

  The **background frame cap was set to 30fps**. Exo's own Games module forces
  borderless windowed — which means alt-tabbing to Discord made your *game* a
  background application and dropped it to 30. Two Exo features working against
  each other on the same PC. The cap is now off.

  The **raw-latency profiles turned G-SYNC off globally but left it on per-game**,
  so a VRR monitor could quietly switch it back on for the exact titles the raw
  profile exists to keep it off for. Both variants are internally consistent now.

  The smoke test had been pinning the 30fps cap as *required*, so it was defending
  the bug rather than catching it. It now asserts the absence of a cap.

- **NVIDIA profiles no longer need Profile Inspector.** Exo now writes all ~80
  driver settings through NVIDIA's own API and reads every single one back, so it
  can tell you *which* ones didn't take. The old path shelled out to a downloaded
  third-party tool and trusted its exit code, which told you nothing about whether
  any individual setting landed.

  It also picks up a setting the old path was silently dropping: the Resizable BAR
  size limit is stored differently from the rest, and it had been skipped.

  Profile Inspector stays as a fallback for now — it works, and the new path hasn't
  yet run across every driver branch and card out there. It goes once it has.

  It also reads the two settings that usually matter most and that no software can
  change: whether your RAM is running at its rated speed, and whether Resizable BAR
  is on. Both live in your BIOS. Exo tells you what to change and where.

- **New: Spotify.** Very High audio quality, no home-page promo panel, no
  track-change popups mid-game, no starting with Windows, and it renders on the
  integrated GPU so the one running your game stays free. Spotify overwrites its
  own settings when it closes, so Exo closes it first — and tells you plainly
  instead of pretending it worked if it can't.

- **Your graphics card can now run at its real limit.** Exo raises the power and
  temperature ceilings to whatever your specific board says its maximum is — never
  a number we picked. Plenty of cards are locked to one value, and on those it says
  "already at the ceiling" rather than claiming a win. If another tuning app left
  your fan stuck at a fixed speed, it hands it back to the driver.

  Not doing: overclocking, undervolting, or custom fan curves. Those need to be
  stress-tested and rolled back automatically to be done honestly, and a bad fan
  curve can cook a card. Named in the docs rather than quietly skipped.

- **NVIDIA settings no longer go through Profile Inspector.** Exo writes them to
  the driver itself and then reads them back through a fresh connection to confirm
  they took -- checking through the same connection that wrote them only proves the
  cache is working. One external download gone from the apply path.

  Comparing the ten per-series profiles against each other turned up two settings
  that fought each other. One capped background frame rate at 30 while another part
  of Exo was enforcing borderless windowed, so the two disagreed about what your
  game should do when it lost focus. Both fixed.

- **New: Exo can handle your GPU driver.** It reads what NVIDIA actually lists for
  your exact card, tells you whether there's a newer one and why, then downloads it,
  strips out the parts you don't need -- the updater, telemetry, the browser
  integration -- and installs just the driver. This is the job people use
  NVCleanInstall for.

  Hotfix drivers are found too, but only offered when they fix something *your*
  card has. NVIDIA ships them as beta and takes them down when the next full driver
  lands, so trading a stable driver for one that fixes a problem you don't have is
  a bad deal, and Exo won't suggest it.

  Nothing happens without three separate yeses: one to look, one to download, one
  to install. Before the last one you're shown exactly which components are being
  left out, which are being kept and why, that your screen will blank several
  times, that a reboot follows, and that the install wipes the NVIDIA control panel
  so Exo's profile has to go back on afterwards.

  It needs 7-Zip to unpack the driver. If you don't have it, Exo offers to install
  the official one through winget -- asked separately, because that's real software
  going on your PC, and asked before the download rather than after it.

- **Everything above can be undone.** Every setting is recorded before it is
  changed, and Repair puts back exactly what was there. Where nothing was recorded,
  Repair says so and leaves the machine alone instead of guessing at a default you
  might not have wanted.

  The driver install is the exception, and it says so. A driver that fails to
  install isn't something Exo can put back -- recovery is Safe Mode and DDU. That
  is why it's the last step, why it's opt-in, and why you're looking at exactly
  what it will do before you take it.

**Under the hood: two checks for the bug that keeps coming back.**

- The recurring defect all month has been code that exists and nothing calls --
  written, tested, and unreachable, with every check passing because the checks
  test the code directly instead of asking whether anything reaches it. It happened
  again twice while building the driver feature.

  There are now two checks for it: one that every service is used by something
  other than its own tests, and one that everything the app's backend answers is
  actually asked for by the interface. The first deleted 126 lines of dead code
  whose only reference was a test checking the file existed. The second found the
  driver feature itself -- complete, and reachable from no button anywhere.

## 4.3.5

**Fixes for the things you and your friends hit in 4.3.4.**

- **Brave's bookmark bar and Proton Pass are actually fixed now.** 4.3.4 stopped
  Exo from *writing* the settings that caused those, but left behind the ones
  earlier versions had already written -- and a managed policy overrides your own
  setting forever. Re-optimizing Brave now clears them out. Your bookmarks bar
  comes back on the new tab page, and the "blocked by administrator" message goes
  away.
- **Removed a new-tab setting that never did anything.** Exo was writing a
  "make the background black" preference that Brave has never had, so it was
  ignored. That's why it showed as "None" instead of black. True black is a theme
  change, not a background setting -- it's coming with the AMOLED work rather than
  being faked here. The settings that *do* work (stats card, hidden widgets, no
  sponsored images) were verified against Brave's own source.
- **Removed the "or just tell me" box.** It couldn't be typed into. Undo is
  unaffected -- that still works from the wrap-up.

**Under the hood: the first tests that could have caught this week's bugs.**

- Three parts of the app were unreachable in 4.3.4 and every automated check
  passed anyway, because the checks all read source code rather than running it.
  The logic that decides whether a module needs optimizing is now tested directly
  with made-up PCs -- including the exact one that made the Games section
  invisible to everyone who had never used it.
- One of Brave's own checks turned out to be *requiring* the broken setting, so
  it would have blocked the fix. That check now tests the real thing.

## 4.3.4

**The NVIDIA display settings never applied. At all.**

- **Exo told people their PC was fine while their monitors weren't.** The code that
  sets refresh rate, colour range, bit depth and scaling was written, shipped, and
  connected to nothing -- Apply announced it was skipping displays 500 lines below
  the function that would have set them. Four more bugs stacked on top: refresh was
  never checked during a scan, bit depth could never move up from 8-bit even on a
  panel that supports 10-bit, the verdict threw away the display state it had just
  measured, and the checkbox for it was hardcoded to always show green. All fixed,
  both halves -- it detects the problem *and* fixes it now.
- **A second high-refresh monitor was being quietly dropped to 60 Hz** on every
  Apply, with no way to opt out. If you have two 144 or 165 Hz monitors, that was
  costing you the second one. Exo now leaves it alone unless you say otherwise.
- **Discord's voice priority did nothing on home networks.** The setting was written
  correctly and Windows silently ignored it, because a separate switch has to be on
  for it to work off a corporate domain. Voice packets went out unprioritised while
  the app reported success.
- **Two modules disagreed about which GPU your browser should use** -- and on a
  normal single-GPU desktop, one of them was writing a setting that does nothing.
  Repair also never undid it.
- **Steam was writing a fullscreen setting onto a window that can't go fullscreen.**
- **The orb erased its own answer.** "Your PC is good to go" was replaced by a random
  idle thought about 8 seconds after you read it. It now goes back to what it was
  telling you.

**New: it tells you what it can't fix.**

- The two biggest losses on most gaming PCs are RAM running below its rated speed
  (XMP/EXPO switched off) and Resizable BAR switched off. Both live in your BIOS, so
  no optimizer can touch them -- which is exactly why most tools never mention them.
  Exo now measures both and tells you the specific setting to change. If your RAM is
  slow, "your PC is good to go" says so, because that one thing outweighs everything
  else Exo does.

## 4.3.3

**Dota 2 support -- with the folklore left out on purpose.**

- Dota 2 joins the optimizer with real Source 2 settings: uncapped framerate,
  no throttling when the window loses focus, and the expensive cosmetic effects
  (ambient embers, animated portraits, fog, global shadows) turned off.
- **What it deliberately does NOT do:** the `-nod3d9ex` launch option that shows
  up in most Dota guides. That's a leftover from the old Source 1 engine and does
  nothing on Source 2 -- exactly the kind of tweak that looks like effort and
  changes nothing. There's now a test that fails the build if it ever gets added
  for any game.
- Settings go in Exo's own config file, so your personal autoexec is never
  touched and Repair removes ours cleanly.

## 4.3.2

**Confirmed and locked in: the network tuning covers Wi-Fi, not just Ethernet.**

- Verified end to end that both connection types get optimized. Shared settings
  (interrupt moderation, offloads, and stopping Windows powering the adapter
  down) apply to every network adapter. Wi-Fi additionally gets the levers that
  only exist on wireless -- radio power saving, MIMO power save, uAPSD, packet
  coalescing, band preference, roaming -- which are the biggest causes of Wi-Fi
  lag spikes. RSS core placement stays Ethernet-only because Wi-Fi adapters
  don't expose it.
- Added tests that fail the build if the tuning ever regresses to Ethernet-only,
  and confirmed Repair re-enables any adapter that was disabled -- so Wi-Fi can
  never be left off as a side effect.

## 4.3.1

**Rocket League support -- built so the settings actually survive.**

- Rocket League joins the optimizer: uncapped FPS (past the built-in ~250 cap),
  a frame of render latency removed, sharper texture filtering, and the visual
  noise (bloom, depth of field, lens flare, motion blur) turned off.
- The part that matters: **Rocket League rewrites its config file every single
  launch.** Any tool that just writes those settings has them wiped before you
  reach a match -- which is exactly the kind of tweak that looks applied and does
  nothing. Exo locks the file afterwards so the game keeps them, and Repair
  unlocks and restores it.


---

Older releases (4.2.9 and earlier) are in [docs/CHANGELOG-ARCHIVE.md](docs/CHANGELOG-ARCHIVE.md).



