# Windows tweak ownership

## Strategy

**App optimizers keep app-scoped Windows integration. A future Windows module owns only machine-wide host policy and skips keys already owned by another module.**

| Kind | Examples | Owner |
|------|----------|--------|
| App-scoped | Discord toasts/autostart/tray/GPU/FSO for `Discord.exe`; Steam quiet shell + GPU for Steam CEF; Internet DNS/TCP/NIC | That optimizer |
| App-scoped (games launchers) | High-perf GPU / FSO for Riot/Epic **discovered game + launcher exes** | Riot / Epic |
| Machine-wide (future) | Global power/Game Mode/shell defaults/unaffiliated startup clutter | Windows module |

## Rules

1. **One writer per key.** Discord owns Discord notification IDs; Steam owns Steam’s; Windows must not re-set them on Apply.
2. **Detect before write.** Windows Apply skips rows already verified by Discord/Steam/Internet/NVIDIA ownership.
3. **Repair is scoped.** Discord Repair undoes Discord keys only; Windows Repair undoes Windows-owned keys only.
4. **Internet stays network.** Windows is not “Internet 2”.
5. **Do not strip Discord/Steam Windows tweaks** while waiting for a Windows module — they deliver value today.
6. **Riot/Epic own per-game GPU/FSO** for their discovered executables (snapshot + Repair).

## Module inventory (current)

- **Discord** — Run, StartupApproved, notifications, tray, `UserGpuPreferences` for Discord.exe, AppCompat FSO, voice QoS
- **Steam** — Run/StartupMode, notifications, tray, tasks, GPU prefs for steam/CEF
- **Internet** — TCP/IP, DNS/DoH, metrics, NIC advanced (Ethernet + Wi-Fi paths); Experimental adds MMCSS/Psched host knobs
- **NVIDIA** — driver/DRS; Experimental force re-imports profile
- **Riot / Epic** — Run quiet + shell quiet + yield companion + high-perf GPU + FSO off for game exes
