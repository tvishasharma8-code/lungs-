import { useCallback, useEffect, useState } from "react";
import {
  deleteVRPatient,
  getUnityStatus,
  listVRPatients,
  sendToVR,
  setActiveVRPatient,
  viewInUnity,
} from "../services/api";
import type { VRPatient } from "../types";

/**
 * "Send to VR" plus the patients already kept for the headset. The VR app follows
 * whichever patient is active here (it checks every few seconds), so sending or
 * choosing one swaps the lungs in the headset without touching it.
 */
export default function VRPanel({ jobId }: { jobId: string }) {
  const [label, setLabel] = useState("");
  const [patients, setPatients] = useState<VRPatient[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  // null until known; the button works either way and explains the outcome.
  const [unity, setUnity] = useState<{ built: boolean; editor: boolean } | null>(null);

  // The backend names a job's package after the job (see vr_service.create_package).
  const thisScanId = jobId.replaceAll("-", "").slice(0, 12);
  const sent = patients.some((p) => p.id === thisScanId);

  const refresh = useCallback(async () => {
    try {
      setPatients(await listVRPatients());
    } catch {
      // The list is a convenience; sending still works without it.
    }
  }, []);

  useEffect(() => {
    refresh();
    getUnityStatus()
      .then((s) => setUnity({ built: s.built, editor: s.editor }))
      .catch(() => setUnity(null));
  }, [refresh]);

  const run = async (action: () => Promise<unknown>, success: string) => {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      if (success) setMessage({ ok: true, text: success });
      await refresh();
    } catch (err) {
      setMessage({ ok: false, text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="panel p-5">
      <p className="font-display text-sm font-medium text-slate-200">View in VR / Unity</p>
      <p className="mt-1 text-xs text-slate-500">
        Shows these lungs breathing with the BioGears simulation - in the headset, or in the
        Unity app on this PC with all the sliders. Kept until you remove it here.
      </p>

      <input
        type="text"
        value={label}
        onChange={(e) => setLabel(e.target.value)}
        placeholder="Name in VR (optional)"
        maxLength={60}
        className="mt-4 w-full rounded-lg border border-white/10 bg-white/5 px-3 py-2 text-xs text-slate-200 placeholder:text-slate-600 focus:border-accent-400 focus:outline-none"
      />
      <button
        className="btn-primary mt-3 w-full"
        disabled={busy}
        onClick={() =>
          run(() => sendToVR(jobId, label), "Sent - the headset will show it within a few seconds.")
        }
      >
        <HeadsetIcon />
        {sent ? "Send to VR again" : "Send to VR"}
      </button>
      <button
        className="btn-secondary mt-2 w-full"
        disabled={busy}
        title="Open these lungs in Unity on this PC, with all the sliders"
        onClick={() =>
          run(async () => {
            const r = await viewInUnity(jobId, label);
            setMessage({
              ok: true,
              text:
                r.mode === "editor"
                  ? "Sent to the open Unity Editor - switch to Unity; in Play mode the lungs appear within a few seconds (press Play if it isn't running)."
                  : r.started
                    ? "Opening the Unity app - the lungs appear once it has loaded."
                    : "Showing in the Unity app (already open - switch to its window).",
            });
          }, "")
        }
      >
        <MonitorIcon />
        View in Unity
      </button>
      {unity && !unity.built && (
        <p className="mt-1 text-[11px] text-slate-500">
          {unity.editor
            ? "Uses the open Unity Editor. For a standalone window, build once in Unity: BioGears > Build Desktop App (Windows)."
            : "Open the project in Unity (press Play), or build the app once in Unity: BioGears > Build Desktop App (Windows)."}
        </p>
      )}

      {message && (
        <p className={`mt-2 text-[11px] ${message.ok ? "text-glow-cyan" : "text-red-400"}`}>{message.text}</p>
      )}

      {patients.length > 0 && (
        <div className="mt-5 border-t border-white/5 pt-4">
          <p className="text-xs font-medium text-slate-300">Patients in VR</p>
          <ul className="mt-2 space-y-2">
            {patients.map((p) => (
              <li key={p.id} className="flex items-center gap-2 text-xs">
                <span
                  className={`h-1.5 w-1.5 shrink-0 rounded-full ${p.active ? "bg-glow-cyan" : "bg-slate-600"}`}
                />
                <span className="min-w-0 flex-1 truncate text-slate-400" title={p.label}>
                  {p.label}
                  {p.id === thisScanId && <span className="text-slate-600"> (this scan)</span>}
                </span>
                {p.active ? (
                  <span className="text-[11px] text-glow-cyan">Showing</span>
                ) : (
                  <button
                    className="text-[11px] text-accent-300 hover:text-accent-200 disabled:opacity-40"
                    disabled={busy}
                    onClick={() => run(() => setActiveVRPatient(p.id), `Showing ${p.label} in VR.`)}
                  >
                    Show
                  </button>
                )}
                <button
                  className="text-[11px] text-slate-600 hover:text-red-400 disabled:opacity-40"
                  disabled={busy}
                  title="Remove from VR"
                  onClick={() => {
                    if (window.confirm(`Remove "${p.label}" from VR?`))
                      run(() => deleteVRPatient(p.id), `Removed ${p.label}.`);
                  }}
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function MonitorIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
      <path
        d="M4 5h16a1 1 0 011 1v10a1 1 0 01-1 1H4a1 1 0 01-1-1V6a1 1 0 011-1zm5 15h6m-3-3v3"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function HeadsetIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
      <path
        d="M3 9a2 2 0 012-2h14a2 2 0 012 2v6a2 2 0 01-2 2h-4l-2-2.5h-2L9 17H5a2 2 0 01-2-2V9z"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinejoin="round"
      />
    </svg>
  );
}
