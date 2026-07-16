import { useState } from 'react'
import type { ModuleData } from '../data/mock'
import { ActionBar } from '../components/ActionBar'
import { FeatureRow } from '../components/FeatureRow'
import { StatusCapsule } from '../components/StatusCapsule'
import './ModulePage.css'

interface ModulePageProps {
  module: ModuleData
  onOpenDisplayPanel?: () => void
}

export function ModulePage({ module, onOpenDisplayPanel }: ModulePageProps) {
  const [message, setMessage] = useState<string | null>(null)
  const [gsync, setGsync] = useState(true)
  const [statusTitle, setStatusTitle] = useState(module.statusTitle)

  const appliedCount = module.features.filter((f) => f.applied).length
  const allApplied = appliedCount === module.features.length

  const flash = (text: string) => {
    setMessage(text)
    window.setTimeout(() => setMessage(null), 2200)
  }

  return (
    <div className="module-page page-enter" data-testid={`page-${module.id}`}>
      <div className="module-page__body">
        <header className="module-page__header glass glass--mid stagger-child">
          <div className="module-page__header-text">
            <p className="module-page__section">{module.section}</p>
            <h2 className="module-page__status" data-testid={`${module.id}-status`}>
              {statusTitle}
            </h2>
          </div>
          <StatusCapsule
            applied={allApplied || statusTitle === 'Applied'}
            label={
              statusTitle === 'Applied'
                ? 'Applied'
                : `${appliedCount}/${module.features.length}`
            }
          />
        </header>

        {module.variant === 'nvidia' ? (
          <div
            className="module-page__nvidia-controls glass glass--soft stagger-child"
            data-testid="nvidia-controls"
          >
            <label className="module-page__gsync">
              <span>G-SYNC</span>
              <button
                type="button"
                className={`module-page__switch ${gsync ? 'is-on' : ''}`}
                data-testid="toggle-gsync"
                role="switch"
                aria-checked={gsync}
                onClick={() => setGsync((v) => !v)}
              >
                <span className="module-page__knob" />
              </button>
            </label>
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-display-panel"
              onClick={onOpenDisplayPanel}
            >
              Display panel
            </button>
          </div>
        ) : null}

        <div
          className="module-page__features stagger-list"
          data-testid={`${module.id}-features`}
        >
          {module.features.map((feature) => (
            <FeatureRow key={feature.id} feature={feature} />
          ))}
        </div>
      </div>

      <ActionBar
        variant={module.variant}
        applyLabel={module.applyLabel}
        repairLabel={module.repairLabel}
        hint={
          module.variant === 'nvidia'
            ? 'Reset clears Exo status only — NVIDIA driver and profiles stay.'
            : module.variant === 'internet'
              ? 'Repair restores prior network stack markers.'
              : undefined
        }
        message={message}
        onApply={() => {
          setStatusTitle('Applied')
          flash('Apply complete (mock).')
        }}
        onLatency={() => {
          setStatusTitle('Low latency profile')
          flash('Low latency stack applied (mock).')
        }}
        onThroughput={() => {
          setStatusTitle('Highest download profile')
          flash('Highest download stack applied (mock).')
        }}
        onRepair={() => {
          setStatusTitle(
            module.variant === 'nvidia' ? 'Status cleared' : 'Repair complete',
          )
          flash(
            module.variant === 'nvidia'
              ? 'Exo NVIDIA status cleared (mock).'
              : 'Repair finished (mock).',
          )
        }}
        onRefresh={() => {
          setStatusTitle(module.statusTitle)
          flash('Refreshed (mock).')
        }}
      />
    </div>
  )
}
