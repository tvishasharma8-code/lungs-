import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import Header from "../components/Header";
import STLViewer from "../components/STLViewer";
import { sliceImageUrl, stlDownloadUrl } from "../services/api"
import { useSession } from "../services/SessionContext";

type Tab = "reconstruction" | "abnormality";

export default function Results() {
  const navigate = useNavigate();
  const { results, jobId, reset } = useSession();
  const [tab, setTab] = useState<Tab>("reconstruction");

  useEffect(() => {
    if (!results || !jobId) {
      navigate("/upload");
      return;
    }
    if (!results.reconstruction && results.abnormality) setTab("abnormality");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [results, jobId]);

  if (!results || !jobId) return null;

  const hasBoth = !!results.reconstruction && !!results.abnormality;

  const handleNewAnalysis = async () => {
    await reset();
    navigate("/");
  };

  return (
    <div className="min-h-screen">
      <Header step={3} />
      <main className="mx-auto max-w-6xl px-6 py-10">
        <div className="mb-6 flex items-center justify-between">
          <div>
            <h2 className="font-display text-xl font-semibold text-slate-100">
              Analysis Complete <span className="text-glow-cyan">✓</span>
            </h2>
            <p className="text-sm text-slate-500">Explore the results from the completed analysis.</p>
          </div>
          <button className="btn-secondary" onClick={handleNewAnalysis}>
            <RefreshIcon />
            New Analysis
          </button>
        </div>

        {hasBoth && (
          <div className="mb-6 inline-flex rounded-lg border border-white/10 bg-white/[0.02] p-1">
            <TabButton active={tab === "reconstruction"} onClick={() => setTab("reconstruction")}>
              3D Reconstruction
            </TabButton>
            <TabButton active={tab === "abnormality"} onClick={() => setTab("abnormality")}>
              AI Abnormality Detection
            </TabButton>
          </div>
        )}

        {tab === "reconstruction" && results.reconstruction && (
          <ReconstructionPanel stl={results.reconstruction} jobId={jobId} />
        )}
        {tab === "abnormality" && results.abnormality && (
          <AbnormalityPanel result={results.abnormality} jobId={jobId} />
        )}
      </main>
    </div>
  );
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className={[
        "rounded-md px-4 py-2 text-sm font-medium transition-colors",
        active ? "bg-accent-500 text-white" : "text-slate-400 hover:text-slate-200",
      ].join(" ")}
    >
      {children}
    </button>
  );
}

function ReconstructionPanel({
  stl,
  jobId,
}: {
  stl: NonNullable<import("../types").ResultsResponse["reconstruction"]>;
  jobId: string;
}) {
  const downloadUrl = stlDownloadUrl(jobId);
  return (
    <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_280px]">
      <div className="panel flex flex-col overflow-hidden">
        <div className="flex items-center justify-between border-b border-white/5 px-5 py-3">
          <p className="font-display text-sm font-medium text-slate-200">3D Lung Model</p>
          <span className="rounded-full bg-glow-cyan/15 px-2.5 py-1 text-[11px] text-glow-cyan">
            STL Generated
          </span>
        </div>
        <div className="h-[440px] w-full bg-[radial-gradient(circle_at_center,rgba(139,92,246,0.08),transparent_60%)]">
          <STLViewer stlUrl={downloadUrl} />
        </div>
      </div>

      <div className="flex flex-col gap-5">
        <div className="panel p-5">
          <p className="font-display text-sm font-medium text-slate-200">Model Information</p>
          <dl className="mt-4 space-y-3 text-xs">
            <Row label="Vertices" value={stl.vertex_count.toLocaleString()} />
            <Row label="Faces" value={stl.face_count.toLocaleString()} />
            <Row label="Format" value="STL" />
            <Row label="File Size" value={formatBytes(stl.file_size_bytes)} />
            <Row label="Structures" value={`${stl.included_structures.length} segmented`} />
          </dl>
        </div>

        <div className="panel p-5">
          <p className="font-display text-sm font-medium text-slate-200">Included Structures</p>
          <ul className="mt-3 space-y-1.5 text-xs text-slate-400">
            {stl.included_structures.map((s) => (
              <li key={s} className="flex items-center gap-2">
                <span className="h-1.5 w-1.5 rounded-full bg-accent-400" />
                {s.replaceAll("_", " ")}
              </li>
            ))}
          </ul>
        </div>

        <a href={downloadUrl} download className="btn-primary">
          <DownloadIcon />
          Download STL
        </a>
      </div>
    </div>
  );
}

