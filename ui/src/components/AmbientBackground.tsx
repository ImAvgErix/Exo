/** Drifting blurred color blobs — the Liquid Glass ambient wash behind content. */
export function AmbientBackground() {
  return (
    <div className="exo-ambient" aria-hidden>
      <span className="exo-blob b1" />
      <span className="exo-blob b2" />
      <span className="exo-blob b3" />
    </div>
  )
}
