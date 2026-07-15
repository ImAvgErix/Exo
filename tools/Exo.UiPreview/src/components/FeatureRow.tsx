import type { FeatureItem } from '../data/mock'
import './FeatureRow.css'

interface FeatureRowProps {
  feature: FeatureItem
}

export function FeatureRow({ feature }: FeatureRowProps) {
  return (
    <div
      className={`feature-row ${feature.applied ? 'is-applied' : ''}`}
      data-testid={`feature-${feature.id}`}
    >
      <span
        className="feature-row__rail"
        data-testid={`feature-rail-${feature.id}`}
        aria-hidden="true"
      />
      <span className="feature-row__icon" aria-hidden="true">
        {feature.icon}
      </span>
      <div className="feature-row__text">
        <div className="feature-row__title">{feature.title}</div>
        <div className="feature-row__detail">{feature.detail}</div>
      </div>
    </div>
  )
}
