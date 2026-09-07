import React, { createContext, useContext, useState, useCallback } from "react";
import type { AnalysisType, ResultsResponse } from "../types";
import { resetSession } from "./api";

interface SessionState {
  sessionId: string | null;
  fileCount: number;
  analysisType: AnalysisType | null;
  jobId: string | null;
  results: ResultsResponse | null;
  setUpload: (sessionId: string, fileCount: number) => void;
  setAnalysisType: (type: AnalysisType) => void;
  setJobId: (jobId: string) => void;
  setResults: (results: ResultsResponse) => void;
  reset: () => Promise<void>;
}

const SessionCtx = createContext<SessionState | null>(null);

export function SessionProvider({ children }: { children: React.ReactNode }) {
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [fileCount, setFileCount] = useState(0);
  const [analysisType, setAnalysisTypeState] = useState<AnalysisType | null>(null);
  const [jobId, setJobIdState] = useState<string | null>(null);
  const [results, setResultsState] = useState<ResultsResponse | null>(null);

  const setUpload = useCallback((sid: string, count: number) => {
    setSessionId(sid);
    setFileCount(count);
  }, []);

  const setAnalysisType = useCallback((type: AnalysisType) => setAnalysisTypeState(type), []);
  const setJobId = useCallback((id: string) => setJobIdState(id), []);
  const setResults = useCallback((r: ResultsResponse) => setResultsState(r), []);

  const reset = useCallback(async () => {
    if (sessionId) {
      await resetSession(sessionId, jobId ?? undefined).catch(() => {});
    }
    setSessionId(null);
    setFileCount(0);
    setAnalysisTypeState(null);
    setJobIdState(null);
    setResultsState(null);
  }, [sessionId, jobId]);

  return (
    <SessionCtx.Provider
      value={{
        sessionId,
        fileCount,
        analysisType,
        jobId,
        results,
        setUpload,
        setAnalysisType,
        setJobId,
        setResults,
        reset,
      }}
    >
      {children}
    </SessionCtx.Provider>
  );
}

export function useSession(): SessionState {
  const ctx = useContext(SessionCtx);
  if (!ctx) throw new Error("useSession must be used within SessionProvider");
  return ctx;
}
