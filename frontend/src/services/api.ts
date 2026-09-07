import type {
  AnalysisType,
  JobStatusResponse,
  ResultsResponse,
  UploadResponse,
} from "../types";

const BASE = "/api";

async function handleJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.json().catch(() => ({ detail: res.statusText }));
    throw new Error(body.detail || `Request failed (${res.status})`);
  }
  return res.json() as Promise<T>;
}

/** Uploads a full DICOM folder (from a webkitdirectory input or drag & drop). */
export async function uploadDicomFolder(files: File[]): Promise<UploadResponse> {
  const form = new FormData();
  for (const file of files) {
    form.append("files", file, file.webkitRelativePath || file.name);
  }
  const res = await fetch(`${BASE}/upload`, { method: "POST", body: form });
  return handleJson<UploadResponse>(res);
}

export async function startAnalysis(
  sessionId: string,
  analysisType: AnalysisType
): Promise<{ job_id: string }> {
  const res = await fetch(`${BASE}/analyze`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ session_id: sessionId, analysis_type: analysisType }),
  });
  return handleJson(res);
}

export async function getJobStatus(jobId: string): Promise<JobStatusResponse> {
  const res = await fetch(`${BASE}/status/${jobId}`);
  return handleJson<JobStatusResponse>(res);
}

export async function getResults(jobId: string): Promise<ResultsResponse> {
  const res = await fetch(`${BASE}/results/${jobId}`);
  return handleJson<ResultsResponse>(res);
}

export function stlDownloadUrl(jobId: string): string {
  return `${BASE}/download/${jobId}/stl`;
}

export function sliceImageUrl(jobId: string, sliceIndex: number): string {
  return `${BASE}/slice-image/${jobId}/${sliceIndex}`;
}

export async function resetSession(sessionId: string, jobId?: string): Promise<void> {
  const params = new URLSearchParams({ session_id: sessionId });
  if (jobId) params.set("job_id", jobId);
  await fetch(`${BASE}/reset?${params.toString()}`, { method: "POST" });
}

/**
 * Polls job status every `intervalMs` until it completes or fails.
 *
 * Long-running jobs (TotalSegmentator on CPU can take several minutes)
 * poll dozens of times, and a single transient failure - a dev-server
 * hiccup, a dropped connection, a momentary network blip - should not
 * kill the whole flow. We only surface an error and stop polling after
 * `maxConsecutiveFailures` in a row; any successful poll resets the
 * counter. The backend job keeps running regardless - this only
 * affects whether the frontend is still watching it.
 */
export function pollJobStatus(
  jobId: string,
  onUpdate: (status: JobStatusResponse) => void,
  onDone: (status: JobStatusResponse) => void,
  onError: (err: Error) => void,
  intervalMs = 1200,
  maxConsecutiveFailures = 5
): () => void {
  let cancelled = false;
  let consecutiveFailures = 0;

  const tick = async () => {
    if (cancelled) return;
    try {
      const status = await getJobStatus(jobId);
      if (cancelled) return;
      consecutiveFailures = 0;
      onUpdate(status);
      if (status.state === "completed" || status.state === "failed") {
        onDone(status);
        return;
      }
      setTimeout(tick, intervalMs);
    } catch (err) {
      if (cancelled) return;
      consecutiveFailures += 1;
      if (consecutiveFailures >= maxConsecutiveFailures) {
        onError(err as Error);
        return;
      }
      // Transient failure - back off slightly and keep polling instead
      // of abandoning a job that may still be running successfully on
      // the backend.
      setTimeout(tick, intervalMs * 2);
    }
  };

  tick();
  return () => {
    cancelled = true;
  };
}
