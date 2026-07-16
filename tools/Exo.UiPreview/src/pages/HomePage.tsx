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
      <div className="home-page__atmosphere" aria-hidden="true">
        <span className="home-page__blob home-page__blob--a" />
        <span className="home-page__blob home-page__blob--b" />
        <span className="home-page__blob home-page__blob--c" />
      </div>

      <header className="home-page__hero stagger-child">
        <h1 className="home-page__brand" data-testid="hero-brand">
          Exo
        </h1>
        <p className="home-page__tagline" data-testid="hero-tagline">
          Maximum performance. No compromise.
        </p>
      </header>

      <div className="home-page__carousel stagger-child" role="list">
        {live.map((card) => (
          <button
            key={card.id}
            type="button"
            className="home-orb glass glass--soft"
            data-testid={`card-${card.id}`}
            role="listitem"
            onClick={() => handleCard(card)}
          >
            <span className="home-orb__glow" aria-hidden="true" />
            <span className="home-orb__icon">
              <img
                src={card.logo}
                alt=""
                width={48}
                height={48}
                className="home-orb__logo"
                draggable={false}
              />
            </span>
            <span className="home-orb__title">{card.title}</span>
          </button>
        ))}
      </div>

      <div className="home-page__soon stagger-child">
        <p className="home-page__soon-label">Coming soon</p>
        <div className="home-page__soon-row">
          {soon.map((card) => (
            <button
              key={card.id}
              type="button"
              className="home-chip glass glass--soft is-soon"
              data-testid={`card-${card.id}`}
              disabled
              aria-disabled="true"
            >
              <img
                src={card.logo}
                alt=""
                width={22}
                height={22}
                className="home-chip__logo"
                draggable={false}
              />
              <span>{card.title}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}
