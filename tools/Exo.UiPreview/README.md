# Exo UI Preview

Clickable **web mirror** of the Exo v2.5 WinUI shell for Linux/cloud agents.

This is **not** the shipping app. Apply/Repair/NVAPI stay in the Windows WinUI build.
Use this preview to validate navigation, home, modules, NVIDIA panel, and settings with real clicks (Playwright).

## Run

```bash
cd tools/Exo.UiPreview
npm install
npx playwright install chromium   # once
npm run dev                       # http://127.0.0.1:5173
```

## Automated click test

```bash
npm run preview:click
```

## Screenshots

```bash
npm run preview:shots
```

Writes PNGs under `/opt/cursor/artifacts/ui-preview/` (or pass an output path).

## Frame

Fixed **1180×760** AMOLED shell: left `NavRail`, brand-forward home, vertical feature rows, sticky action bar, settings gear flyout.
