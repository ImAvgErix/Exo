# Contributing to Exo

Thanks for helping. Exo stays small and aggressive — contributions should match that bar.

## Ground rules

- **Deterministic optimizers** — no folklore keys, no invented registry paths; every tweak must be documented behavior
- **Honest status** — detectors and UI must agree; smoke tests cover detect logic
- **Every tweak ships complete** — a new tweak needs its detect row, a repair path, and a smoke-test marker in the same PR
- **Repair where we break things** — Internet restores its pre-apply snapshot; Discord/Steam repair paths stay working
- **NVIDIA Reset is not rollback** — status clear only; driver recovery is manual

## Dev setup (Windows x64)

```powershell
git clone https://github.com/ImAvgErix/Exo.git
cd Exo
dotnet build Exo.sln -c Release -p:Platform=x64
```

Full installer package:

```powershell
.\Publish-Exo.ps1
# → release\Exo.exe
```

## Tests

```powershell
.\tools\Test-Repository.ps1
dotnet run --project tools\Ui.Smoke -c Release
dotnet run --project tools\Network.Smoke -c Release
dotnet run --project tools\Discord.Smoke -c Release
dotnet run --project tools\Steam.Smoke -c Release
dotnet run --project tools\Nvidia.Smoke -c Release
```

UI changes must keep `tools/Ui.Smoke` green.

**Linux note:** the WinUI app build and `Ui.Smoke` are Windows-only (XAML compiler / `System.Drawing.Common`). On Linux, `Test-Repository.ps1` and the Network/Discord/Steam/NVIDIA smokes still run; the single Discord smoke failure `stable path under root` is a known Linux path-semantics limitation, not a regression.

## Pull requests

1. One focused change per PR when possible
2. All smokes and `Test-Repository.ps1` must pass — CI gates on them
3. Update `CHANGELOG.md` under a new or existing unreleased section
4. Bump `VERSION` + `Exo/Exo.csproj` together when shipping a release
5. Do not commit `publish/`, `release/`, `bin/`, or `obj/`

## Code style

- WinUI 3 unpackaged (.NET 10 LTS, Windows App SDK 2.2, C# 14)
- Prefer XAML Storyboards for shell motion — never Composition Opacity = 0
- Match existing Opti* styles in `Styles/ThemeResources.xaml`
- See [AGENTS.md](AGENTS.md) for agent/team workflow notes
