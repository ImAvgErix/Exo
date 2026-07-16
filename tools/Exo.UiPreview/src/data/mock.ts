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
  icon: string
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
  { id: 'internet', label: 'Net', logo: '/logos/internet.png' },
  { id: 'nvidia', label: 'GPU', logo: '/logos/nvidia.png' },
]

const discordFeatures: FeatureItem[] = [
  { id: 'equicord', title: 'Equicord', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'exohost', title: 'Exo Host', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'kernel', title: 'RAM / latency kernel', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'debloat', title: 'Client debloat', detail: 'Applied', applied: true, icon: '✓' },
  { id: 'amoled', title: 'AMOLED theme', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'quiet', title: 'Windows quiet', detail: 'Applied', applied: true, icon: '✓' },
]

const steamFeatures: FeatureItem[] = [
  { id: 'startup', title: 'Startup quiet', detail: 'Applied', applied: true, icon: '✓' },
  { id: 'cef', title: 'CEF launcher', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'cache', title: 'Cache / download', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'client', title: 'Client tweaks', detail: 'Applied', applied: true, icon: '✓' },
  { id: 'webhelper', title: 'WebHelper trim', detail: 'Not applied', applied: false, icon: '◈' },
]

const internetFeatures: FeatureItem[] = [
  { id: 'tcp', title: 'TCP stack', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'nagle', title: 'Nagle / delayed ACK', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'qos', title: 'QoS / priority', detail: 'Applied', applied: true, icon: '✓' },
  { id: 'dns', title: 'DNS cache', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'adapter', title: 'Adapter power', detail: 'Applied', applied: true, icon: '✓' },
]

const nvidiaFeatures: FeatureItem[] = [
  { id: 'msi', title: 'Driver / MSI', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'profiles', title: '3D profiles', detail: 'Not applied', applied: false, icon: '◈' },
  { id: 'debloat', title: 'Debloat', detail: 'Applied', applied: true, icon: '✓' },
  { id: 'display', title: 'Display prefs', detail: 'Not applied', applied: false, icon: '◈' },
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

export const APP_VERSION = '2.6.0'
