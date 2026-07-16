import './MetricStrip.css'

export interface MetricItem {
  label: string
  value: string | number
}

interface MetricStripProps {
  metrics: MetricItem[]
}

export function MetricStrip({ metrics }: MetricStripProps) {
  return (
    <div className="metric-strip glass glass--soft" data-testid="metric-strip">
      {metrics.map((m, i) => (
        <div key={m.label} className="metric-strip__item">
          {i > 0 ? <span className="metric-strip__divider" aria-hidden="true" /> : null}
          <div className="metric-strip__body">
            <span className="metric-strip__value">{m.value}</span>
            <span className="metric-strip__label">{m.label}</span>
          </div>
        </div>
      ))}
    </div>
  )
}
