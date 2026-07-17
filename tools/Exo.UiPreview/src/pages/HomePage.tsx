import { homeDashboardSeed } from '../data/mock'
import './HomePage.css'

function formatBytes(bytes: number): string {
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`
  return `${Math.round(bytes / (1 << 20))} MB`
}

export function HomePage() {
  const seed = homeDashboardSeed
  const usedPercent = Math.round((seed.memoryUsedBytes / seed.memoryTotalBytes) * 100)
  const outcomes = [
    { id: 'discord', label: 'Discord', tag: 'Verified', title: 'Lean client active', change: 'Privacy patch · voice QoS · idle memory guard', live: '170 MB live · 412 MB reclaimed this session', tone: 'discord' },
    { id: 'steam', label: 'Steam', tag: 'Verified', title: 'Memory guard active', change: 'Background web pages reclaim first · games keep CPU priority', live: '836 MB live client memory', tone: 'steam' },
    { id: 'internet', label: 'Internet', tag: 'Verified', title: '2.5G Ethernet', change: 'Adaptive stack applied · Cloudflare encrypted DNS', live: '8.4 ms idle · 0.8 ms jitter · 0% loss', tone: 'internet' },
    { id: 'nvidia', label: 'NVIDIA', tag: 'Verified', title: 'GeForce RTX 4070', change: 'Low-latency base profile · per-game overrides', live: '62 driver pins · 29 game profiles verified', tone: 'nvidia' },
  ]

  return (
    <div className="home-page page-enter" data-testid="page-home">
      <div className="home-plate glass">
        <header className="status-head stagger-child">
          <span className="status-head__rail" aria-hidden="true" />
          <div>
            <p className="eyebrow">System status</p>
            <h1 data-testid="hero-brand">4 / 4 verified</h1>
          </div>
          <p className="status-head__summary" data-testid="hero-tagline">Every optimizer has a verified apply record.</p>
          <span className="status-head__live"><i /> LIVE · 5 SEC</span>
        </header>

        <section className="memory-strip stagger-child" data-testid="home-memory">
          <div>
            <p className="eyebrow">System memory</p>
            <strong>{formatBytes(seed.memoryUsedBytes)}</strong>
          </div>
          <div className="memory-strip__meter">
            <p>{formatBytes(seed.memoryTotalBytes - seed.memoryUsedBytes)} free · {formatBytes(seed.memoryTotalBytes)} total</p>
            <span><i style={{ width: `${usedPercent}%` }} /></span>
          </div>
          <b>{usedPercent}% in use</b>
        </section>

        <section className="outcome-grid stagger-child" data-testid="home-signals">
          {outcomes.map((item) => (
            <article className={`outcome-card outcome-card--${item.tone}`} key={item.id} data-testid={`home-stat-${item.id}`}>
              <span className="outcome-card__rail" aria-hidden="true" />
              <div className="outcome-card__body">
                <header><p className="eyebrow">{item.label}</p><span>{item.tag}</span></header>
                <strong>{item.title}</strong>
                <p className="outcome-card__change">{item.change}</p>
                <p className="outcome-card__live">{item.live}</p>
              </div>
            </article>
          ))}
        </section>
      </div>
    </div>
  )
}
