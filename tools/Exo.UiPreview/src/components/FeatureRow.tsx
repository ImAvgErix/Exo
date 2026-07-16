import type { FeatureItem } from '../data/mock'
import { StatusCapsule } from './StatusCapsule'
import './FeatureRow.css'

interface FeatureRowProps {
  feature: FeatureItem
}

export function FeatureRow({ feature }: FeatureRowProps) {
  return (
    <div
      className={`feature-row glass glass--soft stagger-child ${feature.applied ? 'is-applied' : ''}`}
      data-testid={`feature-${feature.id}`}
    >
      <span
        className="feature-row__check"
        data-testid={`feature-rail-${feature.id}`}
        aria-hidden="true"
      >
        {feature.applied ? (
          <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
            <path
              d="M3 7.2 5.8 10 11 3.5"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        ) : (
          <span className="feature-row__ring" />
        )}
      </span>
      <span className="feature-row__icon" aria-hidden="true">
        {feature.icon}
      </span>
      <div className="feature-row__text">
        <div className="feature-row__title">{feature.title}</div>
        <div className="feature-row__detail">{feature.detail}</div>
      </div>
      <StatusCapsule applied={feature.applied} />
    </div>
  )
}
