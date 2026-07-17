import { homeDashboardSeed } from '../data/mock'
import './HomePage.css'

function formatBytes(bytes: number): string {
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`
  return `${Math.round(bytes / (1 << 20))} MB`
}

export function HomePage() {
  const seed = homeDashboardSeed
  const usedPercent = Math.round((seed.memoryUsedBytes / seed.memoryTotalBytes) * 100)
  const stats = [
    { id: 'discord', label: 'Discord', value: '170 MB', meta: 'live process RAM · kernel on', tone: 'discord' },
    { id: 'steam', label: 'Steam', value: '836 MB', meta: 'lean library · game yield armed', tone: 'steam' },
    { id: 'connection', label: 'Connection', value: '49.5 ms', meta: '437 down · 291 up · zero loss', tone: 'internet' },
    { id: 'gpu', label: 'GPU profile', value: seed.nvidiaPath, meta: seed.nvidiaPathDetail, tone: 'nvidia' },
  ]

  return (
    <div className="home-page page-enter" data-testid="page-home">
      <div className="home-plate glass">
        <header className="home-head stagger-child">
          <span className="home-head__rail" aria-hidden="true" />
          <h1 className="home-head__brand" data-testid="hero-brand">EXO</h1>
          <p className="home-head__summary" data-testid="hero-tagline">All four modules reporting.</p>
          <span className="home-head__live"><i /> LIVE · 5 SEC</span>
        </header>

        <section className="memory-card stagger-child" data-testid="home-memory">
          <div>
            <p className="eyebrow">System RAM</p>
            <strong>{formatBytes(seed.memoryUsedBytes)}</strong>
            <p>{formatBytes(seed.memoryTotalBytes - seed.memoryUsedBytes)} free · {formatBytes(seed.memoryTotalBytes)} total</p>
          </div>
          <span className="memory-card__percent">{usedPercent}% in use</span>
          <span className="memory-card__track"><i style={{ width: `${usedPercent}%` }} /></span>
        </section>

        <section className="signal-grid stagger-child" data-testid="home-signals">
          {stats.map((stat) => (
            <article className={`signal-card signal-card--${stat.tone}`} key={stat.id} data-testid={`home-stat-${stat.id}`}>
              <span className="signal-card__rail" aria-hidden="true" />
              <div>
                <p className="eyebrow">{stat.label}</p>
                <strong>{stat.value}</strong>
                <p>{stat.meta}</p>
              </div>
            </article>
          ))}
        </section>
      </div>
    </div>
  )
}
