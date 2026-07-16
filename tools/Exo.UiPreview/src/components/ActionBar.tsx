import './ActionBar.css'

interface ActionBarProps {
  variant?: 'standard' | 'internet' | 'nvidia' | 'panel'
  applyLabel?: string
  repairLabel?: string
  hint?: string
  onApply?: () => void
  onLatency?: () => void
  onThroughput?: () => void
  onRepair?: () => void
  onRefresh?: () => void
  onOpenCpl?: () => void
  message?: string | null
}

export function ActionBar({
  variant = 'standard',
  applyLabel = 'Apply',
  repairLabel = 'Repair',
  hint,
  onApply,
  onLatency,
  onThroughput,
  onRepair,
  onRefresh,
  onOpenCpl,
  message,
}: ActionBarProps) {
  return (
    <footer className="action-foot" data-testid="action-bar">
      {message ? (
        <div className="action-foot__message" data-testid="action-message">
          {message}
        </div>
      ) : null}

      <div className="action-foot__controls">
        {variant === 'internet' ? (
          <div className="action-foot__dual">
            <button
              type="button"
              className="btn btn-primary"
              data-testid="btn-low-latency"
              onClick={onLatency}
            >
              Low latency
            </button>
            <button
              type="button"
              className="btn btn-primary"
              data-testid="btn-highest-download"
              onClick={onThroughput}
            >
              Highest download
            </button>
          </div>
        ) : variant === 'panel' ? (
          <div className="action-foot__secondary">
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-open-cpl"
              onClick={onOpenCpl}
            >
              Open NVIDIA Control Panel
            </button>
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-refresh"
              onClick={onRefresh}
            >
              Refresh
            </button>
          </div>
        ) : (
          <button
            type="button"
            className="btn btn-primary action-foot__grow"
            data-testid="btn-apply"
            onClick={onApply}
          >
            {applyLabel}
          </button>
        )}

        {variant !== 'panel' && variant !== 'internet' ? (
          <div className="action-foot__secondary">
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-repair"
              onClick={onRepair}
            >
              {repairLabel}
            </button>
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-refresh"
              onClick={onRefresh}
            >
              Refresh
            </button>
          </div>
        ) : null}

        {variant === 'internet' ? (
          <div className="action-foot__secondary">
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-repair"
              onClick={onRepair}
            >
              {repairLabel}
            </button>
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-refresh"
              onClick={onRefresh}
            >
              Refresh
            </button>
          </div>
        ) : null}
      </div>

      {hint ? <p className="action-foot__hint">{hint}</p> : null}
    </footer>
  )
}
