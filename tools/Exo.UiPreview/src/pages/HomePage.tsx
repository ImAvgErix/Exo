import type { DirectoryCard, ModuleId } from '../data/mock'
import { directoryCards } from '../data/mock'
import './HomePage.css'

interface HomePageProps {
  onOpen: (id: ModuleId) => void
}

export function HomePage({ onOpen }: HomePageProps) {
  const handleCard = (card: DirectoryCard) => {
    if (card.comingSoon) return
    onOpen(card.id as ModuleId)
  }

  const live = directoryCards.filter((c) => !c.comingSoon)
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

      <div className="home-page__rack stagger-child" role="list">
        <div className="home-blade glass">
          {live.map((card, index) => (
            <button
              key={card.id}
              type="button"
              className={`home-blade__seg ${index > 0 ? 'has-divider' : ''}`}
              data-testid={`card-${card.id}`}
              role="listitem"
              onClick={() => handleCard(card)}
            >
              <img
                src={card.logo}
                alt=""
                width={28}
                height={28}
                className="home-blade__logo"
                draggable={false}
              />
              <span className="home-blade__title">{card.title}</span>
            </button>
          ))}
        </div>
      </div>

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
