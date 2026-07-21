# Clean-PC audit (Exo optimizers)

**Clean PC** = no prior Exo, empty `%LocalAppData%\Exo`, no optional tools. Target apps may be missing.

| Module | Works from zero? | Self-bootstraps | Hard prerequisites | Gaps |
|--------|------------------|-----------------|--------------------|------|
| **Discord** | Partial | Kits + optional Discord install (winget/CDN); Equicord download; PS7 bootstrap | **Must log into Discord once** before full Apply; elev; net for first install | Detect OK if missing. Repair works from zero (needs net). |
| **Steam** | Yes if Steam present | Native launcher/guard/startup; kits; PS7 for deep pack | **Steam must already be installed** (does not download Steam) | No userdata yet → soft reapply later. No Steam → hard fail Apply. |
| **Windows** | **Yes** | Pure native registry/policy/power | Admin (one elev for HKLM); Windows 10+ | No extra assets. Works with only OS. |
| **Internet** | **Yes** | Network scripts + native pins | Admin; a real NIC | No adapter / offline → probe partial; apply can still set stack policy. |
| **NVIDIA** | Yes if NVIDIA GPU + driver | Kit + NPI download; **PS7 auto-bootstrap on Apply** | NVIDIA GPU/driver; admin; net once for NPI | No GPU → hard fail. First offline NPI needs cache. |
| **Riot** | Yes if client found | Native GPU/FSO/yield; state files under `%LocalAppData%\Exo` | Riot Client (or games) | No install → hard fail. Launcher-only: yield OK when no game EXEs. DSCP needs elev. |
| **Epic** | Yes if launcher/manifests found | Same as Riot | Epic launcher | Same as Riot. |
| **Games (Rivals)** | Yes if Steam Rivals installed | **Configs always**; creates `~mods`; installs **bypass + packs from bundled seed** `Scripts/Games/MarvelRivals/` | Marvel Rivals on Steam; game **closed** for bypass write; admin if locks | Without Rivals: fail clear. Without seed in publish: configs only. |

## Games clean path (Apply)

1. Locate Rivals via Steam libraries  
2. Backup AppData configs (if any)  
3. Write Engine.ini / Scalability / GUS pins (Potato or Optimized)  
4. Create `…\Paks\~mods`  
5. Seed cache from **bundled** `Scripts\Games\MarvelRivals\`  
6. Install `dsound.dll` + ASI bypass  
7. Install Exo packs into `~mods`  
8. Verify profile marker in Engine.ini  

## Ship checklist

- Publish **self-contained** (`Publish-Exo.ps1`) so .NET is not required on the machine  
- Confirm `Scripts\Games\MarvelRivals\bypass` + `packs` exist under published app (~60 MB)  
- Confirm `Exo.runtimeconfig.json` has `includedFrameworks` + `coreclr.dll` beside `Exo.exe`  
