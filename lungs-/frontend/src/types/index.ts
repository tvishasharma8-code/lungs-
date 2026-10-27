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

/** One tumor: the per-slice detections that belong together (backend tumor_cluster.py). */
export interface TumorFinding {
  id: string;
  max_confidence: number;
  mean_confidence: number;
  slice_count: number;
  slice_range: [number, number];
  key_slice: number;
  key_bbox: [number, number, number, number];
  center_mm: [number, number, number] | null;
  size_mm: [number, number, number] | null;
  volume_mm3: number | null;
  /** Lobe (TotalSegmentator name) or "outside the lungs"; null without 3D reconstruction. */
  location: string | null;
}

export interface AbnormalityResult {
  mock: boolean;
  total_slices: number;
  findings: AbnormalityFinding[];
  summary: string;
  tumors?: TumorFinding[];
  scanned_slices?: number | null;
}

export interface ResultsResponse {
  job_id: string;
  reconstruction: ReconstructionResult | null;
  abnormality: AbnormalityResult | null;
}

/** A patient kept for the VR app ("Send to VR"), from GET /api/vr/patients. */
export interface VRPatient {
  id: string;
  label: string;
  created_utc: string;
  triangles: number | null;
  structures: string[];
  has_findings: boolean;
  active: boolean;
}
