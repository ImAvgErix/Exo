# Internet/Network — evidence audit (keep / drop / verify)

The same bar as the Games redesign: every knob has to earn its place with a real,
measurable, durable effect. Network "gaming tweaks" are the single biggest folklore
minefield on Windows, so this is mostly about what we **refuse** to do.

## The honest ceiling (say this out loud in the UI)

**Most of your latency is physics and your ISP, not a registry key.** Propagation delay,
your route to the game server, ISP peering, and Wi-Fi vs Ethernet dwarf anything a TCP
setting can do. Exo *measures* the connection and tunes the handful of things that genuinely
matter; it does **not** promise lower ping, because for a healthy connection there's little
left to win. If there are spikes/loss, those are the real problem and no registry tweak fixes
them.

## The critical UDP fact that kills most "tweaks"

Competitive game netcode — CS2, Valorant, Apex, COD, Fortnite — is **UDP**. The famous
registry pins (`Nagle`/`TCPNoDelay`, `TcpAckFrequency`, `TcpDelAckTicks`) only affect **TCP**
sockets. They do **nothing** for UDP gameplay traffic. That's why Exo **removed** them — they
are near-placebo for the exact games people apply them for. (They have a small, real effect on
TCP-heavy apps, which is not what a gaming optimizer is for.) **Decision: stay dropped.**

## What Exo applies — held against the evidence

| Lever | Verdict | Why |
|---|---|---|
| **Receive auto-tuning = `normal`** | **KEEP** | `normal` is correct for modern high-BDP links. *Disabling* it (a top folklore "tweak") throttles throughput and is a myth. We keep the OS default and never disable it. |
| **Congestion provider = CUBIC** | **KEEP** | CUBIC is the Win10 1809+ default and the right choice. BBR2 exists on newer builds but isn't a stable, exposed client provider — don't chase it. |
| **Measured `Set-NetTCPSetting` template** (RTO, SYN retrans, NonSackRttResiliency, timestamps, ECN) | **KEEP, but VERIFY** | Applied via a supplemental template from a real quality benchmark, not blind. **Gap:** re-read after apply and confirm CUBIC/level actually took, instead of assuming. |
| **Delivery Optimization `DODownloadMode=0`** | **KEEP (underrated)** | Stops Windows Update peer-upload from stealing your upstream mid-match. A *real* bandwidth/latency win, snapshot-restored. This is the kind of lever that actually helps. |
| **NIC: interrupt moderation Off/Low, RSC, LSO, RSS→core** | **KEEP, measured** | Real but small latency-vs-CPU levers, applied per measurement on the gaming NIC only — not blasted. RSS steering to a non-zero core is legitimate on multi-core. |
| **DSCP 46 QoS on game/voice** | **KEEP + VERIFY** | Real prioritization **on your own network/router**. **Gap:** on non-domain networks DSCP marking needs `"Do not use NLA"=1` or the policy is silently ignored — verify + snapshot that. |
| **DNS: fastest measured resolver + DoH where Windows supports it** | **KEEP** | Picks the fastest healthy resolver from a measurement and requests its DoH template. Real (resolver response + privacy), reversible. |
| **Never touch** Wi-Fi disable, LLDP, NCSI probe; **no** DNS/MTU "packs"; snapshot-fail aborts | **KEEP** | Fail-closed safety. MTU/DNS packs are folklore; disabling NCSI breaks "internet access" detection. |

## Explicitly refused (folklore) — and why

- `TcpAckFrequency` / `TCPNoDelay` / `TcpDelAckTicks` — TCP-only, UDP games unaffected.
- **Disabling** receive auto-tuning — throttles modern links; cargo-cult.
- Static MTU 1472 "for gaming" — your path MTU is negotiated; a wrong static MTU causes
  fragmentation/black-holes.
- "Best DNS" hardcoded packs (8.8.8.8/1.1.1.1 blindly) — we *measure* instead; the fastest
  resolver is connection-specific.
- `NetworkThrottlingIndex`, `SystemResponsiveness` as "network" tweaks — those are MMCSS/audio
  scheduling, not network; only touched under the host-latency scope, honestly labelled.
- Registry "TCP optimizer" one-click packs — the whole category is why this doc exists.

## Fixes this audit produces

1. **Post-apply verification**: re-read `Get-NetTCPSetting` / DSCP policy after apply and report
   per-lever "verified" vs "wrote, didn't take" — same honesty upgrade the other modules get.
2. **DSCP on non-domain networks**: set + snapshot `Tcpip\QoS "Do not use NLA"=1` so marking
   isn't silently dropped.
3. **UI copy**: the brain should state the honest ceiling ("your line's already healthy — I
   tuned what matters, ping is mostly your route from here") instead of implying a big ping win.

Nothing here changes the safety model: measured, snapshotted, fully reversible via Repair.
