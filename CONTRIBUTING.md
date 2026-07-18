# Contributing to Exo

Keep changes narrow, reversible, and evidence-based. A new optimizer mutation
is complete only when it has capability detection, a pristine pre-state
snapshot, post-apply verification, exact Repair behavior, and a smoke test.

## Before opening a pull request

1. Create a branch and explain the user-visible behavior being changed.
2. Do not add folklore registry tweaks, anti-cheat changes, game-file edits,
   forced hardware assumptions, telemetry, startup agents, or background tasks.
3. Preserve existing user settings and unrelated dirty-worktree changes.
4. Run the repository checks and the relevant module smoke suite.
5. Include before/after evidence for UI, startup, package-size, or performance
   claims. Measurements are observations, not universal guarantees.

```powershell
dotnet build Exo.sln -c Release -p:Platform=x64
pwsh -File tools/Test-Repository.ps1
dotnet run --project tools/Contracts.Smoke/Contracts.Smoke.csproj -c Release
```

Security issues should be reported through a private GitHub security advisory,
not a public issue. See [SECURITY.md](SECURITY.md).