function AbnormalityPanel({
  result,
  jobId,
}: {
  result: import("../types").AbnormalityResult;
  jobId: string;
}) {
  return (
    <div className="flex flex-col gap-5">
      {result.mock ? (
        <div className="rounded-lg border border-amber-500/25 bg-amber-500/10 px-4 py-3 text-xs text-amber-300">
          Mock output — this pipeline uses simulated findings until the team's trained model is
          integrated. Do not use for clinical decisions.
        </div>
      ) : (
        <div className="rounded-lg border border-amber-500/25 bg-amber-500/10 px-4 py-3 text-xs text-amber-300">
          AI-assisted detection output — for research/demo purposes only. Not a clinical
          diagnosis; always confirm findings with a qualified radiologist.
        </div>
      )}

      <div className="panel p-5">
        <p className="font-display text-sm font-medium text-slate-200">Summary</p>
        <p className="mt-2 text-sm text-slate-400">{result.summary}</p>
        <p className="mt-1 text-xs text-slate-600">Scanned {result.total_slices} slices.</p>
      </div>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {result.findings.map((f) => (
          <FindingCard key={f.id} finding={f} jobId={jobId} />
        ))}
      </div>
    </div>
  );
}

function FindingCard({
  finding,
  jobId,
}: {
  finding: import("../types").AbnormalityFinding;
  jobId: string;
}) {
  const [xmin, ymin, xmax, ymax] = finding.bbox;
  return (
    <div className="panel overflow-hidden">
      {/* Native CT slices are typically near-square (e.g. 512x512), so a
          square container with object-contain keeps the overlay's
          percentage-based positioning aligned with the image in the
          vast majority of real scans. */}
      <div className="relative aspect-square bg-black">
        <img
          src={sliceImageUrl(jobId, finding.slice_index)}
          alt={`CT slice ${finding.slice_index}`}
          className="absolute inset-0 h-full w-full object-contain"
        />
        <div
          className="absolute border-2 border-red-500 shadow-[0_0_0_1px_rgba(0,0,0,0.5)]"
          style={{
            left: `${xmin * 100}%`,
            top: `${ymin * 100}%`,
            width: `${(xmax - xmin) * 100}%`,
            height: `${(ymax - ymin) * 100}%`,
          }}
        />
        <span className="absolute left-2 top-2 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-mono text-red-400">
          {finding.label}
        </span>
      </div>
      <div className="p-4">
        <div className="flex items-center justify-between">
          <p className="text-sm font-medium text-slate-200">{finding.label}</p>
          <span className="rounded-full bg-accent-500/15 px-2 py-0.5 text-[11px] text-accent-300">
            {(finding.confidence * 100).toFixed(0)}% confidence
          </span>
        </div>
        <p className="mt-1 text-xs text-slate-500">Slice #{finding.slice_index}</p>
        {finding.volume_mm3 && (
          <p className="mt-0.5 text-xs text-slate-500">Est. volume: {finding.volume_mm3} mm³</p>
        )}
        {finding.note && <p className="mt-2 text-[11px] italic text-slate-600">{finding.note}</p>}
      </div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between">
      <dt className="text-slate-500">{label}</dt>
      <dd className="font-mono text-slate-300">{value}</dd>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(1)} KB`;
  return `${(kb / 1024).toFixed(1)} MB`;
}

function DownloadIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
      <path d="M12 3v12m0 0l-4-4m4 4l4-4M5 21h14" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
function RefreshIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none">
      <path
        d="M3 12a9 9 0 0115.3-6.4M21 12a9 9 0 01-15.3 6.4M21 5v5h-5M3 19v-5h5"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
