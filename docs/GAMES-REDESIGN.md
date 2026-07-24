# Games optimizer — redesign spec (per-game real tweaks)

## Why the current module is wrong

The current Games module inherited the **Marvel Rivals pak-era mindset**: one generic
"Potato / Optimized" preset stamped onto every title. That's backwards.

- **"Potato mode" only ever made sense for the Rivals paks** (extreme asset downscaling).
  Bolting a fake potato preset onto CS2 or Valorant does nothing meaningful — those games
  don't have that lever. It's a box being checked, not a real tweak.
- **Some of what it writes doesn't survive.** For UE5 games it writes an `Engine.ini`
  `[SystemSettings]` block — but hand-edited UE5 `Engine.ini` entries **fail silently when a
  patch overwrites them**, so you're left with "phantom" tweaks that look applied but do
  nothing. The durable levers are the ones the game itself persists.
- It treats 10 games identically instead of asking, per game, *"what actually moves FPS or
  latency here, and is it safe?"*

## The new principle

For each game, apply **only** the levers that are (1) genuinely effective, (2) **durable**
(the game honors and keeps them across patches), and (3) **safe** (see anti-cheat boundary).
If a game has no real safe lever beyond what its own menu already does, **we do nothing and
say so.** No universal preset. No folklore. No box-checking.

Think of it like the NVIDIA app's per-game "Optimize" — but game-specific and deeper, using
each title's real config surface instead of a generic slider.

## The anti-cheat boundary (non-negotiable)

The most popular games run **kernel anti-cheat** (Valorant→Vanguard, Fortnite→EAC,
CS2→VAC, Apex→EAC, COD→Ricochet, Marvel Rivals→proprietary, R6→BattlEye). For those:

- **No paks. No injected DLLs. No binary/asar mutation. No process injection.** These get you
  **banned** and **break on every patch** — that's precisely why the Rivals pak system was
  removed. This is not negotiable and it matches Exo's own contract.
- "Real tweaks" for these games = **config levers the game reads**: launch options, autoexec /
  cvars, `GameUserSettings.ini` scalability, `Input.ini`, video configs.
- Paks/DLLs are only ever on the table for games with **no kernel AC** (mostly single-player) —
  none of the competitive titles below.

## Per-game plan (start: 8 of the most popular)

Legend: **DO** = real durable lever we apply · **DROP** = remove (fragile/folklore/no-op) ·
**OUT** = unsafe, never.

### 1. Counter-Strike 2 (Source 2, VAC)
- **DO** launch options: `-high -novid -nojoy +fps_max 0` and `-freq <hz>` matched to the
  monitor; `+exec exo.cfg`.
- **DO** an `exo.cfg` in `game/csgo/cfg`: `fps_max 0`, `mat_queue_mode 2` (multi-thread render),
  `cl_forcepreload 1`, `engine_no_focus_sleep 0`. Reflex is a driver/NVIDIA setting, not here.
- **DROP** CS:GO-era folklore that hurts Source 2: `-nod3d9ex`, `-softparticlesdefaultoff`,
  `-processheap`.
- Launch options live in Steam's `localconfig.vdf` — written natively, Steam closed.

### 2. Apex Legends (Source, EAC)
- **DO** launch options: `+fps_max 0 -novid -preload +exec autoexec.cfg`.
- **DO** `videoconfig.txt` cvars that are EAC-safe and real: `setting.cl_gib_allow 0`,
  `setting.cl_ragdoll_maxcount 0`, `setting.mat_depthfeather_enable 0`, `setting.ssao_enabled 0`,
  `setting.shadow_enable 0` (competitive), `setting.mat_forceaniso 1`.
- **OUT** anything that modifies gameplay mechanics (EAC blocks/bans it); `-dxlevel95` (DX9) is
  aggressive and only helps ancient GPUs — skip on modern cards.

### 3. Fortnite (UE5, EAC)
- **DO** `GameUserSettings.ini` scalability (`sg.*`) — the game persists these across patches.
- **DO** the handful of durable Fortnite keys it honors: `bUseVSync=False`,
  `bUseDynamicResolution=False`, `bDisableMouseAcceleration=True`, `FrameRateLimit=0`.
- **DROP** `Engine.ini` `[SystemSettings]` blasting (silently reverts on patch).

### 4. Valorant (UE, Vanguard — the strictest)
- **DO** only what Vanguard tolerates: `GameUserSettings.ini` display/quality mirror of the
  in-game menu, borderless. That's essentially it.
- **Be honest:** beyond mirroring menu settings there is almost nothing safe to add. If that's
  all, the brain should say "Valorant's locked down — I set what's safe, the rest is in its own
  menu," not pretend to a big optimization.

### 5. Marvel Rivals (UE5, anti-cheat)
- **DO** `GameUserSettings.ini` / `Scalability.ini` `sg.*` (durable, menu-backed).
- **DO** `Input.ini`: `bEnableMouseSmoothing=False`, `RawMouseInputEnabled=1` (real competitive
  win, survives patches).
- **DO** advise disabling the **High-Resolution Texture Pack DLC** (documented 1%-low stutter
  fix) — we can detect it and prompt.
- **DROP** the big `Engine.ini` `[SystemSettings]` block (the fragile phantom-tweak problem).

### 6. Call of Duty — Warzone / BO7 (Ricochet)
- **DO** the `s.*` / `g.*` players configs the game reads (VSync off, motion blur off, DOF off,
  film grain off, Reflex on) — already largely correct; keep the **durable** subset, verify each.
- **OUT** anything touching the game binary or Ricochet.

### 7. League of Legends (custom engine)
- **DO** `PersistedSettings.json` (yes, JSON) + `game.cfg`: cap FPS uncapped/high, disable frame
  cap, character-quality/effects low, shadows off, disable HUD anims. Real, safe, durable.

### 8. Overwatch 2 (custom engine)
- **DO** `Settings_v0.ini`: real render toggles (RenderScale 100, dynamic render scale off, local
  fog detail low, shadow detail low, reflections off, VSync off, limit-to-refresh options).

### Candidates for later / on request
Rainbow Six Siege (`GameSettings.ini`, well-documented), Rocket League (`TASystemSettings.ini`),
Deadlock (Source 2 autoexec), PUBG. Helldivers 2 / The Finals / Predecessor stay only if their
levers are real — otherwise dropped rather than faked.

## Model changes

- **Remove the universal Potato/Optimized preset.** Each game gets a single **Optimize** that
  applies *its* real tweaks (a game may internally still choose a couple of quality levels, but
  there's no fake global "potato").
- **Per-game capability honesty.** A game with no real lever shows "nothing safe to change here"
  instead of a fake success.
- **Keep** the good bones: pre-write config backup, one-click **Repair** (restore), the
  game-running guard, borderless-everywhere (a genuine durable lever), and **post-apply re-read
  verify** so we only claim what actually stuck.
- **Launch options** become a first-class lever (Steam `localconfig.vdf`, EA/Epic equivalents)
  for the Source/idTech games where they matter.

## Rollout

Implement per game, ship via the **prerelease channel** (`v4.x-games.N`), validate on real
installs (which levers actually hold after the game launches), then promote. Nothing here
touches anti-cheat, game binaries, saves, or logins — config files only.
