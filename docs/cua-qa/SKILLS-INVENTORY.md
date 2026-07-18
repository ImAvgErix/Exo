# Skills inventory for Exo + Cua

## From [trycua/cua](https://github.com/trycua/cua) (installed)

| Skill | Path in this repo | Purpose |
|-------|-------------------|---------|
| **cua-driver** | `.agents/skills/cua-driver/` | Full Windows/macOS/Linux background GUI drive via `cua-driver` CLI/MCP |
| **cua-gui-automation** | `.agents/skills/cua-gui-automation/` | Higher-level GUI automation patterns from Cua |
| **exo-cua-qa** | `.agents/skills/exo-cua-qa/` | Exo-specific install-path module click QA loop |

Also installed on disk by Cua installer:

- `%USERPROFILE%\.cua-driver\skills\cua-driver\`
- Binary: `%LocalAppData%\Programs\Cua\cua-driver\bin\cua-driver.exe`

## Grok agent skill copy

Copied to `C:\Users\Erix\.grok\skills\` so Grok Build can load them:

- `cua-driver`
- `cua-gui-automation`
- `exo-cua-qa`

## Already in Exo (design / product)

Use together with Cua when fixing UI:

- `exo-ui-craft`, `better-ui`, `better-typography`, `better-colors`
- `frontend-design`, `emil-design-eng`, `apple-design`
- `verification-before-completion`, `systematic-debugging`
- `webapp-testing` (web only — **not** for WinUI; use Cua for Exo desktop)

## Control model (honest limits)

Cua Driver is **not** unrestricted OS admin remote control. It is:

- Background UIA + screenshots + click/type against app windows
- Default `delivery_mode: background` (no focus steal)
- Escalate to `foreground` only when driver returns `background_unavailable`

For Exo DoD we use it to:

1. Drive `%LocalAppData%\Exo\app\Exo.exe`
2. Click Discord / Steam / Internet / NVIDIA / Riot / Epic / Home
3. Save PNGs + UIA trees under `docs/cua-qa/`
4. Fail on honesty contradictions (status vs banner)

## Commands

```powershell
$env:PATH = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin;$env:PATH"
cua-driver doctor
cua-driver serve   # or autostart (registered on this PC)
pwsh -File tools/Cua.Qa/Invoke-ExoCuaQa.ps1
pwsh -File tools/Cua.Qa/Watch-ExoMain.ps1   # wait for ChatGPT / new origin/main
```

## After ChatGPT lands a new version

1. `git fetch origin` → confirm new `origin/main`
2. Rebase/reset work branch onto that tip
3. Clean MIR deploy to install path (never mixed binaries)
4. `Invoke-ExoCuaQa.ps1` full module pass
5. Fix only real failures; re-run Cua until green
