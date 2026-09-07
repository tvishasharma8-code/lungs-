import React from "react";

const STEPS = [
  { id: 1, label: "Upload" },
  { id: 2, label: "Process" },
  { id: 3, label: "Results" },
];

export default function Stepper({ current }: { current: number }) {
  return (
    <div className="flex items-center gap-2 text-sm">
      {STEPS.map((step, i) => {
        const isActive = step.id === current;
        const isDone = step.id < current;
        return (
          <React.Fragment key={step.id}>
            <div className="flex items-center gap-2">
              <span
                className={[
                  "flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-mono transition-colors",
                  isActive
                    ? "bg-accent-500 text-white"
                    : isDone
                    ? "bg-accent-500/20 text-accent-300"
                    : "bg-white/5 text-slate-500",
                ].join(" ")}
              >
                {String(step.id).padStart(2, "0")}
              </span>
              <span className={isActive ? "text-slate-100 font-medium" : "text-slate-500"}>
                {step.label}
              </span>
            </div>
            {i < STEPS.length - 1 && <span className="mx-1 h-px w-8 bg-white/10" />}
          </React.Fragment>
        );
      })}
    </div>
  );
}
