import { useState } from "react";
import { useNavigate } from "react-router-dom";
import Header from "../components/Header";
import FileDropzone from "../components/FileDropzone";
import AnalysisSelector from "../components/AnalysisSelector";
import { uploadDicomFolder } from "../services/api";
import { useSession } from "../services/SessionContext";
import type { AnalysisType } from "../types";

export default function Upload() {
  const navigate = useNavigate();
  const { setUpload, setAnalysisType, sessionId, fileCount, analysisType } = useSession();

  const [folderName, setFolderName] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleFiles = async (files: File[]) => {
    setError(null);
    const relPath = (files[0] as any).webkitRelativePath as string | undefined;
    const inferredFolder = relPath ? relPath.split("/")[0] : "CT_Scan_Upload";
    setFolderName(inferredFolder);
    setUploading(true);
    try {
      const res = await uploadDicomFolder(files);
      setUpload(res.session_id, res.file_count);
    } catch (err) {
      setError((err as Error).message || "Upload failed.");
      setFolderName(null);
    } finally {
      setUploading(false);
    }
  };

  const canContinue = !!sessionId && !!analysisType && !uploading;

  return (
    <div className="min-h-screen">
      <Header step={1} />
      <main className="mx-auto max-w-3xl px-6 py-12">
        <div className="panel p-6">
          <h2 className="font-display text-lg font-semibold text-slate-100">Upload CT Scan</h2>
          <p className="mt-1 text-sm text-slate-500">
            Upload a DICOM folder containing the patient's CT scan to begin.
          </p>

          <div className="mt-6">
            <FileDropzone
              onFilesSelected={handleFiles}
              selectedFolderName={folderName}
              selectedFileCount={fileCount}
              disabled={uploading}
            />
          </div>
          {uploading && <p className="mt-3 text-xs text-accent-300">Uploading DICOM files…</p>}
          {error && <p className="mt-3 text-xs text-red-400">{error}</p>}

          <div className="mt-8">
            <h3 className="font-display text-sm font-semibold text-slate-100">Choose Analysis Type</h3>
            <p className="mt-1 text-xs text-slate-500">Select one analysis to run on this scan.</p>
            <div className="mt-4">
              <AnalysisSelector
                value={analysisType}
                onChange={(v: AnalysisType) => setAnalysisType(v)}
              />
            </div>
          </div>

          <button
            className="btn-primary mt-8 w-full"
            disabled={!canContinue}
            onClick={() => navigate("/processing")}
          >
            Continue
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
              <path d="M5 12h14M13 6l6 6-6 6" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
          <p className="mt-2 text-center text-[11px] text-slate-600">
            You can run either analysis independently — both are not required.
          </p>
        </div>
      </main>
    </div>
  );
}
