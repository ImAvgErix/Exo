/** Typed bridge to the .NET WebView2 host. Falls back to mock data in browser dev. */

export type ModuleId =
  | 'discord'
  | 'brave'
  | 'steam'
  | 'internet'
  | 'nvidia'
  | 'system'
  | 'spotify'
  | 'amd'

/** What a module row reads as in the shell. */
export type ModuleState = 'applied' | 'ready' | 'blocked' | 'missing'

/** Shell text appearance. Persisted by the host and painted as CSS custom properties. */
export type TextColour = 'white' | 'grey'
export type TextSize = 'small' | 'normal' | 'large'

export interface ExoSettings {
  appVersion: string
  checkForUpdatesOnLaunch?: boolean
  welcomePromptSeen?: boolean
  buyMeACoffeeUrl?: string
  issuesUrl?: string
  textColour?: TextColour
  textSize?: TextSize
  experimentalDefaults: Record<string, boolean>
}

export interface LiveStats {
  memoryPercent: number
  memoryUsed: string
  memoryTotal: string
  memorySecondary: string
  cpuPercent: number
  /** False until the two-sample CPU counter is primed */
  hasCpu?: boolean
  gpuPercent: number
  /** False when GPU load could not be read */
  hasGpu?: boolean
  /** System drive (the volume Windows runs from), not every mounted disk */
  diskPercent: number
  /** False when the drive could not be read */
  hasDisk?: boolean
  diskUsed?: string
  diskTotal?: string
  /** e.g. 412 GB of 931 GB */
  diskSecondary?: string
  /** Full link string e.g. 2.5G Ethernet */
  netLink: string
  /** Speed only e.g. 2.5G */
  netLinkSpeed?: string
  /** Media only e.g. Ethernet */
  netLinkMedia?: string
  /** Idle latency label e.g. 11.5 ms */
  netIdleMs: string
  netIdleMsValue: number
  /** From last quality test when available */
  netDownMbps: number | null
  netUpMbps: number | null
  netLoadedDownMs: number | null
  netLoadedUpMs: number | null
  netLoss: string | null
  netLossPercent: number | null
  netDns: string | null
  netRating?: string
  netRatingDetail?: string
  /** Deprecated: was a fake health bar. Always 0 from host. */
  netMetricPercent: number
}

export interface DashboardSnapshot {
  overview: string
  heroSummary: string
  specs: { cpu: string; gpu: string; ram: string; os: string }
  live: LiveStats
  modules: Array<{
    id: ModuleId
    title: string
    applied: boolean
    /**
     * The shell's four words. Sent by the host so the tag vocabulary lives in one place;
     * `applied` is the older boolean and stays for callers that only need that much.
     */
    state?: ModuleState
  }>
  next?: { id: ModuleId; label: string } | null
  /** Firmware-level findings Exo can measure but not set (XMP/EXPO, Resizable BAR, VBS). */
  firmware?: FirmwareFinding[]
  appVersion?: string
}

export interface FirmwareFinding {
  id: string
  title: string
  /** true = good, false = a real miss worth telling the user about, null = could not read. */
  ok: boolean | null
  detail: string
  /** Exact thing to change and where. Empty when ok or unknown. */
  fixWhere: string
}

export interface ModuleStatus {
  id: ModuleId
  isApplied: boolean
  /** Shared vocabulary: ready | applied | partial | missing | … */
  statusKind?: string
  statusText: string
  detail: string
  /**
   * Why the module is in the state it is, when "what state" is not enough to phrase a
   * question. Currently only 'superseded' — applied, but with a tuning set Exo has since
   * changed — which needs a different sentence from a partial the machine actually undid.
   */
  statusReason?: string
  features: Array<{ title: string; detail: string; active: boolean }>
  /** Last-apply step lines: "step|ok", "step|fail:reason" */
  applyReport?: string[]
  /** Host-provided defaults for module options */
  options?: {
    experimental?: boolean
    useGsync?: boolean
    preferLowestLatency?: boolean
  }
}

/** Mirrors NvidiaDriverInstaller.Recommendation. */
export type DriverRecommendation =
  | 'UpToDate'
  | 'UpgradeWhql'
  | 'UpgradeHotfix'
  | 'NoLongerSupported'
  | 'Unknown'

