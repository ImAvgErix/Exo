import { APP_VERSION } from '../data/mock'
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
        className="settings-flyout"
        data-testid="settings-flyout"
        role="dialog"
        aria-label="Settings"
      >
        <p className="settings-flyout__section">APPEARANCE</p>
        <div className="settings-flyout__theme">
          <button
            type="button"
            className={`settings-flyout__choice ${darkMode ? 'is-active' : ''}`}
            data-testid="settings-theme-dark"
            onClick={() => onDarkMode(true)}
          >
            Dark
          </button>
          <button
            type="button"
            className={`settings-flyout__choice ${!darkMode ? 'is-active' : ''}`}
            data-testid="settings-theme-light"
            onClick={() => onDarkMode(false)}
          >
            Light
          </button>
        </div>

        <div className="settings-flyout__divider" />

        <p className="settings-flyout__section">UPDATES</p>
        <div className="settings-flyout__row">
          <span>Check on launch</span>
          <button
            type="button"
            className={`settings-flyout__switch ${autoUpdate ? 'is-on' : ''}`}
            data-testid="settings-auto-update"
            role="switch"
            aria-checked={autoUpdate}
            onClick={() => onAutoUpdate(!autoUpdate)}
          >
            <span className="settings-flyout__knob" />
          </button>
        </div>
        <button
          type="button"
          className="btn btn-primary settings-flyout__check"
          data-testid="settings-check-updates"
        >
          Check for updates
        </button>
        <p className="settings-flyout__status">Up to date</p>

        <div className="settings-flyout__divider" />

        <div className="settings-flyout__actions">
          <button type="button" className="btn btn-quiet" data-testid="settings-report">
            Report issue
          </button>
          <button type="button" className="btn btn-quiet" data-testid="settings-logs">
            Open logs
          </button>
        </div>

        <div className="settings-flyout__version">
          <span>App version</span>
          <strong data-testid="settings-version">{APP_VERSION}</strong>
        </div>
      </aside>
    </>
  )
}
