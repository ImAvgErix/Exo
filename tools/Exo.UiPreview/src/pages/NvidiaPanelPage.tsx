import { useState } from 'react'
import { fakeDisplays } from '../data/mock'
import { ActionBar } from '../components/ActionBar'
import './NvidiaPanelPage.css'

export function NvidiaPanelPage() {
  const [message, setMessage] = useState<string | null>(null)
  const [vibrance, setVibrance] = useState(
    Object.fromEntries(fakeDisplays.map((d) => [d.id, d.vibrance])),
  )

  const flash = (text: string) => {
    setMessage(text)
    window.setTimeout(() => setMessage(null), 2200)
  }

  return (
    <div className="nvidia-panel page-enter" data-testid="page-nvidia-panel">
      <div className="nvidia-panel__body">
        <header className="nvidia-panel__header glass glass--mid stagger-child">
          <div>
            <p className="nvidia-panel__section">DISPLAY</p>
            <h2 className="nvidia-panel__status" data-testid="nvidia-panel-status">
              2 displays
            </h2>
            <p className="nvidia-panel__detail">Selectors only — mock preview data.</p>
          </div>
        </header>

        <div className="nvidia-panel__list stagger-list">
          {fakeDisplays.map((display) => (
            <article
              key={display.id}
              className="display-sheet glass glass--soft stagger-child"
              data-testid={`display-${display.id}`}
            >
              <div className="display-sheet__top">
                <h3 className="display-sheet__title">{display.title}</h3>
                <button
                  type="button"
                  className="btn btn-primary display-sheet__apply"
                  data-testid={`btn-apply-${display.id}`}
                  onClick={() => flash(`Applied ${display.title} (mock).`)}
                >
                  Apply
                </button>
              </div>

              <div className="display-sheet__grid">
                <label className="display-field">
                  <span>Resolution</span>
                  <div className="display-field__segment">
                    <select
                      defaultValue={display.resolution}
                      data-testid={`${display.id}-resolution`}
                    >
                      <option>{display.resolution}</option>
                      <option>1920 × 1080</option>
                      <option>3840 × 2160</option>
                    </select>
                  </div>
                </label>
                <label className="display-field">
                  <span>Refresh rate</span>
                  <div className="display-field__segment">
                    <select
                      defaultValue={display.refresh}
                      data-testid={`${display.id}-refresh`}
                    >
                      <option>{display.refresh}</option>
                      <option>144 Hz</option>
                      <option>240 Hz</option>
                    </select>
                  </div>
                </label>
                <label className="display-field">
                  <span>Color depth</span>
                  <div className="display-field__segment">
                    <select defaultValue={display.depth}>
                      <option>{display.depth}</option>
                      <option>10-bit</option>
                    </select>
                  </div>
                </label>
                <label className="display-field">
                  <span>NVIDIA color</span>
                  <div className="display-field__segment">
                    <select defaultValue={display.colorRange}>
                      <option>{display.colorRange}</option>
                      <option>Limited</option>
                      <option>Full</option>
                    </select>
                  </div>
                </label>
                <label className="display-field display-field--span">
                  <span>Scaling</span>
                  <div className="display-field__segment">
                    <select defaultValue={display.scaling}>
                      <option>{display.scaling}</option>
                      <option>Fullscreen</option>
                      <option>Aspect ratio</option>
                    </select>
                  </div>
                </label>
                <div className="display-field display-field--span">
                  <div className="display-field__vibrance-label">
                    <span>Digital vibrance</span>
                    <span data-testid={`${display.id}-vibrance-value`}>
                      {vibrance[display.id]}%
                    </span>
                  </div>
                  <div className="vibrance-track">
                    <div
                      className="vibrance-track__fill"
                      style={{ width: `${vibrance[display.id]}%` }}
                    />
                    <input
                      type="range"
                      min={0}
                      max={100}
                      value={vibrance[display.id]}
                      data-testid={`${display.id}-vibrance`}
                      onChange={(e) =>
                        setVibrance((prev) => ({
                          ...prev,
                          [display.id]: Number(e.target.value),
                        }))
                      }
                    />
                  </div>
                </div>
              </div>
            </article>
          ))}
        </div>
      </div>

      <ActionBar
        variant="panel"
        message={message}
        onOpenCpl={() => flash('Open NVIDIA Control Panel (mock).')}
        onRefresh={() => flash('Displays refreshed (mock).')}
      />
    </div>
  )
}