export interface DriverCheck {
  ok: boolean
  kind: DriverRecommendation | string
  gpu?: string
  current?: string
  target?: string | null
  /** Hotfixes are beta by NVIDIA's own description — say so before anyone takes one. */
  beta?: boolean
  headline: string
  reasons: string[]
  /** Only an upgrade is actionable; up-to-date / unsupported / unknown have no button. */
  canPrepare?: boolean
  /** Whether an unpacker is already on the machine. Reported by check, before the download. */
  sevenZip?: boolean
  /** Whether winget is available to install one if it isn't. */
  canInstallSevenZip?: boolean
}

export interface DriverPrepared {
  ok: boolean
  message: string
  version?: string
  removed?: string[]
  kept?: string[]
  /** Components asked for but kept anyway, with the reason. */
  refused?: string[]
  command?: string
  /** Required by nvidia.driverInstall — a caller that only previewed cannot produce it. */
  token?: string
  plan?: string[]
}

export interface DriverInstalled {
  ok: boolean
  message: string
  /** A clean install resets the driver profile, so Exo's NVIDIA pack has to go back on. */
  reapplyNeeded?: boolean
}

export interface SweepCheck {
  ok: boolean
  /** True only when something here would survive a normal driver reinstall. */
  needsSweep: boolean
  headline: string
  findings: Array<{ id: string; title: string; detail: string; needsSweep: boolean }>
  packages: string[]
  folders: string[]
  token: string
}

export interface VerifyAllResult {
  results: ModuleStatus[]
  summary: string
  applied: number
  partial: number
  ready: number
  missing: number
  failed: number
}

export interface StartupEntry {
  name: string
  location: string
  command: string
  enabled: boolean
  source: string
}

export interface StartupListResponse {
  ok: boolean
  entries: StartupEntry[]
}

export interface ServiceEntry {
  name: string
  displayName: string
  state: string
  startMode: string
  account: string
}

export interface DiagnosticsSummary {
  ok: boolean
  specs: {
    cpuName: string
    logicalProcessors: number
    gpuName: string | null
    totalRamBytes: number
    ramLabel: string
    osName: string
  } | null
  disk: { totalBytes: number; usedBytes: number } | null
  memory: { totalBytes: number; availableBytes: number } | null
  firmware: unknown[]
}

export interface TempItem {
  path: string
  bytes: number
  kind: string
}

export interface StorageScanResponse {
  ok: boolean
  totalBytes: number
  items: TempItem[]
}

export interface ServicesListResponse {
  ok: boolean
  services: ServiceEntry[]
}

export interface UpdateCheck {
  updateAvailable: boolean
  message?: string
  alreadyLatest?: boolean
  localVersion?: string | null
  remoteVersion?: string | null
  releaseSummary?: string | null
}

export interface UpdateInstallResult {
  message: string
  updateAvailable: boolean
  alreadyLatest?: boolean
  installed?: boolean
  shouldExit?: boolean
  appVersion?: string
  localVersion?: string
  remoteVersion?: string
  releaseSummary?: string
}

type HostRequest = { id: string; method: string; params?: Record<string, unknown> }
type HostResponse = { id: string; ok: boolean; result?: unknown; error?: string }
type HostEvent = { event: string; data?: unknown }

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const eventHandlers = new Map<string, Set<(data: unknown) => void>>()

function emitHostEvent(event: string, data?: unknown) {
  const set = eventHandlers.get(event)
  if (set) for (const handler of set) handler(data)
}

function isHost(): boolean {
  return typeof window !== 'undefined' && !!(window as unknown as { chrome?: { webview?: unknown } }).chrome?.webview
}

function post(msg: unknown) {
  const wv = (window as unknown as { chrome?: { webview?: { postMessage: (m: unknown) => void } } }).chrome?.webview
  if (!wv) return
  wv.postMessage(typeof msg === 'string' ? msg : JSON.stringify(msg))
}

let hostBridgeReady = false

