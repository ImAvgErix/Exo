# Security policy

## Supported versions

Only the **latest release** of Exo is supported with security and critical fixes.

| Version | Supported |
|---------|-----------|
| Latest GitHub release | Yes |
| Older tags | No |

## Reporting a vulnerability

Please **do not** open a public issue for security-sensitive reports.

1. Email or contact the maintainer via GitHub: [@ImAvgErix](https://github.com/ImAvgErix)
2. Or open a private security advisory on the [Exo repository](https://github.com/ImAvgErix/Exo/security) if available
3. Include: Exo version, OS build, steps to reproduce, and impact

You should receive an acknowledgment when possible. Coordinated disclosure is appreciated.

## Scope notes

Exo **intentionally** modifies application files, launchers, display/GPU settings, and Windows configuration when you run an optimizer. That is product behavior, not a vulnerability by itself.

Reports we prioritize:

- Remote code execution or unexpected network download of untrusted code
- Privilege escalation beyond the elevation you already approve
- Installer / update path integrity failures (wrong binary, no version check)
- Integrity failures in the public one-liner scripts (`Install-Exo.ps1`, `Repair-Discord.ps1`, `Repair-Internet.ps1`) or in verified vendor downloads (size/SHA-256 bypass)

Out of scope:

- “This tweak is too aggressive” product feedback (use Issues)
- Third-party Discord/Steam/NVIDIA tools behaving after Exo Apply
