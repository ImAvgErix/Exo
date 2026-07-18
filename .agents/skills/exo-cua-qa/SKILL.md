---
name: exo-cua-qa
description: Drive the live Exo WinUI app with Cua Driver (trycua/cua) for background UIA snapshots, screenshots, and module click QA. Use when verifying Exo UI after changes, investigating "UI feels broken", or before claiming install-path gates green.
---

# Exo + Cua Driver GUI QA

## What this is

[Cua Driver](https://github.com/trycua/cua) (`cua-driver` on Windows) is a **background** computer-use driver. It walks UIA trees, clicks elements by index, and captures screenshots **without stealing focus**.

It is installed on this machine:

- Binary: `%LocalAppData%\Programs\Cua\cua-driver\bin\cua-driver.exe`
- Home: `%USERPROFILE%\.cua-driver`
- Skill pack: `%USERPROFILE%\.cua-driver\skills\cua-driver` (see `WINDOWS.md` there)

## When to use

- After **any** Exo UI change (shell, plate, module page)
- Before claiming "app works on PC" / install-path green
- When the user says the UI is broken, empty, contradictory, or "feels wrong"
- Prefer this over guessing from XAML alone — **ground on live UIA + PNG**

## Hard rules (Exo DoD)

1. Baseline is **GitHub `origin/main`** (product), not a thinner local fork.
2. Drive the **install-path** binary: `%LocalAppData%\Exo\app\Exo.exe` (Start Menu target).
3. After UI work: open **Discord, Steam, Internet, NVIDIA** (and Riot/Epic if present). Crash → stop and fix.
4. Never claim done without: install binary + module navigation evidence + smokes green.

## Quick path (script)

From repo root:

```powershell
# Ensure PATH
$env:PATH = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin;$env:PATH"
cua-driver doctor
# Starts serve if needed, launches Exo if needed, clicks each module, writes docs/cua-qa/*
pwsh -File tools/Cua.Qa/Invoke-ExoCuaQa.ps1
```

Artifacts:

| Path | Purpose |
|------|---------|
| `docs/cua-qa/REPORT.md` | Labels + honesty warnings |
| `docs/cua-qa/<module>.png` | Window screenshots |
| `docs/cua-qa/*-state.json` | Full UIA snapshots |

## Manual CLI loop (PowerShell 5.1)

**Pipe JSON via stdin** — PS 5.1 strips quotes in multi-field `--args`:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin;$env:PATH"
cua-driver serve   # or Start-Process cua-driver.exe -ArgumentList serve -WindowStyle Hidden

# Find Exo
'{}' | cua-driver call list_windows

# Snapshot (replace pid / window_id)
$pid=2808; $hwnd=15010066
@{ pid=$pid; window_id=$hwnd; screenshot_out_file="$PWD\docs\cua-qa\shot.png"; include_screenshot=$true; max_elements=100 } |
  ConvertTo-Json -Compress | cua-driver call get_window_state

# Click by element_index from last snapshot (Discord often index 4)
@{ pid=$pid; window_id=$hwnd; element_index=4; delivery_mode='background' } |
  ConvertTo-Json -Compress | cua-driver call click
```

Default `delivery_mode` is **`background`**. Only escalate to `foreground` after a structured `background_unavailable` error.

## What to look for (Exo-specific)

| Module | Must see |
|--------|----------|
| Shell | Discord, Steam, Internet, NVIDIA (+ Riot, Epic on 3.6+) + Home |
| Discord | Status and guidance **agree**; never "Already optimized" + "not installed" |
| NVIDIA | **Use G-SYNC / VRR** toggle (not auto-only) |
| Internet | Analyze & Apply + Repair; no folklore DNS claims |
| Steam | Honest install / open-once states |
| Riot / Epic | Housekeeping only; never anti-cheat / Vanguard / EOS host kills |

## MCP (optional for other agents)

```text
cua-driver mcp
```

Register with Claude Code / Cursor / Codex per [Cua docs](https://cua.ai/docs/how-to-guides/driver/connect-your-agent).

## Full Windows skill

For complete tool schema and delivery-mode contract:

`%USERPROFILE%\.cua-driver\skills\cua-driver\WINDOWS.md`