export function initHostBridge() {
  if (!isHost() || hostBridgeReady) return
  hostBridgeReady = true
  const wv = (window as unknown as {
    chrome: { webview: { addEventListener: (t: string, fn: (e: MessageEvent) => void) => void } }
  }).chrome.webview
  wv.addEventListener('message', (e: MessageEvent) => {
    let data: HostResponse | HostEvent | null = null
    try {
      data =
        typeof e.data === 'string'
          ? (JSON.parse(e.data) as HostResponse | HostEvent)
          : (e.data as HostResponse | HostEvent)
    } catch {
      return
    }
    if (data && typeof data === 'object' && 'event' in data && (data as HostEvent).event) {
      const ev = data as HostEvent
      emitHostEvent(ev.event, ev.data)
      return
    }
    const res = data as HostResponse
    if (!res?.id) return
    const p = pending.get(res.id)
    if (!p) return
    pending.delete(res.id)
    if (res.ok) p.resolve(res.result)
    else p.reject(new Error(res.error || 'host error'))
  })
}

export function onHostEvent(event: string, handler: (data: unknown) => void) {
  let set = eventHandlers.get(event)
  if (!set) {
    set = new Set()
    eventHandlers.set(event, set)
  }
  set.add(handler)
  return () => {
    set!.delete(handler)
  }
}

async function rawCall<T>(
  method: string,
  params?: Record<string, unknown>,
  timeoutMs = 180_000,
): Promise<T> {
  if (!isHost()) return mockCall<T>(method, params)
  const id = crypto.randomUUID()
  const req: HostRequest = { id, method, params }
  return new Promise<T>((resolve, reject) => {
    // The timeout must be cancelled once the host answers. Previously it stayed
    // armed for the full window (up to 10 min for verifyAll) even on success, so
    // with the 1.5s live-stats poll hundreds of dead timers piled up per session.
    let timer: number | undefined
    const done = () => {
      if (timer !== undefined) {
        clearTimeout(timer)
        timer = undefined
      }
      pending.delete(id)
    }
    pending.set(id, {
      resolve: (v) => { done(); resolve(v as T) },
      reject: (e) => { done(); reject(e) },
    })
    post(req)
    timer = window.setTimeout(() => {
      timer = undefined
      if (pending.has(id)) {
        pending.delete(id)
        reject(new Error(`host timeout: ${method}`))
      }
    }, timeoutMs)
  })
}

type UiOperationDescriptor = { label: string; module?: string }

function describeUiOperation(method: string, params?: Record<string, unknown>): UiOperationDescriptor | null {
  const module = typeof params?.module === 'string' ? params.module : undefined
  const moduleLabel: Record<string, string> = {
    amd: 'AMD Radeon',
    brave: 'Brave',
    chipset: 'Chipset',
    discord: 'Discord',
    internet: 'Internet',
    nvidia: 'NVIDIA',
    spotify: 'Spotify',
    steam: 'Steam',
    system: 'Windows',
  }
  if (method === 'module.apply') return { label: `${moduleLabel[module || ''] || 'Optimizer'} apply`, module }
  if (method === 'module.repair') return { label: `${moduleLabel[module || ''] || 'Optimizer'} repair`, module }
  if (method === 'module.verifyAll') return { label: 'Verify all' }
  if (method === 'nvidia.driverPrepare') return { label: 'NVIDIA prepare', module: 'nvidia' }
  if (method === 'nvidia.driverInstall') return { label: 'NVIDIA install', module: 'nvidia' }
  if (method === 'chipset.driverPrepare') return { label: 'Chipset prepare', module: 'chipset' }
  if (method === 'chipset.driverInstall') return { label: 'Chipset install', module: 'chipset' }
  if (method === 'nvidia.sweepArm') return { label: 'Driver cleanup', module: 'nvidia' }
  if (method === 'settings.checkUpdates') return { label: 'Exo update' }
  return null
}

async function call<T>(
  method: string,
  params?: Record<string, unknown>,
  timeoutMs = 180_000,
): Promise<T> {
  const operation = describeUiOperation(method, params)
  const operationId = operation ? crypto.randomUUID() : ''
  if (operation) {
    emitHostEvent('ui.operation', {
      id: operationId,
      phase: 'start',
      label: operation.label,
      module: operation.module,
      status: 'Starting…',
    })
  }
  try {
    const result = await rawCall<T>(method, params, timeoutMs)
    if (operation) {
      emitHostEvent('ui.operation', {
        id: operationId,
        phase: 'settled',
        label: operation.label,
        module: operation.module,
        status: method === 'module.verifyAll' ? 'Verified' : 'Finished',
      })
    }
    return result
  } catch (error) {
    if (operation) {
      emitHostEvent('ui.operation', {
        id: operationId,
        phase: 'error',
        label: operation.label,
        module: operation.module,
        status: error instanceof Error ? error.message : 'Could not finish',
      })
    }
    throw error
  }
}

