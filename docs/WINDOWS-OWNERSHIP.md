# Windows tweak ownership

Also see **[PC-AWARE.md](PC-AWARE.md)** — community multi-PC: discover this machine live before every write.

## Strategy

**One writer per key-family. App optimizers keep app-scoped integration; Windows owns machine-wide gaming host policy; Internet stays network-only; Games owns display (borderless).**

| Kind | Examples | Owner |
|------|----------|--------|
| App-scoped | Discord toasts/autostart/tray/GPU/FSO for `Discord.exe`; Steam quiet shell + GPU for Steam CEF | That optimizer |
| App-scoped (game launchers) | High-perf GPU for Riot/Epic/Steam library **game + launcher exes** (no FSO-off) | Riot / Epic / Steam |
| Display mode | Borderless / windowed-fullscreen config tokens | **Games hub** |
| Machine-wide host gaming | Game Mode, HAGS, Game Bar quiet, Win32 priority, MMCSS (`SystemResponsiveness=10`, Games task), PowerThrottling, power plan, MPO | **Windows** |
| Network | TCP/IP, DNS/DoH, NIC advanced, Psched `NonBestEffortLimit`, DSCP for voice/game leaves | Internet / Discord / launchers |

## Rules

1. **One writer per key.** Discord owns Discord notification IDs; Steam owns Steam’s; Windows must not re-set them on Apply.
2. **Internet never writes Windows host gaming stack** (MMCSS / HAGS / Game Mode / Win32). Internet Repair must not undo them.
3. **Repair is scoped.** Discord Repair undoes Discord keys only; Windows Repair undoes Windows-owned keys only; Internet Repair is network-only.
4. **Games hub owns borderless.** Launchers do not stamp `DISABLEDXMAXIMIZEDWINDOWEDMODE` on game EXEs (cleared on re-Apply).
5. **Zero always-on background.** No yield/memory-guard Run companions; Steam-Exo.cmd does not spawn helpers.
6. **Riot/Epic/Steam own per-game GPU** (`GpuPreference=2`) for their discovered executables. Cooperative when values match.

## Module inventory (current)

- **Windows** — Game Mode, HAGS, Game Bar, Win32=38, MMCSS 10/10 + Games MMCSS, power plan, input, shell, Defender/WU policy, no Exo Run companions
- **Discord** — Run, StartupApproved, notifications, tray, `UserGpuPreferences` for Discord.exe, AppCompat FSO, voice QoS
- **Steam** — Run/StartupMode, notifications, tray, client FSO, library GPU (no game FSO), client/library DSCP, lean CEF launcher
- **Internet** — TCP/IP, DNS/DoH, metrics, NIC advanced, Psched NonBestEffortLimit only (no host gaming stack)
- **NVIDIA** — driver/DRS only; display mode left to Games
- **Riot / Epic** — Run quiet + shell quiet + high-perf GPU + DSCP; purge yield; no FSO-off on games
- **Brave** — managed policies (Neo Max-class debloat), profile prefs, vault purge, Proton Pass force-install, GPU/startup/task quiet; owns Brave only
- **Games** — per-title quality + always borderless configs
