# Contributing to Exo

Thanks for helping. Exo stays small and aggressive — contributions should match that bar.

## Ground rules

- **Deterministic optimizers** — no folklore keys, no invented registry paths
- **Honest status** — detectors and UI must agree; smoke tests cover peak logic
- **Repair where we break things** — Discord/Steam repair paths stay working
- **NVIDIA Reset is not rollback** — status clear only; driver recovery is manual

## Dev setup (Windows x64)

```powershell
git clone https://github.com/ImAvgErix/Exo.git
cd Exo
dotnet build Exo\Exo.csproj -c Release
```

Full installer package:

```powershell
.\Publish-Exo.ps1
# → release\Exo.exe
```

## Tests

```powershell
.\tools\Test-Repository.ps1
dotnet run --project tools\UiPeak.Smoke -c Release
dotnet run --project tools\NetworkPeak.Smoke -c Release
dotnet run --project tools\DiscordPeak.Smoke -c Release
dotnet run --project tools\SteamPeak.Smoke -c Release
dotnet run --project tools\NvidiaPeak.Smoke -c Release
```

UI changes must keep `tools/UiPeak.Smoke` green.

## Pull requests

1. One focused change per PR when possible
2. Update `CHANGELOG.md` under a new or existing unreleased section
3. Bump `VERSION` + `Exo/Exo.csproj` together when shipping a release
4. Do not commit `publish/`, `release/`, `bin/`, or `obj/`

## Code style

- WinUI 3 / .NET 8 unpackaged
- Prefer XAML Storyboards for shell motion — never Composition Opacity = 0
- Match existing Opti* styles in `Styles/ThemeResources.xaml`
- See [AGENTS.md](AGENTS.md) for agent/team workflow notes