/**
 * Apply and Repair have to outlast the host, not race it.
 *
 * The elevated runner allows a script 25 minutes (PowerShellRunnerService.cs, the
 * `TimeSpan.FromMinutes(25)` ceiling). The UI's 3-minute default meant a long but perfectly
 * healthy run — a first NVIDIA pass, a cold Discord install on a slow link — was declared
 * failed at the 3-minute mark while the host was still mid-flight and still writing to the
 * machine. Nothing was cancelled by that rejection: the elevated process carried on, and the
 * user was told their optimization had broken while it was in fact still running.
 *
 * Sitting just past the host's own ceiling means the host is always the one to time out, so
 * what surfaces is its real error instead of a bare "host timeout".
 */
const APPLY_TIMEOUT_MS = 26 * 60_000

/** Per-module detect cache — reopening a card within TTL is instant. */
const DETECT_TTL_MS = 120_000
const detectCache = new Map<ModuleId, { at: number; status: ModuleStatus }>()
const mockAppliedModules = new Set<ModuleId>()
let updatePeekInFlight: Promise<UpdateCheck> | null = null

export function invalidateDetectCache(module?: ModuleId) {
  if (module) detectCache.delete(module)
  else detectCache.clear()
}

export const host = {
  getDashboard: () => call<DashboardSnapshot>('dashboard.get'),
  getLive: () => call<LiveStats>('dashboard.live'),
  detect: async (module: ModuleId, opts?: { force?: boolean }) => {
    if (!opts?.force) {
      const hit = detectCache.get(module)
      // Short TTL when missing — user may have just installed Steam/GPU drivers
      const ttl =
        hit?.status.statusKind === 'missing' ||
        /not installed|no .*gpu|missing target/i.test(
          `${hit?.status.statusText || ''} ${hit?.status.detail || ''}`,
        )
          ? 20_000
          : DETECT_TTL_MS
      if (hit && Date.now() - hit.at < ttl) return hit.status
    }
    const status = await call<ModuleStatus>('module.detect', {
      module,
      ...(opts?.force ? { force: true } : {}),
    })
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  apply: async (module: ModuleId, options?: Record<string, unknown>) => {
    const status = await call<ModuleStatus>(
      'module.apply',
      { module, ...(options || {}) },
      APPLY_TIMEOUT_MS,
    )
    // Cross-module side effects — wipe entire client cache
    detectCache.clear()
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  repair: async (module: ModuleId) => {
    const status = await call<ModuleStatus>('module.repair', { module }, APPLY_TIMEOUT_MS)
    detectCache.clear()
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  /**
   * Stop an in-flight Apply or Repair.
   *
   * The host kills the elevated process tree. This does NOT resolve the pending
   * apply()/repair() promise — that one settles on its own, because the host answers the
   * original request with a fresh detect of whatever state the machine was left in. Keep
   * awaiting the original call; this is a request to stop, not a replacement result.
   * Short timeout: it is a signal, not work.
   */
  cancel: (module: ModuleId | 'chipset') =>
    call<{ ok: boolean; running?: boolean; message?: string }>(
      'module.cancel',
      { module },
      15_000,
    ),
  /**
   * NVIDIA driver, in three deliberate steps. Splitting them is the safety design, not
   * ceremony: check only reads, prepare downloads and strips but installs nothing, and
   * install refuses without the token prepare issued. No single call can install a driver.
   */
  driverCheck: () => call<DriverCheck>('nvidia.driverCheck', undefined, 90_000),
  /**
   * Multi-hundred-MB download plus a 7-Zip unpack — this one legitimately takes a while.
   * `installSevenZip` is the user's yes to installing the unpacker, and the host refuses to
   * install it without one.
   */
  driverPrepare: (installSevenZip = false) =>
    call<DriverPrepared>('nvidia.driverPrepare', { installSevenZip }, 45 * 60_000),
  driverInstall: (token: string) =>
    call<DriverInstalled>('nvidia.driverInstall', { token, confirm: true }, 45 * 60_000),

  /** AMD / Intel CPU chipset — same three-stage consent as NVIDIA. */
  chipsetCheck: () =>
    call<{
      ok: boolean
      kind: string
      vendor?: string
      title?: string
      current?: string | null
      target?: string | null
      headline: string
      reasons: string[]
      supportUrl?: string | null
      dropFolder?: string | null
      localPackage?: boolean
      canPrepare?: boolean
      canStrip?: boolean
      sevenZip?: boolean
      canInstallSevenZip?: boolean
    }>('chipset.driverCheck', undefined, 30_000),
  chipsetPrepare: (installSevenZip = false) =>
    call<{
      ok: boolean
      message: string
      version?: string
      vendor?: string
      removed?: string[]
      kept?: string[]
      token?: string
      plan?: string[]
      dropFolder?: string
      supportUrl?: string
    }>('chipset.driverPrepare', { installSevenZip }, 3 * 60_000),
  chipsetInstall: (token: string) =>
    call<{ ok: boolean; message: string; rebootRecommended?: boolean; rebootRequired?: boolean }>(
      'chipset.driverInstall',
      { token, confirm: true },
      45 * 60_000,
    ),
  chipsetOpenDropFolder: () =>
    call<{ ok: boolean; path?: string; message?: string }>('chipset.openDropFolder'),
  chipsetOpenSupport: () =>
    call<{ ok: boolean; url?: string; message?: string }>('chipset.openSupport'),
  /**
   * The driver sweep — Exo's DDU. Read-only check, then an arm that needs the token the
   * check produced. Arming only sets a one-shot Safe Mode boot; the removal happens on the
   * next start, in Safe Mode, where the driver files are not in use.
   */
  sweepCheck: () => call<SweepCheck>('nvidia.sweepCheck', undefined, 120_000),
  sweepArm: (token: string) =>
    call<{ ok: boolean; message: string; rebootRequired?: boolean }>(
      'nvidia.sweepArm', { token, confirm: true }, 120_000),
  getSettings: () => call<ExoSettings>('settings.get'),
  setSettings: (patch: {
    checkForUpdatesOnLaunch?: boolean
    welcomePromptSeen?: boolean
    textColour?: TextColour
    textSize?: TextSize
  }) => call<ExoSettings>('settings.set', patch),
  getChangelog: () =>
    call<{
      ok: boolean
      message?: string
      sections: Array<{ version: string; bullets: string[] }>
    }>('settings.getChangelog'),
  /** Check-only (never installs). Concurrent launch/manual checks share one request. */
  peekUpdate: () => {
    if (updatePeekInFlight) return updatePeekInFlight
    const request = call<UpdateCheck>('updates.peek', undefined, 60_000)
    updatePeekInFlight = request
    const clear = () => {
      if (updatePeekInFlight === request) updatePeekInFlight = null
    }
    void request.then(clear, clear)
    return request
  },
  /** Explicit download/install path. Never call this from a check action. */
  installUpdate: () =>
    call<UpdateInstallResult>('settings.checkUpdates', undefined, 30 * 60_000),
  listStartup: () => call<StartupListResponse>('startup.list'),
  setStartup: (name: string, location: string, enabled: boolean) =>
    call<{ ok: boolean; error?: string }>('startup.set', { name, location, enabled }),
  saveStartupSnapshot: () => call<{ ok: boolean }>('startup.snapshot'),
  listServices: () => call<ServicesListResponse>('services.list'),
  scanStorage: () => call<StorageScanResponse>('storage.scan'),
  cleanStorage: () => call<{ ok: boolean; freedBytes: number }>('storage.clean'),
  storageJournal: () => call<{ ok: boolean; items: TempItem[] }>('storage.journal'),
  diagnosticsSummary: () => call<DiagnosticsSummary>('diagnostics.summary'),


  openLogs: () => call<{ ok: boolean; path?: string; message?: string }>('shell.openLogs'),
  openIssues: () => call<{ ok: boolean; message?: string }>('shell.openIssues'),
  openUrl: (url?: string) =>
    call<{ ok: boolean; url?: string; message?: string }>(
      'shell.openUrl',
      url ? { url } : undefined,
    ),
  openNvidiaControlPanel: () =>
    call<{ ok: boolean; message?: string }>('shell.openNvidiaControlPanel'),
  minimize: () => call<{ ok: boolean }>('shell.minimize'),
  toggleMaximize: () => call<{ ok: boolean; maximized?: boolean }>('shell.toggleMaximize'),
  close: () => call<{ ok: boolean }>('shell.close'),
  /**
   * Settings → Verify: force live detect on every module (no Apply).
   * Long timeout — several modules probe PS / native stacks.
   */
  verifyAll: async () => {
    const r = await call<VerifyAllResult>('module.verifyAll', undefined, 10 * 60_000)
    detectCache.clear()
    for (const row of r.results ?? []) {
      if (row?.id) detectCache.set(row.id, { at: Date.now(), status: row })
    }
    return r
  },
}

const mockLive = (): LiveStats => ({
  memoryPercent: 42,
  memoryUsed: '6.2 GB',
  memoryTotal: '16.0 GB',
  memorySecondary: '6.2 / 16.0 GB',
  cpuPercent: 14,
  hasCpu: true,
  gpuPercent: 8,
  hasGpu: true,
  diskPercent: 44,
  hasDisk: true,
  diskUsed: '412 GB',
  diskTotal: '931 GB',
  diskSecondary: '412 GB of 931 GB',
  netLink: '2.5G Ethernet',
  netLinkSpeed: '2.5G',
  netLinkMedia: 'Ethernet',
  netIdleMs: '12.4 ms',
  netIdleMsValue: 12.4,
  netDownMbps: 940,
  netUpMbps: 42,
  netLoadedDownMs: 38,
  netLoadedUpMs: 55,
  netLoss: '0.0%',
  netLossPercent: 0,
  netDns: 'Cloudflare',
  netRating: 'Excellent',
  netRatingDetail: '',
  netMetricPercent: 0,
})

function mockCall<T>(method: string, params?: Record<string, unknown>): Promise<T> {
  if (method === 'dashboard.get') {
    return Promise.resolve({
      overview: '3 / 8 verified',
      heroSummary: 'Mock host',
      specs: { cpu: 'Ryzen 7', gpu: 'RTX 4070', ram: '32 GB', os: 'Windows 11 25H2' },
      live: mockLive(),
      modules: [
        { id: 'discord', title: 'Discord', applied: true, state: 'applied' },
        { id: 'brave', title: 'Brave', applied: false, state: 'missing' },
        { id: 'steam', title: 'Steam', applied: true, state: 'applied' },
        { id: 'internet', title: 'Internet', applied: true, state: 'applied' },
        { id: 'nvidia', title: 'NVIDIA', applied: true, state: 'applied' },
        { id: 'system', title: 'Your PC', applied: true, state: 'applied' },
        { id: 'spotify', title: 'Spotify', applied: false, state: 'ready' },
        { id: 'amd', title: 'AMD Radeon', applied: false, state: 'blocked' },
      ],
      next: { id: 'spotify', label: 'Spotify' },
      appVersion: '4.5.5-dev',
    } as T)
  }
  if (method === 'dashboard.live') return Promise.resolve(mockLive() as T)
  if (method === 'startup.list') {
    return Promise.resolve({
      ok: true,
      entries: [
        { name: 'Example App', source: 'HKCU Run', command: 'C:\\example.exe', enabled: true },
        { name: 'Game Launcher', source: 'HKLM Run', command: 'C:\\launcher.exe', enabled: false },
      ],
    } as T)
  }
  if (method === 'startup.set') return Promise.resolve({ ok: true } as T)
  if (method === 'startup.snapshot') return Promise.resolve({ ok: true } as T)
  if (method === 'services.list') {
    return Promise.resolve({
      ok: true,
      services: [
        { name: 'ExampleSvc', displayName: 'Example Service', state: 'Running', startMode: 'Auto', account: 'LocalSystem' },
        { name: 'OldSvc', displayName: 'Old Service', state: 'Stopped', startMode: 'Disabled', account: 'LocalService' },
      ],
    } as T)
  }
  if (method === 'storage.scan') {
    return Promise.resolve({ ok: true, totalBytes: 123456, items: [{ path: 'C:\\Users\\mock\\AppData\\Local\\Temp', bytes: 123456, kind: 'user temp' }] } as T)
  }
  if (method === 'storage.clean') return Promise.resolve({ ok: true, freedBytes: 123456 } as T)
  if (method === 'storage.journal') return Promise.resolve({ ok: true, items: [] } as T)
  if (method === 'diagnostics.summary') {
    return Promise.resolve({
      ok: true,
      specs: { cpuName: 'AMD Ryzen 7 mock CPU', gpuName: 'RTX 4070', ramLabel: '32 GB', osName: 'Windows 11 25H2' },
      disk: { totalBytes: 1e12, usedBytes: 4e11 },
      memory: { totalBytes: 32e9, availableBytes: 16e9 },
    } as T)
  }

  if (method === 'updates.peek') {
    // Browser dev: append ?update to the URL to exercise the brain's update ask.
    const wantsUpdate =
      typeof location !== 'undefined' && location.search.includes('update')
    return Promise.resolve({
      updateAvailable: wantsUpdate,
      alreadyLatest: !wantsUpdate,
      localVersion: '4.2.1',
      remoteVersion: wantsUpdate ? '9.9.9' : '4.2.1',
      releaseSummary: wantsUpdate ? 'Mock release notes' : null,
      message: 'mock',
    } as T)
  }
  if (method === 'settings.get' || method === 'settings.set') {
    return Promise.resolve({
      appVersion: '4.5.5-dev',
      checkForUpdatesOnLaunch: true,
      welcomePromptSeen: true,
      buyMeACoffeeUrl: 'https://www.buymeacoffee.com/UhhErix',
      issuesUrl: 'https://github.com/ImAvgErix/Exo/issues',
      textColour: (params?.textColour as TextColour) || 'white',
      textSize: (params?.textSize as TextSize) || 'normal',
      experimentalDefaults: {},
    } as T)
  }
  if (method === 'module.cancel') {
    return Promise.resolve({ ok: true, running: true, message: 'mock cancel' } as T)
  }
  if (method === 'settings.getChangelog') {
    return Promise.resolve({
      ok: true,
      sections: [
        {
          version: '3.16.6',
          bullets: [
            'PC-aware: discover this machine live before applying',
            'Task Scheduler inventory per PC',
          ],
        },
        {
          version: '3.16.3',
          bullets: ['Welcome tip jar', 'Buy me a coffee in Settings'],
        },
      ],
    } as T)
  }
  if (method === 'settings.checkUpdates') {
    const installsMockUpdate =
      typeof location !== 'undefined' && location.search.includes('update')
    return Promise.resolve({
      message: installsMockUpdate
        ? 'Mock update installed. The browser build does not restart.'
        : 'You are on the latest build (mock).',
      updateAvailable: installsMockUpdate,
      alreadyLatest: !installsMockUpdate,
      installed: installsMockUpdate,
      shouldExit: false,
      appVersion: '4.5.5-dev',
      remoteVersion: installsMockUpdate ? '9.9.9' : '4.5.5-dev',
    } as T)
  }
  if (method === 'shell.openLogs') {
    return Promise.resolve({ ok: true, path: 'mock-logs', folder: 'mock-logs' } as T)
  }
  if (method === 'shell.openIssues') {
    return Promise.resolve({ ok: true } as T)
  }
  if (method === 'shell.openUrl') {
    const u =
      (params?.url as string | undefined) || 'https://www.buymeacoffee.com/UhhErix'
    try {
      window.open(u, '_blank', 'noopener,noreferrer')
    } catch {
      /* ignore */
    }
    return Promise.resolve({ ok: true, url: u } as T)
  }
  if (method === 'shell.minimize' || method === 'shell.toggleMaximize' || method === 'shell.close') {
    return Promise.resolve({ ok: true } as T)
  }
  if (method === 'module.verifyAll') {
    return Promise.resolve({
      results: [],
      summary: '2 applied · 1 partial · 3 ready · 0 missing (mock)',
      applied: 2,
      partial: 1,
      ready: 3,
      missing: 0,
      failed: 0,
    } as T)
  }
  // Browser dev: append ?driver to the URL to walk the whole driver conversation.
  if (method === 'nvidia.driverCheck') {
    const offer =
      typeof location !== 'undefined' && location.search.includes('driver')
    return Promise.resolve(
      offer
        ? {
            ok: true,
            kind: 'UpgradeWhql',
            gpu: 'NVIDIA GeForce RTX 4070',
            current: '576.52',
            target: '581.29',
            beta: false,
            headline: 'A newer WHQL driver is available: 581.29 (you have 576.52).',
            reasons: ['581.29 is the newest driver listing NVIDIA GeForce RTX 4070.'],
            canPrepare: true,
            // ?driver&no7z exercises the missing-unpacker branch of the conversation.
            sevenZip: !location.search.includes('no7z'),
            canInstallSevenZip: true,
          }
        : {
            ok: true,
            kind: 'UpToDate',
            gpu: 'NVIDIA GeForce RTX 4070',
            current: '581.29',
            target: '581.29',
            beta: false,
            headline: 'You are on the newest driver for this card (581.29).',
            reasons: [],
            canPrepare: false,
          },
    ) as Promise<T>
  }
  if (method === 'chipset.driverCheck') {
    return Promise.resolve({
      ok: true,
      kind: 'UpToDate',
      vendor: 'AMD',
      title: 'AMD Chipset Software',
      current: '6.05.28.016',
      target: '6.05.28.016',
      headline: 'AMD chipset software is current on this PC.',
      reasons: [],
      supportUrl: 'https://www.amd.com/en/support',
      dropFolder: null,
      localPackage: false,
      canPrepare: false,
      canStrip: false,
      sevenZip: true,
      canInstallSevenZip: false,
    } as T)
  }
  if (method === 'nvidia.sweepCheck') {
    return Promise.resolve({
      ok: true,
      needsSweep: true,
      headline: '2 thing(s) here would survive a normal reinstall.',
      findings: [
        { id: 'stale-packages', title: '4 NVIDIA driver packages in the store', detail: 'oem10.inf 31.0.15.3623, oem22.inf 32.0.15.6094, oem31.inf 32.0.16.1074, oem40.inf 32.0.16.2001', needsSweep: true },
        { id: 'orphan-services', title: '1 NVIDIA service with nothing behind it', detail: 'nvlddmkm', needsSweep: true },
      ],
      packages: ['oem10.inf', 'oem22.inf', 'oem31.inf', 'oem40.inf'],
      folders: ['C:\\Program Files\\NVIDIA Corporation', 'C:\\ProgramData\\NVIDIA'],
      token: 'mock-sweep-token',
    } as T)
  }
  if (method === 'nvidia.sweepArm') {
    return Promise.resolve({ ok: false, message: 'Browser mock — run Exo.exe to sweep a driver.' } as T)
  }
  if (method === 'nvidia.driverPrepare') {
    return Promise.resolve({
      ok: true,
      message: 'Driver 581.29 is unpacked and stripped, ready to install.',
      version: '581.29',
      removed: ['Display.Update', 'Network.Service', 'Display.NView', 'NvContainerRecovery'],
      kept: ['Display.Driver', 'HDAudio.Driver', 'Display.PhysX', 'Display.Optimus'],
      refused: [],
      command: '-s -clean -noreboot -noeula',
      token: 'mock-token',
      plan: [
        'Install NVIDIA driver 581.29.',
        'Leaving out: Display.Update, Network.Service, Display.NView, NvContainerRecovery.',
        'Keeping: Display.Driver, HDAudio.Driver, Display.PhysX, Display.Optimus.',
        'Command: setup.exe -s -clean -noreboot -noeula',
        'The screen will go black several times while the driver loads.',
      ],
    } as T)
  }
  if (method === 'nvidia.driverInstall') {
    return Promise.resolve({
      ok: false,
      message: 'Browser mock — run Exo.exe to install a driver.',
      reapplyNeeded: false,
    } as T)
  }
  if (method.startsWith('module.')) {
    const id = (params?.module as ModuleId) || 'discord'
    if (method === 'module.apply') mockAppliedModules.add(id)
    if (method === 'module.repair') mockAppliedModules.delete(id)
    const appliedNow = mockAppliedModules.has(id)
    const mockStatus: ModuleStatus = {
      id,
      isApplied: appliedNow,
      statusKind: appliedNow ? 'applied' : 'ready',
      statusText: appliedNow ? 'Optimized applied' : 'Ready to optimize',
      detail: 'Browser mock — run Exo.exe for real optimizers.',
      features: [
        { title: 'Install', detail: 'Present', active: true },
        { title: 'Profile', detail: 'Optimized', active: true },
        { title: 'DLSS left alone', detail: 'OK', active: true },
        { title: 'One-click Repair ready', detail: 'OK', active: true },
      ],
      applyReport: appliedNow
        ? ['engine.ini|ok', 'scalability.ini|ok', 'borderless|ok']
        : [],
      options: {
        experimental: false,
        useGsync: id === 'nvidia',
        preferLowestLatency: id === 'internet',
      },
    }
    return Promise.resolve(mockStatus as T)
  }
  return Promise.resolve(undefined as T)
}
