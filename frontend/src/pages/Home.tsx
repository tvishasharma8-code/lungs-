import { useNavigate } from "react-router-dom";
import Header from "../components/Header";
import LogoMark from "../components/LogoMark";

export default function Home() {
  const navigate = useNavigate();

  return (
    <div className="min-h-screen">
      <Header />
      <main className="mx-auto max-w-6xl px-6 py-16">
        <div className="grid grid-cols-1 items-center gap-12 lg:grid-cols-2">
          <div>
            <h1 className="font-display text-4xl font-semibold leading-tight text-slate-50 sm:text-5xl">
              From CT Scans to{" "}
              <span className="bg-gradient-to-r from-accent-400 to-glow-cyan bg-clip-text text-transparent">
                Immersive Lung Models
              </span>
            </h1>
            <p className="mt-5 max-w-md text-sm leading-relaxed text-slate-400">
              Generate patient-specific 3D lung models from CT scans and screen for
              potential abnormalities, powered by a real segmentation pipeline.
            </p>
            <button onClick={() => navigate("/upload")} className="btn-primary mt-8">
              Start Analysis
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
                <path d="M5 12h14M13 6l6 6-6 6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </button>

            <div className="mt-12 grid grid-cols-1 gap-4 sm:grid-cols-3">
              <FeatureCard
                title="3D Reconstruction"
                desc="Generate accurate 3D lung models from CT scans as STL files."
              />
              <FeatureCard
                title="AI Abnormality Detection"
                desc="Detect and highlight potential abnormalities (mock pipeline for now)."
              />
              <FeatureCard
                title="VR Visualization"
                desc="Explore the 3D model in VR on your Meta Quest headset (coming soon)."
              />
            </div>
          </div>

          <div className="relative mx-auto flex h-80 w-full max-w-md items-center justify-center rounded-2xl border border-white/5 bg-grid-fade bg-[length:28px_28px]">
            <div className="absolute h-40 w-40 rounded-full bg-accent-500/20 blur-3xl" />
            <LogoMark size={180} className="relative text-accent-300" />
          </div>
        </div>
      </main>
    </div>
  );
}

function FeatureCard({ title, desc }: { title: string; desc: string }) {
  return (
    <div className="panel p-4">
      <p className="font-display text-sm font-medium text-slate-200">{title}</p>
      <p className="mt-1.5 text-xs leading-relaxed text-slate-500">{desc}</p>
    </div>
  );
}
