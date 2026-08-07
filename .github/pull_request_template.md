## Summary

<!-- What and why -->

## Checklist

- [ ] `dotnet build Exo.sln -c Release -p:Platform=x64` succeeds
- [ ] `dotnet format Exo.sln --verify-no-changes` is clean (CI gates this)
- [ ] `tools/Test-Repository.ps1` passes
- [ ] **All seven smokes pass with `failed=0`** — Ui, Contracts, Network, Discord, Steam,
      Nvidia, Brave. A gate that was already red before your change still blocks: CI runs the
      whole set, and "it was broken when I got here" is how a red gate shipped through 4.4.1.
- [ ] Shipped `.ps1` edited → `tools/Generate-ScriptManifest.ps1` re-run (CI gates freshness)
- [ ] `ui/` edited → `npm run build` re-run so `Exo/wwwroot` matches the source
- [ ] New/changed tweaks ship with a detect row + repair path + smoke marker (no folklore keys)
- [ ] Changed *what a module applies* → `ModuleTweakVersion` bumped, or the change never
      reaches a machine that already reads "applied"
- [ ] `CHANGELOG.md` updated (if user-facing)
- [ ] `VERSION` + `Exo/Exo.csproj` bumped together (if release-bound)
- [ ] No `bin/` / `publish/` / secrets in the diff

## Test plan

<!--
How you verified. If the change touches an optimizer, say which machine you ran it on and what
the logs showed — Exo is Windows-only and WinUI does not build on Linux, so a change that has
only been reasoned about has not been tested.
-->
