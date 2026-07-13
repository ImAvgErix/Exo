# OptiHub tweak audit (evidence-based)

Last pass: v1.9.25. Goal: keep only knobs with real OS/driver behavior; drop folklore.

## Internet

| Knob | Verdict | Why |
|------|---------|-----|
| `DisableTaskOffload=0` | **Keep** | `=1` is a real footgun; kills stack offloads |
| Autotune normal / experimental | **Keep** | Documented `netsh` / `Set-NetTCPSetting` |
| Heuristics disabled | **Keep** | Prevents Windows from restricting autotune |
| RSS on | **Keep** | Documented multi-queue receive |
| RSC on/off by preset | **Keep** | Real coalescing latency vs throughput tradeoff |
| LSO on/off by preset | **Keep** | Real driver offload tradeoff |
| CUBIC | **Keep** | Current Windows default path |
| Nagle keys (latency only) | **Keep** | Still applied per interface for TCP |
| SystemResponsiveness **10** | **Keep** | MS: &lt;10 or &gt;100 **clamp to 20**; 0 is wrong |
| NetworkThrottlingIndex **10** | **Keep** | OS default; `ffffffff` can raise DPC/audio issues |
| QoS NonBestEffortLimit 0 | **Keep** | Real GPO (old 20% reserve) |
| Games MMCSS Priority/GPU | **Keep** | Real when apps register with MMCSS |
| NIC EEE / selective suspend off | **Keep** | Real link power renegotiation source |
| PnPCapabilities 24 | **Keep** | Stops “turn off this device to save power” |
| Dynamic ports via netsh | **Keep** | Modern replacement for MaxUserPort |
| MaxUserPort / MaxFreeTcbs / TcpNumConnections | **Removed** | XP/server-era; ignored or irrelevant on modern desktop |
| TCP chimney / NetDMA / DCA registry | **Removed** | Removed from modern Windows |
| LargeSystemCache | **Removed** | Server-oriented; can hurt desktop |
| Static TcpWindowSize / GlobalMaxTcpWindowSize | **Removed** | Auto-tuning owns RWIN |
| AFD dynamic backlog / FastSend… | **Removed** | Server folklore |
| DNS cache TTL / ServiceProvider order | **Removed** | No proven gaming gain |
| WinINET MaxConnections | **Removed** | IE-era; modern browsers ignore |
| DefaultTTL / KeepAlive / SynAttackProtect | **Removed** | No meaningful client gaming effect |
| BBR2 force, timestamps, initialRto, fastopen | **Removed** | Mixed support / leave OS defaults |
| Force MTU 1500 | **Removed** | Can break PPPoE/VPN; default is fine |
| Scheduled tray tasks | **Removed** | Background noise |

## NVIDIA

| Area | Verdict |
|------|---------|
| Driver-only + OptiHub panel | **Keep** — App/CPL are not required for gaming |
| MSI High / telemetry / Ansel / HDCP off | **Keep** — real driver/service knobs |
| Profile Inspector ULL / max perf / pre-render 1 | **Keep** — real DRS settings |
| Tray: hide display IsPromoted=0; delete App ghosts | **Keep** — no logon task |
| Logon/persist scheduled tasks | **Removed** |

## Discord / Steam

| Area | Verdict |
|------|---------|
| Discord kernel (priority, trim, raw input) | **Keep** — in-process real behavior |
| Steam CEF lean launcher /HIGH + cache clean | **Keep** — real client overhead reduction |
| Windows quiet (toasts/tray/autostart) | **Keep** — real startup/notification reduction |
| Random “FPS registry packs” | **Not used** |

## How we decide

1. Prefer Microsoft docs / `netsh` / `Set-Net*` over blog lists  
2. Prefer driver advanced properties over dead Tcpip Parameters  
3. Prefer clamp-aware values (e.g. SystemResponsiveness 10)  
4. Prefer apply-time cleanup over background tasks  
