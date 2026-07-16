export type PageId =
  | 'home'
  | 'discord'
  | 'steam'
  | 'internet'
  | 'nvidia'
  | 'nvidia-panel'

export type ModuleId = 'discord' | 'steam' | 'internet' | 'nvidia'

export interface DirectoryCard {
  id: string
  title: string
  logo: string
  comingSoon: boolean
}

export interface FeatureItem {
  id: string
  title: string
  detail: string
  applied: boolean
}

export interface ModuleData {
  id: ModuleId
  section: string
  statusTitle: string
  features: FeatureItem[]
  applyLabel: string
  repairLabel: string
  variant: 'standard' | 'internet' | 'nvidia'
}

export interface FakeDisplay {
  id: string
  title: string
  resolution: string
  refresh: string
  depth: string
  colorRange: string
  scaling: string
  vibrance: number
}

export const directoryCards: DirectoryCard[] = [
  { id: 'discord', title: 'Discord', logo: '/logos/discord.png', comingSoon: false },
  { id: 'steam', title: 'Steam', logo: '/logos/steam.png', comingSoon: false },
  { id: 'internet', title: 'Internet', logo: '/logos/internet.png', comingSoon: false },
  { id: 'nvidia', title: 'NVIDIA', logo: '/logos/nvidia.png', comingSoon: false },
  { id: 'windows', title: 'Windows', logo: '/logos/windows.png', comingSoon: true },
  { id: 'amd', title: 'AMD', logo: '/logos/amd.png', comingSoon: true },
  { id: 'brave', title: 'Brave', logo: '/logos/brave.png', comingSoon: true },
  { id: 'riot', title: 'Riot', logo: '/logos/riot.png', comingSoon: true },
  { id: 'epic', title: 'Epic', logo: '/logos/epic.png', comingSoon: true },
]

export const navItems: { id: ModuleId; label: string; logo: string }[] = [
  { id: 'discord', label: 'Discord', logo: '/logos/discord.png' },
  { id: 'steam', label: 'Steam', logo: '/logos/steam.png' },
  { id: 'internet', label: 'Internet', logo: '/logos/internet.png' },
  { id: 'nvidia', label: 'NVIDIA', logo: '/logos/nvidia.png' },
]

/**
 * Preview-only seeded dashboard.
 * WinUI keeps FPS/frame-time empty until a real capture path ships;
 * it reads memory / trim / latency / NVIDIA pack from LocalAppData.
 */
export const homeDashboardSeed = {
  fpsGainPercent: 18,
  frameTimeMs: 6.2,
  frameTimeOnePercentMs: 9.1,
  frameTimeSeriesMs: [
    7.4, 6.8, 6.1, 5.9, 6.3, 6.0, 5.8, 6.5, 7.1, 6.4, 5.9, 6.2, 6.8, 7.2, 6.6,
    6.1, 5.9, 6.3, 6.7, 7.0, 6.4, 6.0, 5.8, 6.2,
  ],
  nvidiaPath: 'Max FPS pack',
  nvidiaPathDetail: '40 Series.nip',
  trimTotalBytes: 1_480 * (1 << 20),
  trimLast24hBytes: 420 * (1 << 20),
  memoryUsedBytes: 14.2 * (1 << 30),
  memoryTotalBytes: 32 * (1 << 30),
  latencyBeforeP50: 28.4,
  latencyAfterP50: 16.1,
}

/* Status = rail + Applied/Not applied. */
const discordFeatures: FeatureItem[] = [
  { id: 'equicord', title: 'Equicord', detail: 'Client strip + host', applied: false },
  { id: 'exohost', title: 'Exo Host', detail: 'Overlay host process', applied: false },
  { id: 'kernel', title: 'RAM / latency kernel', detail: 'Working set trim', applied: false },
  { id: 'debloat', title: 'Client debloat', detail: 'Telemetry + bloat', applied: true },
  { id: 'amoled', title: 'AMOLED theme', detail: 'Pure black client', applied: false },
  { id: 'quiet', title: 'Windows quiet', detail: 'Background hush', applied: true },
]

const steamFeatures: FeatureItem[] = [
  { id: 'startup', title: 'Startup quiet', detail: 'Boot services', applied: true },
  { id: 'cef', title: 'CEF launcher', detail: 'Chromium flags', applied: false },
  { id: 'cache', title: 'Cache / download', detail: 'Shader + depot', applied: false },
  { id: 'client', title: 'Client tweaks', detail: 'UI + overlay', applied: true },
  { id: 'webhelper', title: 'WebHelper trim', detail: 'Helper process', applied: false },
]

const internetFeatures: FeatureItem[] = [
  { id: 'tcp', title: 'TCP stack', detail: 'Window + autotune', applied: false },
  { id: 'nagle', title: 'Nagle / delayed ACK', detail: 'Latency stack', applied: false },
  { id: 'qos', title: 'QoS / priority', detail: 'DSCP + throttle', applied: true },
  { id: 'dns', title: 'DNS cache', detail: 'Resolver flush', applied: false },
  { id: 'adapter', title: 'Adapter power', detail: 'NIC power save', applied: true },
]

const nvidiaFeatures: FeatureItem[] = [
  { id: 'msi', title: 'Driver / MSI', detail: 'Interrupt mode', applied: false },
  { id: 'profiles', title: '3D profiles', detail: 'Global + per-game', applied: false },
  { id: 'debloat', title: 'Debloat', detail: 'Telemetry strip', applied: true },
  { id: 'display', title: 'Display prefs', detail: 'Color + scaling', applied: false },
]

export const modules: Record<ModuleId, ModuleData> = {
  discord: {
    id: 'discord',
    section: 'DISCORD',
    statusTitle: 'Ready to optimize',
    features: discordFeatures,
    applyLabel: 'Apply',
    repairLabel: 'Repair',
    variant: 'standard',
  },
  steam: {
    id: 'steam',
    section: 'STEAM',
    statusTitle: 'Partially applied',
    features: steamFeatures,
    applyLabel: 'Apply',
    repairLabel: 'Repair',
    variant: 'standard',
  },
  internet: {
    id: 'internet',
    section: 'INTERNET',
    statusTitle: 'Stack idle',
    features: internetFeatures,
    applyLabel: 'Apply',
    repairLabel: 'Repair',
    variant: 'internet',
  },
  nvidia: {
    id: 'nvidia',
    section: 'NVIDIA',
    statusTitle: 'GPU detected',
    features: nvidiaFeatures,
    applyLabel: 'Apply',
    repairLabel: 'Reset',
    variant: 'nvidia',
  },
}

export const fakeDisplays: FakeDisplay[] = [
  {
    id: 'display-1',
    title: 'Display 1 · Primary',
    resolution: '2560 × 1440',
    refresh: '165 Hz',
    depth: '8-bit',
    colorRange: 'Full',
    scaling: 'No scaling',
    vibrance: 50,
  },
  {
    id: 'display-2',
    title: 'Display 2 · Secondary',
    resolution: '1920 × 1080',
    refresh: '60 Hz',
    depth: '8-bit',
    colorRange: 'Limited',
    scaling: 'Aspect ratio',
    vibrance: 50,
  },
]

export const APP_VERSION = '2.6.1'
