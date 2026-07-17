import { directoryCards, homeDashboardSeed } from '../data/mock'
import './HomePage.css'

function formatBytes(bytes: number): string {
  if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GB`
  if (bytes >= 1 << 20) return `${Math.round(bytes / (1 << 20))} MB`
  if (bytes >= 1 << 10) return `${Math.round(bytes / (1 << 10))} KB`
  return `${Math.max(0, bytes)} B`
}

function sparkHeights(values: number[]): number[] {
  if (values.length === 0) return []
  const max = Math.max(1, ...values)
  return values.map((v) => 6 + (v / max) * 28)
}

export function HomePage() {
  const soon = directoryCards.filter((c) => c.comingSoon)
  const seed = homeDashboardSeed

  const heroBytes =
    seed.trimLast24hBytes > 0 ? seed.trimLast24hBytes : seed.trimTotalBytes
  const sparks = sparkHeights(
    seed.frameTimeSeriesMs.map((ms) => 1 / Math.max(0.1, ms)),
  )

  const stats = [
    {
      id: 'ram',
      label: 'RAM reclaimed',
      value: formatBytes(heroBytes),
      meta:
        seed.trimLast24hBytes > 0
          ? `last 24 h · ${formatBytes(seed.trimTotalBytes)} total`
          : 'total reclaimed',
    },
    {
      id: 'latency',
      label: 'Latency',
      value: `${seed.latencyAfterP50.toFixed(1)} ms`,
      meta: `Latest · jitter ${seed.latencyJitter.toFixed(1)} ms · DNS ${seed.latencyDns.toFixed(0)} ms`,
    },
  ]

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

        <div className="home-frame stagger-child" data-testid="home-frame">
          <section className="home-frame__card" data-testid="home-fps" aria-label="FPS gain">
            <p className="home-plate__section">FPS gain</p>
            <div className="home-frame__value" data-testid="home-fps-value">
              +{seed.fpsGainPercent}%
            </div>
            <p className="home-frame__meta">vs pre-apply baseline</p>
          </section>

          <section
            className="home-frame__card"
            data-testid="home-frametime"
            aria-label="Frame time"
          >
            <p className="home-plate__section">Frame time</p>
            <div className="home-frame__value" data-testid="home-frametime-value">
              {seed.frameTimeMs.toFixed(1)} ms
            </div>
            <p className="home-frame__meta">
              avg · 1% low {seed.frameTimeOnePercentMs.toFixed(1)} ms
            </p>
            {sparks.length > 0 ? (
              <div className="home-spark" aria-hidden="true">
                {sparks.map((h, i) => (
                  <span
                    key={i}
                    className="home-spark__bar"
                    style={
                      { height: `${h}px`, '--i': i } as React.CSSProperties
                    }
                  />
                ))}
              </div>
            ) : null}
          </section>
        </div>

        <div className="home-stats stagger-child" data-testid="home-stats">
          {stats.map((stat) => (
            <article
              key={stat.id}
              className="home-stat"
              data-testid={`home-stat-${stat.id}`}
            >
              <p className="home-plate__section">{stat.label}</p>
              <div className="home-stat__value">{stat.value}</div>
              <p className="home-stat__meta">{stat.meta}</p>
            </article>
          ))}
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
