import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import Header from "../components/Header";
import PipelineProgressList from "../components/PipelineProgressList";
import { getResults, pollJobStatus, startAnalysis } from "../services/api";
import { useSession } from "../services/SessionContext";
import type { JobStatusResponse } from "../types";

export default function Processing() {
  const navigate = useNavigate();
  const { sessionId, analysisType, jobId, setJobId, setResults } = useSession();
  const [status, setStatus] = useState<JobStatusResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (!sessionId || !analysisType) {
      navigate("/upload");
      return;
    }
    if (started.current) return;
    started.current = true;

    let stopPolling: (() => void) | null = null;

    (async () => {
      try {
        const { job_id } = await startAnalysis(sessionId, analysisType);
        setJobId(job_id);

        stopPolling = pollJobStatus(
          job_id,
          (s) => setStatus(s),
          async (finalStatus) => {
            setStatus(finalStatus);
            if (finalStatus.state === "completed") {
              const results = await getResults(job_id);
              setResults(results);
              navigate("/results");
            } else {
              setError(finalStatus.error || "The pipeline failed.");
            }
          },
          (err) => setError(err.message)
        );
      } catch (err) {
        setError((err as Error).message || "Failed to start analysis.");
      }
    })();

    return () => {
      stopPolling?.();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, analysisType]);

  return (
    <div className="min-h-screen">
      <Header step={2} />
      <main className="mx-auto max-w-3xl px-6 py-12">
        <div className="mb-6 flex items-center justify-between">
          <div>
            <h2 className="font-display text-lg font-semibold text-slate-100">Processing Analysis</h2>
            <p className="text-sm text-slate-500">Running the selected pipeline(s) on your CT scan…</p>
          </div>
          {status && (
            <span className="font-mono text-xs text-slate-500">
              Elapsed: {formatElapsed(status.elapsed_seconds)}
            </span>
          )}
        </div>

        {error && (
          <div className="mb-6 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-300">
            {error}
            <div className="mt-3">
              <button className="btn-secondary" onClick={() => navigate("/upload")}>
                Back to Upload
              </button>
            </div>
          </div>
        )}

        {!status && !error && (
          <div className="panel p-6 text-sm text-slate-500">Starting pipeline…</div>
        )}

        {status && (
          <div className="flex flex-col gap-5">
            {status.pipelines.map((p) => (
              <PipelineProgressList key={p.key} pipeline={p} />
            ))}
          </div>
        )}
      </main>
    </div>
  );
}

function formatElapsed(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}
