import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { ExoApp } from '../components/ExoApp'

// Host bridge falls back to mock data outside WebView2.

describe('ExoApp AMOLED shell', () => {
  it('renders home meters and module nav', async () => {
    render(<ExoApp />)

    await waitFor(() => {
      expect(screen.getByLabelText('This PC')).toBeInTheDocument()
    })
    expect(screen.getByLabelText('Home')).toBeInTheDocument()
    expect(screen.getByLabelText('Modules')).toBeInTheDocument()
    expect(screen.getByLabelText('Settings')).toBeInTheDocument()
    expect(screen.getByLabelText('NVIDIA')).toBeInTheDocument()
    expect(screen.getByLabelText('Windows')).toBeInTheDocument()
  })

  it('opens a module page when an optimizer icon is clicked', async () => {
    const user = userEvent.setup()
    render(<ExoApp />)

    await waitFor(() => {
      expect(screen.getByLabelText('NVIDIA')).toBeInTheDocument()
    })
    await user.click(screen.getByLabelText('NVIDIA'))
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'NVIDIA' })).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: /Apply|Reapply|Retry/i })).toBeInTheDocument()
  })

  it('keeps a single Update action in the gear menu (no step text)', async () => {
    const user = userEvent.setup()
    render(<ExoApp />)

    await user.click(screen.getByLabelText('Settings'))
    expect(screen.getByLabelText('Update Exo')).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: /View logs/i })).toBeInTheDocument()
    // Old multi-step update chrome must not appear.
    expect(screen.queryByText(/Check for updates/i)).not.toBeInTheDocument()
    expect(screen.queryByText('CHECK FOR UPDATES')).not.toBeInTheDocument()
    expect(screen.queryByText('TEXT COLOUR')).not.toBeInTheDocument()
  })

  it('exposes close next to settings (no minimize)', () => {
    render(<ExoApp />)
    expect(screen.queryByLabelText('Minimize')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Close')).toBeInTheDocument()
    expect(screen.getByLabelText('Settings')).toBeInTheDocument()
  })
})
