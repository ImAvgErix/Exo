import type { ModuleId } from '../data/mock'
import { directoryCards, homeModules } from '../data/mock'
import './HomePage.css'

interface HomePageProps {
  onOpen: (id: ModuleId) => void
}

const pillars = [
  { title: 'Trim', detail: 'Aggressive memory and background cut' },
  { title: 'Debloat', detail: 'Client strip without breaking repair' },
  { title: 'Latency', detail: 'Stack, GPU, and FPS-facing paths' },
] as const

export function HomePage({ onOpen }: HomePageProps) {
  const soon = directoryCards.filter((c) => c.comingSoon)

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

        <div className="home-plate__pillars stagger-child" aria-label="What Exo does">
          {pillars.map((p) => (
            <div key={p.title} className="home-pillar">
              <div className="home-pillar__title">{p.title}</div>
              <div className="home-pillar__detail">{p.detail}</div>
            </div>
          ))}
        </div>

        <div className="home-plate__modules stagger-child" role="list">
          <p className="home-plate__section">Modules</p>
          {homeModules.map((item) => (
            <button
              key={item.id}
              type="button"
              className="home-mod"
              data-testid={`card-${item.id}`}
              role="listitem"
              onClick={() => onOpen(item.id)}
            >
              <span className="home-mod__title">{item.title}</span>
              <span className="home-mod__meta">Ready</span>
              <span className="home-mod__chev" aria-hidden="true">
                ›
              </span>
            </button>
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
