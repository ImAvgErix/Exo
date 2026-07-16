import './StatusCapsule.css'

interface StatusCapsuleProps {
  applied: boolean
  label?: string
}

export function StatusCapsule({ applied, label }: StatusCapsuleProps) {
  const text = label ?? (applied ? 'Applied' : 'Open')
  return (
    <span
      className={`status-capsule ${applied ? 'is-applied' : ''}`}
      data-testid="status-capsule"
    >
      <span className="status-capsule__dot" aria-hidden="true" />
      {text}
    </span>
  )
}
