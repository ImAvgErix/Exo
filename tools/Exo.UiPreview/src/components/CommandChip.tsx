import './CommandChip.css'

interface CommandChipProps {
  label: string
  testId?: string
  onClick?: () => void
  disabled?: boolean
}

export function CommandChip({
  label,
  testId,
  onClick,
  disabled,
}: CommandChipProps) {
  return (
    <button
      type="button"
      className="command-chip"
      data-testid={testId}
      onClick={onClick}
      disabled={disabled}
    >
      {label}
    </button>
  )
}
