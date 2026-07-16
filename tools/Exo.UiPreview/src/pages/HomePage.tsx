import { useEffect, useState } from 'react'
import { directoryCards, homeDashboardSeed } from '../data/mock'
import './HomePage.css'

function formatBytes(bytes: number): string {
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`
  if (bytes >= 1 << 20) return `${Math.round(bytes / (1 << 20))} MB`
  if (bytes >= 1 << 10) return `${Math.round(bytes / (1 << 10))} KB`
  return `${Math.max(0, bytes)} B`
}

function sparkHeights(hourly: number[]): number[] {
  if (hourly.length === 0) return []
  const max = Math.max(1, ...hourly)
  return hourly.map((v) => 6 + (v / max) * 28)
}

export function HomePage() {
  const soon = directoryCards.filter((c) => c.comingSoon)
  const seed = homeDashboardSeed

  const [memoryUsed, setMemoryUsed] = useState(seed.memoryUsedBytes)
  const memoryLoad = Math.round((memoryUsed / seed.memoryTotalBytes) * 100)

  // Soft live tick — preview-only jitter around the seeded sample.
  useEffect(() => {
    const id = window.setInterval(() => {
      setMemoryUsed((prev) => {
        const drift = (Math.random() - 0.5) * (24 << 20)
        return Math.max(
          seed.memoryTotalBytes * 0.35,
          Math.min(seed.memoryTotalBytes * 0.92, prev + drift),
        )
      })
    }, 2000)
    return () => window.clearInterval(id)
  }, [seed.memoryTotalBytes])

  const heroBytes =
    seed.trimLast24hBytes > 0 ? seed.trimLast24hBytes : seed.trimTotalBytes
  const latencyDelta = seed.latencyAfterP50 - seed.latencyBeforeP50
  const latencySign = latencyDelta > 0 ? '+' : ''
  const sparks = sparkHeights(seed.hourlyBytes)

  return (
    <div className="home-page page-enter" data-testid="page-home">
      <div className="home-plate glass">
        <header className="home-plate__hero stagger-child">
          <h1 className="home-plate__brand" data-testid="hero-brand">
            Exo
          </h1>
          <p className="home-plate__tagline" data-testid="hero-tagline">
            Maximum performance. No compromise.
          </p>
        </header>

        <section
          className="home-ram stagger-child"
          data-testid="home-ram"
          aria-label="RAM reclaimed"
        >
          <p className="home-plate__section">RAM reclaimed</p>
          <div className="home-ram__value" data-testid="home-ram-value">
            {formatBytes(heroBytes)}
          </div>
          <p className="home-ram__meta">
            {seed.trimLast24hBytes > 0
              ? `last 24 h · ${formatBytes(seed.trimTotalBytes)} total`
              : 'total reclaimed (Steam webhelper working set)'}
          </p>
          {sparks.length > 0 ? (
            <div className="home-spark" aria-hidden="true">
              {sparks.map((h, i) => (
                <span
                  key={i}
                  className="home-spark__bar"
                  style={{ height: `${h}px` }}
                />
              ))}
            </div>
          ) : null}
        </section>

        <div className="home-stats stagger-child" data-testid="home-stats">
          <article className="home-stat" data-testid="home-stat-memory">
            <p className="home-plate__section">Memory</p>
            <div className="home-stat__row">
              <span className="home-stat__value">{formatBytes(memoryUsed)}</span>
              <span className="home-stat__badge">{memoryLoad}%</span>
            </div>
            <p className="home-stat__meta">
              in use · {formatBytes(seed.memoryTotalBytes)} total
            </p>
          </article>

          <article className="home-stat" data-testid="home-stat-latency">
            <p className="home-plate__section">Latency</p>
            <div className="home-stat__value">
              {latencySign}
              {latencyDelta.toFixed(1)} ms
            </div>
            <p className="home-stat__meta">
              ping p50 {seed.latencyBeforeP50.toFixed(1)} →{' '}
              {seed.latencyAfterP50.toFixed(1)} ms
            </p>
          </article>

          <article className="home-stat" data-testid="home-stat-passes">
            <p className="home-plate__section">Trim passes</p>
            <div className="home-stat__value">
              {seed.trimPasses.toLocaleString()}
            </div>
            <p className="home-stat__meta">Steam webhelper trim passes</p>
          </article>
        </div>

        <footer className="home-plate__foot stagger-child">
          <span className="home-plate__soon-label">Coming soon</span>
          <span className="home-plate__soon-items">
            {soon.map((card) => (
              <button
                key={card.id}
                type="button"
                className="home-plate__soon-chip"
                data-testid={`card-${card.id}`}
                disabled
                aria-disabled="true"
              >
                {card.title}
              </button>
            ))}
          </span>
        </footer>
      </div>
    </div>
  )
}
