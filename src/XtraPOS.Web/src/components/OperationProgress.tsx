interface OperationProgressProps {
  label: string;
  percent?: number | null;
}

export function OperationProgress({ label, percent }: OperationProgressProps) {
  const known = typeof percent === "number" && Number.isFinite(percent);
  const value = known ? Math.min(100, Math.max(0, percent)) : 0;

  return (
    <div className="xp-progress">
      <div className="xp-progress-label">
        <span>{label}</span>
        {known ? <span>{Math.round(value)}%</span> : null}
      </div>
      <div
        className="xp-progress-track"
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={known ? Math.round(value) : undefined}
      >
        <span className={known ? "xp-progress-bar" : "xp-progress-bar is-indeterminate"} style={known ? { width: `${value}%` } : undefined} />
      </div>
    </div>
  );
}
