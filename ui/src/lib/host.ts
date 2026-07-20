/** Typed bridge to the .NET WebView2 host. Falls back to mock data in browser dev. */

export type ModuleId = 'discord' | 'steam' | 'windows' | 'internet' | 'nvidia' | 'riot' | 'epic'

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
  }>
  next?: { id: ModuleId; label: string } | null
  appVersion?: string
}

export interface ModuleStatus {
  id: ModuleId
  isApplied: boolean
  statusText: string
  detail: string
  features: Array<{ title: string; detail: string; active: boolean }>
  /** Host-provided defaults for module options */
  options?: {
    experimental?: boolean
    useGsync?: boolean
    preferLowestLatency?: boolean
  }
}

type HostRequest = { id: string; method: string; params?: Record<string, unknown> }
type HostResponse = { id: string; ok: boolean; result?: unknown; error?: string }
type HostEvent = { event: string; data?: unknown }

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const eventHandlers = new Map<string, Set<(data: unknown) => void>>()

function isHost(): boolean {
  return typeof window !== 'undefined' && !!(window as unknown as { chrome?: { webview?: unknown } }).chrome?.webview
}

function post(msg: unknown) {
  const wv = (window as unknown as { chrome?: { webview?: { postMessage: (m: unknown) => void } } }).chrome?.webview
  if (!wv) return
  wv.postMessage(typeof msg === 'string' ? msg : JSON.stringify(msg))
}

export function initHostBridge() {
  if (!isHost()) return
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
      const set = eventHandlers.get(ev.event)
      if (set) for (const h of set) h(ev.data)
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

async function call<T>(
  method: string,
  params?: Record<string, unknown>,
  timeoutMs = 180_000,
): Promise<T> {
  if (!isHost()) return mockCall<T>(method, params)
  const id = crypto.randomUUID()
  const req: HostRequest = { id, method, params }
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: (v) => resolve(v as T), reject })
    post(req)
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id)
        reject(new Error(`host timeout: ${method}`))
      }
    }, timeoutMs)
  })
}

/** Per-module detect cache — reopening a card within TTL is instant. */
const DETECT_TTL_MS = 120_000
const detectCache = new Map<ModuleId, { at: number; status: ModuleStatus }>()

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
      if (hit && Date.now() - hit.at < DETECT_TTL_MS) return hit.status
    }
    const status = await call<ModuleStatus>('module.detect', { module })
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  apply: async (module: ModuleId, options?: Record<string, unknown>) => {
    const status = await call<ModuleStatus>('module.apply', {
      module,
      ...(options || {}),
    })
    // Cross-module side effects — wipe entire client cache
    detectCache.clear()
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  repair: async (module: ModuleId) => {
    const status = await call<ModuleStatus>('module.repair', { module })
    detectCache.clear()
    detectCache.set(module, { at: Date.now(), status })
    return status
  },
  getSettings: () =>
    call<{
      appVersion: string
      checkForUpdatesOnLaunch?: boolean
      experimentalDefaults: Record<string, boolean>
    }>('settings.get'),
  setSettings: (patch: { checkForUpdatesOnLaunch?: boolean }) =>
    call<{
      appVersion: string
      checkForUpdatesOnLaunch?: boolean
      experimentalDefaults: Record<string, boolean>
    }>('settings.set', patch),
  /** Check + download/install when available. Long timeout for multi-minute SFX download. */
  checkUpdates: () =>
    call<{
      message: string
      updateAvailable: boolean
      alreadyLatest?: boolean
      installed?: boolean
      shouldExit?: boolean
      appVersion?: string
      localVersion?: string
      remoteVersion?: string
    }>('settings.checkUpdates', undefined, 30 * 60_000),
  openLogs: () => call<{ ok: boolean; path?: string; message?: string }>('shell.openLogs'),
  openIssues: () => call<{ ok: boolean; message?: string }>('shell.openIssues'),
  openNvidiaControlPanel: () =>
    call<{ ok: boolean; message?: string }>('shell.openNvidiaControlPanel'),
  minimize: () => call<{ ok: boolean }>('shell.minimize'),
  close: () => call<{ ok: boolean }>('shell.close'),
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
      overview: '3 / 6 verified',
      heroSummary: 'Mock host',
      specs: { cpu: 'Ryzen 7', gpu: 'RTX 4070', ram: '32 GB', os: 'Windows 11 25H2' },
      live: mockLive(),
      modules: [
        { id: 'discord', title: 'Discord', applied: true },
        { id: 'steam', title: 'Steam', applied: true },
        { id: 'windows', title: 'Windows', applied: false },
        { id: 'internet', title: 'Internet', applied: false },
        { id: 'nvidia', title: 'NVIDIA', applied: true },
        { id: 'riot', title: 'Riot', applied: false },
        { id: 'epic', title: 'Epic', applied: false },
      ],
      next: { id: 'windows', label: 'Windows' },
      appVersion: '3.7.2',
    } as T)
  }
  if (method === 'dashboard.live') return Promise.resolve(mockLive() as T)
  if (method === 'settings.get' || method === 'settings.set') {
    return Promise.resolve({
      appVersion: '3.7.2-dev',
      checkForUpdatesOnLaunch: true,
      experimentalDefaults: {},
    } as T)
  }
  if (method === 'settings.checkUpdates') {
    return Promise.resolve({
      message: 'You are on the latest build (mock).',
      updateAvailable: false,
      alreadyLatest: true,
      installed: false,
      shouldExit: false,
      appVersion: '3.7.2-dev',
    } as T)
  }
  if (method === 'shell.openLogs') {
    return Promise.resolve({ ok: true, path: 'mock-logs' } as T)
  }
  if (method === 'shell.openIssues') {
    return Promise.resolve({ ok: true } as T)
  }
  if (method === 'shell.minimize' || method === 'shell.close') {
    return Promise.resolve({ ok: true } as T)
  }
  if (method.startsWith('module.')) {
    const id = (params?.module as ModuleId) || 'discord'
    return Promise.resolve({
      id,
      isApplied: method === 'module.apply',
      statusText: method === 'module.apply' ? 'Applied' : 'Ready',
      detail: 'Browser mock — run Exo.exe for real optimizers.',
      features: [
        { title: 'Install', detail: 'Present', active: true },
        { title: 'Policy', detail: method === 'module.apply' ? 'Verified' : 'Not applied', active: method === 'module.apply' },
      ],
      options: {
        experimental: false,
        useGsync: id === 'nvidia',
        preferLowestLatency: id === 'internet',
      },
    } as T)
  }
  return Promise.resolve(undefined as T)
}
