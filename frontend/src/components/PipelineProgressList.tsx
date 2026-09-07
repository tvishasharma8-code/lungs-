import type { PipelineProgress, StepStatus } from "../types";

export default function PipelineProgressList({ pipeline }: { pipeline: PipelineProgress }) {
  return (
    <div className="panel p-5">
      <p className="mb-4 text-sm font-medium text-slate-200">{pipeline.label}</p>
      <div className="flex flex-col gap-4">
        {pipeline.steps.map((step) => (
          <div key={step.id} className="flex items-start gap-3">
            <StatusIcon status={step.status} />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-3">
                <p
                  className={[
                    "text-sm",
                    step.status === "waiting" ? "text-slate-500" : "text-slate-200",
                  ].join(" ")}
                >
                  {step.label}
                </p>
                <StatusLabel status={step.status} percent={step.percent} />
              </div>
              {step.detail && step.status === "running" && (
                <p className="mt-0.5 truncate text-xs text-slate-500">{step.detail}</p>
              )}
              {step.status === "running" && (
                <div className="mt-2 h-1 w-full overflow-hidden rounded-full bg-white/5">
                  <div
                    className="h-full rounded-full bg-accent-500 transition-all duration-500"
                    style={{ width: `${step.percent ?? 15}%` }}
                  />
                </div>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function StatusIcon({ status }: { status: StepStatus }) {
  if (status === "completed") {
    return (
      <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-glow-cyan/20 text-glow-cyan">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none">
          <path d="M5 13l4 4L19 7" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
    );
  }
  if (status === "running") {
    return (
      <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-accent-500/20">
        <span className="h-2.5 w-2.5 animate-pulse rounded-full bg-accent-400" />
      </span>
    );
  }
  if (status === "failed") {
    return (
      <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-red-500/20 text-red-400">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none">
          <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" />
        </svg>
      </span>
    );
  }
  return <span className="mt-0.5 h-5 w-5 shrink-0 rounded-full border border-white/10" />;
}

function StatusLabel({ status, percent }: { status: StepStatus; percent: number | null }) {
  if (status === "completed") return <span className="shrink-0 text-xs text-glow-cyan">Completed</span>;
  if (status === "failed") return <span className="shrink-0 text-xs text-red-400">Failed</span>;
  if (status === "running")
    return (
      <span className="shrink-0 text-xs text-accent-300">
        {percent != null ? `${percent}%` : "Running…"}
      </span>
    );
  return <span className="shrink-0 text-xs text-slate-600">Waiting</span>;
}
