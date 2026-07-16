import type { ModuleId } from '../data/mock'
import { directoryCards } from '../data/mock'
import './HomePage.css'

/** Live modules are opened from the top bar — home is brand + coming soon only. */
export function HomePage(_props: { onOpen: (id: ModuleId) => void }) {
  const soon = directoryCards.filter((c) => c.comingSoon)

  return (
    <div className="home-page page-enter" data-testid="page-home">
      <header className="home-page__hero stagger-child">
        <h1 className="home-page__brand" data-testid="hero-brand">
          Exo
        </h1>
        <p className="home-page__tagline" data-testid="hero-tagline">
          Maximum performance. No compromise.
        </p>
      </header>

      <p className="home-page__soon stagger-child">
        <span className="home-page__soon-label">Coming soon</span>
        <span className="home-page__soon-items">
          {soon.map((card) => (
            <span key={card.id} className="home-page__soon-item">
              <button
                type="button"
                className="home-page__soon-chip"
                data-testid={`card-${card.id}`}
                disabled
                aria-disabled="true"
              >
                {card.title}
              </button>
            </span>
          ))}
        </span>
      </p>
    </div>
  )
}
