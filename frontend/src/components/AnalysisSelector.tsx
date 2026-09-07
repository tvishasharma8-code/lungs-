import type { AnalysisType } from "../types";

const OPTIONS: { id: AnalysisType; title: string; desc: string; icon: JSX.Element }[] = [
  {
    id: "reconstruction",
    title: "3D Lung Reconstruction",
    desc: "Generate a real 3D STL model from the CT scan using the segmentation pipeline.",
    icon: <LungIcon />,
  },
  {
    id: "abnormality",
    title: "AI Abnormality Detection",
    desc: "Flag potential regions of interest on CT slices. Currently uses mock output.",
    icon: <ScanIcon />,
  },
  {
    id: "both",
    title: "Both Analyses",
    desc: "Run 3D reconstruction and abnormality detection together.",
    icon: <StackIcon />,
  },
];

export default function AnalysisSelector({
  value,
  onChange,
}: {
  value: AnalysisType | null;
  onChange: (v: AnalysisType) => void;
}) {
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
      {OPTIONS.map((opt) => {
        const selected = value === opt.id;
        return (
          <button
            key={opt.id}
            type="button"
            onClick={() => onChange(opt.id)}
            aria-pressed={selected}
            className={[
              "group relative flex flex-col items-start gap-3 rounded-xl border p-4 text-left transition-all",
              selected
                ? "border-accent-500/60 bg-accent-500/10 shadow-glow"
                : "border-white/8 bg-white/[0.02] hover:border-white/15 hover:bg-white/[0.04]",
            ].join(" ")}
          >
            {selected && (
              <span className="absolute right-3 top-3 flex h-5 w-5 items-center justify-center rounded-full bg-accent-500 text-white">
                <CheckIcon />
              </span>
            )}
            <span
              className={[
                "flex h-10 w-10 items-center justify-center rounded-lg",
                selected ? "bg-accent-500/20 text-accent-300" : "bg-white/5 text-slate-400",
              ].join(" ")}
            >
              {opt.icon}
            </span>
            <div>
              <p className="font-display text-sm font-semibold text-slate-100">{opt.title}</p>
              <p className="mt-1 text-xs leading-relaxed text-slate-400">{opt.desc}</p>
            </div>
          </button>
        );
      })}
    </div>
  );
}

function CheckIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none">
      <path d="M5 13l4 4L19 7" stroke="white" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
function LungIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
      <path
        d="M12 2v8m0 0c-1.5-3-4-4-6-3.5C3 7.5 2.5 12 3 15c.4 3 2 6 4 6 1.4 0 2-1 2-3v-8m3 8v8m0-8c1.5-3 4-4 6-3.5.5 1 1 5.5.5 8.5-.4 3-2 6-4 6-1.4 0-2-1-2-3v-8"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
function ScanIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
      <circle cx="12" cy="12" r="3" stroke="currentColor" strokeWidth="1.6" />
      <path
        d="M3 8V5a2 2 0 012-2h3M21 8V5a2 2 0 00-2-2h-3M3 16v3a2 2 0 002 2h3M21 16v3a2 2 0 01-2 2h-3"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
      />
    </svg>
  );
}
function StackIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none">
      <path
        d="M12 3l8 4-8 4-8-4 8-4zM4 13l8 4 8-4M4 17l8 4 8-4"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
