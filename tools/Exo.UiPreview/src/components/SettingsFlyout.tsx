import { APP_VERSION } from '../data/mock'
import { SegmentedControl } from './SegmentedControl'
import './SettingsFlyout.css'

interface SettingsFlyoutProps {
  open: boolean
  darkMode: boolean
  autoUpdate: boolean
  onClose: () => void
  onDarkMode: (dark: boolean) => void
  onAutoUpdate: (on: boolean) => void
}

export function SettingsFlyout({
  open,
  darkMode,
  autoUpdate,
  onClose,
  onDarkMode,
  onAutoUpdate,
}: SettingsFlyoutProps) {
  if (!open) return null

  return (
    <>
      <button
        type="button"
        className="settings-backdrop"
        aria-label="Close settings"
        data-testid="settings-backdrop"
        onClick={onClose}
      />
      <aside
        className="settings-sheet glass glass--strong"
        data-testid="settings-flyout"
        role="dialog"
        aria-label="Settings"
      >
        <div className="settings-sheet__group glass glass--soft">
          <p className="settings-sheet__section">APPEARANCE</p>
          <div className="settings-sheet__theme">
            <SegmentedControl
              options={[
                { id: 'dark', label: 'Dark', testId: 'settings-theme-dark' },
                { id: 'light', label: 'Light', testId: 'settings-theme-light' },
              ]}
              activeId={darkMode ? 'dark' : 'light'}
              onChange={(id) => onDarkMode(id === 'dark')}
            />
          </div>
        </div>

        <div className="settings-sheet__group glass glass--soft">
          <p className="settings-sheet__section">UPDATES</p>
          <div className="settings-sheet__row">
            <span>Check on launch</span>
            <button
              type="button"
              className={`settings-sheet__switch ${autoUpdate ? 'is-on' : ''}`}
              data-testid="settings-auto-update"
              role="switch"
              aria-checked={autoUpdate}
              onClick={() => onAutoUpdate(!autoUpdate)}
            >
              <span className="settings-sheet__knob" />
            </button>
          </div>
          <button
            type="button"
            className="btn btn-primary settings-sheet__check"
            data-testid="settings-check-updates"
          >
            Check for updates
          </button>
          <p className="settings-sheet__status">Up to date</p>
        </div>

        <div className="settings-sheet__actions">
          <button type="button" className="btn btn-ghost" data-testid="settings-report">
            Report issue
          </button>
          <button type="button" className="btn btn-ghost" data-testid="settings-logs">
            Open logs
          </button>
        </div>

        <div className="settings-sheet__version">
          <span>App version</span>
          <strong data-testid="settings-version">{APP_VERSION}</strong>
        </div>
      </aside>
    </>
  )
}
