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

  return (
    <div className="home-page" data-testid="page-home">
      <div className="home-page__stack">
        <h1 className="home-page__brand" data-testid="hero-brand">
          Exo
        </h1>
        <p className="home-page__tagline" data-testid="hero-tagline">
          Maximum performance. No compromise.
        </p>

        <div className="home-page__cards">
          {directoryCards.map((card) => (
            <button
              key={card.id}
              type="button"
              className={`home-card ${card.comingSoon ? 'is-soon' : ''}`}
              data-testid={`card-${card.id}`}
              disabled={card.comingSoon}
              onClick={() => handleCard(card)}
            >
              <img
                src={card.logo}
                alt=""
                width={44}
                height={44}
                className="home-card__logo"
                draggable={false}
              />
              <div className="home-card__text">
                <span className="home-card__title">{card.title}</span>
                {card.comingSoon ? (
                  <span className="home-card__soon">Coming soon</span>
                ) : null}
              </div>
              <span className="home-card__chevron" aria-hidden="true">
                ›
              </span>
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}
