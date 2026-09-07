import { Link } from "react-router-dom";
import Stepper from "./Stepper";
import LogoMark from "./LogoMark";

export default function Header({ step }: { step?: number }) {
  return (
    <header className="sticky top-0 z-20 border-b border-white/5 bg-base-950/80 backdrop-blur-md">
      <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
        <Link to="/" className="flex items-center gap-3">
          <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-accent-500/15 text-accent-300">
            <LogoMark size={18} />
          </span>
          <div className="leading-tight">
            <p className="font-display text-[15px] font-semibold text-slate-100">LungVision</p>
            <p className="text-[11px] text-slate-500">CT → 3D Lung Reconstruction &amp; AI Analysis</p>
          </div>
        </Link>
        {step && <Stepper current={step} />}
      </div>
    </header>
  );
}
