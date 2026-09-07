export type AnalysisType = "reconstruction" | "abnormality" | "both";

export type JobState = "pending" | "running" | "completed" | "failed";

export type StepStatus = "waiting" | "running" | "completed" | "failed";

export interface StepProgress {
  id: string;
  label: string;
  detail: string;
  status: StepStatus;
  percent: number | null;
}

export interface PipelineProgress {
  key: string;
  label: string;
  steps: StepProgress[];
}

export interface UploadResponse {
  session_id: string;
  file_count: number;
  message: string;
}

export interface JobStatusResponse {
  job_id: string;
  state: JobState;
  analysis_type: AnalysisType;
  pipelines: PipelineProgress[];
  elapsed_seconds: number;
  error: string | null;
  stl_ready: boolean;
  abnormality_ready: boolean;
}

export interface ReconstructionResult {
  stl_url: string;
  vertex_count: number;
  face_count: number;
  file_size_bytes: number;
  included_structures: string[];
}

export interface AbnormalityFinding {
  id: string;
  label: string;
  slice_index: number;
  confidence: number;
  bbox: [number, number, number, number];
  volume_mm3: number | null;
  note: string;
}

export interface AbnormalityResult {
  mock: boolean;
  total_slices: number;
  findings: AbnormalityFinding[];
  summary: string;
}

export interface ResultsResponse {
  job_id: string;
  reconstruction: ReconstructionResult | null;
  abnormality: AbnormalityResult | null;
}
