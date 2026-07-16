import type { FeatureItem } from '../data/mock'
import './FeatureRow.css'

interface FeatureRowProps {
  feature: FeatureItem
  isLast?: boolean
}

export function FeatureRow({ feature, isLast }: FeatureRowProps) {
  return (
    <div
      className={`feature-row stagger-child ${feature.applied ? 'is-applied' : ''} ${isLast ? 'is-last' : ''}`}
      data-testid={`feature-${feature.id}`}
    >
      <span
        className="feature-row__rail"
        data-testid={`feature-rail-${feature.id}`}
        aria-hidden="true"
      />
      <div className="feature-row__text">
        <div className="feature-row__title">{feature.title}</div>
        <div className="feature-row__detail">
          {feature.applied ? 'Applied' : 'Not applied'}
        </div>
      </div>
    </div>
  )
}
