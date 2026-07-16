import './SegmentedControl.css'

export interface SegmentOption {
  id: string
  label: string
  testId: string
  onSelect?: () => void
}

interface SegmentedControlProps {
  options: SegmentOption[]
  activeId: string
  onChange: (id: string) => void
}

export function SegmentedControl({
  options,
  activeId,
  onChange,
}: SegmentedControlProps) {
  const activeIndex = Math.max(
    0,
    options.findIndex((o) => o.id === activeId),
  )

  return (
    <div
      className="segmented-control glass glass--soft"
      data-testid="segmented-control"
      role="tablist"
    >
      <span
        className="segmented-control__pill"
        style={{
          width: `${100 / options.length}%`,
          transform: `translateX(${activeIndex * 100}%)`,
        }}
        aria-hidden="true"
      />
      {options.map((opt) => (
        <button
          key={opt.id}
          type="button"
          role="tab"
          aria-selected={activeId === opt.id}
          className={`segmented-control__btn ${activeId === opt.id ? 'is-active' : ''}`}
          data-testid={opt.testId}
          onClick={() => {
            onChange(opt.id)
            opt.onSelect?.()
          }}
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}
