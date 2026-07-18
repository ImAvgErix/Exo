import { useState } from 'react'
import type { ModuleData } from '../data/mock'
import { ActionBar } from '../components/ActionBar'
import { FeatureRow } from '../components/FeatureRow'
import { StatusCapsule } from '../components/StatusCapsule'
import './ModulePage.css'

interface ModulePageProps { module: ModuleData }

export function ModulePage({ module }: ModulePageProps) {
  const [message, setMessage] = useState<string | null>(null)
  const [gsync, setGsync] = useState(false)
  const [statusTitle, setStatusTitle] = useState(module.statusTitle)

  const appliedCount = module.features.filter((f) => f.applied).length
  const allApplied = appliedCount === module.features.length
  const isApplied = allApplied || statusTitle === 'Applied'

  const flash = (text: string) => {
    setMessage(text)
    window.setTimeout(() => setMessage(null), 2200)
  }

  return (
    <div className="module-page page-enter" data-testid={`page-${module.id}`}>
      <div className="module-plate glass">
        <header className="module-plate__header stagger-child">
          <div className="module-plate__header-text">
            <p className="module-plate__section">{module.section}</p>
            <h2 className="module-plate__status" data-testid={`${module.id}-status`}>
              {statusTitle}
            </h2>
          </div>
          <StatusCapsule
            applied={isApplied}
            label={
              isApplied
                ? 'Applied'
                : `${appliedCount}/${module.features.length} applied`
            }
          />
        </header>

        {module.variant === 'nvidia' ? (
          <div
            className="module-plate__nvidia-controls stagger-child"
            data-testid="nvidia-controls"
          >
            <label className="module-plate__gsync">
              <span>G-SYNC</span>
              <button
                type="button"
                className={`module-plate__switch ${gsync ? 'is-on' : ''}`}
                data-testid="toggle-gsync"
                role="switch"
                aria-checked={gsync}
                onClick={() => setGsync((v) => !v)}
              >
                <span className="module-plate__knob" />
              </button>
            </label>
            <button
              type="button"
              className="btn btn-ghost"
              data-testid="btn-open-nvidia-cpl"
              onClick={() => flash('Opened NVIDIA Control Panel (mock).')}
            >
              Open NVIDIA Control Panel
            </button>
          </div>
        ) : null}

        <div
          className="module-plate__features stagger-list"
          data-testid={`${module.id}-features`}
        >
          {module.features.map((feature, index) => (
            <FeatureRow
              key={feature.id}
              feature={feature}
              isLast={index === module.features.length - 1}
            />
          ))}
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
            flash(module.variant === 'internet'
              ? 'Connection analyzed, tuned, and encrypted DNS selected (mock).'
              : 'Apply complete (mock).')
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
    </div>
  )
}
