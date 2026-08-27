using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

/// <summary>
/// Drives respiratory physiology from the real BioGears CDM engine (native C++ via
/// biogears_unity.dll) on Windows Editor/Standalone. On any other platform - or if the
/// native plugin fails to load for any reason - this automatically falls back to
/// <see cref="SimulatedBioGearsRespiratory"/> so the rest of the scene keeps working
/// unchanged. This is the component every scene should reference instead of
/// SimulatedBioGearsRespiratory directly.
///
/// BioGears' own patient stabilization (InitializeEngine with a COPD condition) takes
/// several minutes of wall-clock time to converge - far too slow to run live while a
/// user drags a "smoking years" slider. Instead, a handful of COPD severity levels are
/// pre-stabilized offline (see BioGears/wrapper tooling) and saved as BioGears state
/// files shipped in StreamingAssets/BioGears/states. At runtime this component only
/// ever calls BG_LoadState on those pre-baked states, which takes seconds, and swaps
/// between them as the requested severity crosses a bucket boundary.
/// </summary>
public class BioGearsNativeRespiratory : MonoBehaviour, IBioGearsRespiratorySystem
{
    [Header("Native BioGears data (StreamingAssets)")]
    [Tooltip("Folder name under StreamingAssets containing patients/, substances/, environments/, config/, xsd/ and a precomputed states/ subfolder.")]
    public string dataFolderName = "BioGears";

    [Tooltip("Patient file (relative to the data folder) used only if a requested severity has no precomputed state and a live fallback init is required.")]
    public string patientFile = "StandardMale.xml";

    [Header("Fallback (used automatically off Windows or if native load fails)")]
    [Tooltip("Optional explicit fallback. If left empty one is added automatically on this GameObject when needed.")]
    public SimulatedBioGearsRespiratory fallback;

    [Header("Status (read-only)")]
    public bool usingNativeEngine;
    public string statusMessage = "Not started";

    struct SeverityBucket
    {
        public string StateFile;
        public double Bronchitis;
        public double Emphysema;
        public SeverityBucket(string stateFile, double bronchitis, double emphysema)
        {
            StateFile = stateFile;
            Bronchitis = bronchitis;
            Emphysema = emphysema;
        }
    }

    // Must match the buckets pre-stabilized offline (see BioGears/wrapper precompute
    // tooling). Severity thresholds below pick the nearest bucket for a 0-1 COPD
    // severity request - if you add/change buckets here you must also regenerate the
    // matching state files, otherwise LoadState will fail and this falls back to a
    // live (multi-minute) InitializeEngineWithCOPD.
    static readonly SeverityBucket[] Buckets =
    {
        new SeverityBucket("healthy.xml",     0.00, 0.00),
        new SeverityBucket("mild.xml",        0.15, 0.00),
        new SeverityBucket("moderate.xml",    0.35, 0.15),
        new SeverityBucket("significant.xml", 0.55, 0.35),
        new SeverityBucket("severe.xml",      0.75, 0.60),
    };

    #region P/Invoke

    const string DLL = "biogears_unity";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectory(string lpPathName);

    [DllImport(DLL)] static extern int BG_CreateEngine(string workingDir);
    [DllImport(DLL)] static extern int BG_LoadState(string stateFile);
    [DllImport(DLL)] static extern int BG_InitializeEngineWithCOPD(string patientFile, double bronchitisSeverity, double emphysemaSeverity);
    [DllImport(DLL)] static extern int BG_AdvanceTime(double seconds);
    [DllImport(DLL)] static extern void BG_DestroyEngine();
    [DllImport(DLL)] static extern IntPtr BG_GetLastError();
    [DllImport(DLL)] static extern int BG_ApplyAsthmaAttack(double severity);
    [DllImport(DLL)] static extern int BG_ApplyExercise(double intensity);
    [DllImport(DLL)] static extern int BG_ApplyNutrition(double carbohydrate_g, double fat_g, double protein_g, double sodium_g, double water_mL);
    [DllImport(DLL)] static extern int BG_SetOxygenSaturationOverride(double oxygenSaturation);
    [DllImport(DLL)] static extern int BG_ClearOxygenSaturationOverride();

    [StructLayout(LayoutKind.Sequential)]
    struct BG_RespiratoryData
    {
        public double respirationRate;
        public double tidalVolume_mL;
        public double functionalResidualCapacity_mL;
        public double totalLungVolume_mL;
        public double leftLungVolume_mL;
        public double rightLungVolume_mL;
        public double ieRatio;
        public double totalPulmonaryVentilation_mL_per_min;
        public double heartRate;
        public double oxygenSaturation;
        public double pulseOximetry;
        public double carbonMonoxideSaturation;
        public double transpulmonaryPressure_cmH2O;
    }

    [DllImport(DLL)] static extern int BG_GetRespiratorySnapshot(ref BG_RespiratoryData data);

    static string LastNativeError()
    {
        try { return Marshal.PtrToStringAnsi(BG_GetLastError()); }
        catch { return "(unavailable)"; }
    }

    static bool dllSearchPathSet;
    static void EnsureDllSearchPath()
    {
        if (dllSearchPathSet) return;
        // Unity's own native-plugin loader usually resolves sibling DLLs (libbiogears.dll
        // etc.) fine on its own, but SetDllDirectory removes any ambiguity about the
        // Windows DLL search order finding biogears_unity.dll's own dependencies.
        string pluginDir = Path.Combine(Application.dataPath, "Plugins", "x86_64");
        SetDllDirectory(pluginDir);
        dllSearchPathSet = true;
    }

    #endregion

    // ---- Cached snapshot, written by the engine thread, read by the main thread ----
    readonly object dataLock = new object();
    float snapRespirationRate;
    float snapTidalVolume;
    float snapFRC;
    float snapTotalLungVolume;
    float snapIERatio;
    float snapHeartRate;
    float snapOxygenSaturation;
    float snapPulseOximetry;
    float snapCarbonMonoxideSaturation;
    volatile bool engineRunning;

    // Breathing waveform is computed locally every Unity frame from the live
    // RespirationRate/IERatio (mirrors SimulatedBioGearsRespiratory's math) so the
    // visual expansion stays smooth even though the engine thread only publishes a
    // fresh physiology snapshot a few times a second.
    float phase;
    float expansion;
    float breathCycleTime = 60f / 14f;

    // How often (in simulated seconds, run at ~1:1 with real time) to trickle-feed
    // the patient so extended sessions don't drift into unrelated nutrient/fluid
    // depletion pathology. 5 minutes is frequent enough that BioGears' own reserves
    // never run dry between doses.
    const double NutritionIntervalSeconds = 300.0;

    Thread engineThread;
    volatile bool wantStop;
    volatile int targetBucket;
    volatile int loadedBucket = -1;

    // Asthma and a measured SpO2 override are patient actions, not conditions - they
    // apply live to a running engine. But swapping severity buckets destroys and
    // recreates the whole engine (see LoadBucket), which wipes every action along
    // with it. So these are tracked as persistent "current value" state, not one-shot
    // events - reapplied automatically after every successful bucket load, and
    // (re)sent to the engine whenever the value changes.
    volatile bool hasAsthmaSeverity;
    volatile float asthmaSeverity;
    volatile bool asthmaDirty;

    volatile bool hasSpO2Override;
    volatile float spo2Override;
    volatile bool spo2Dirty;

    volatile bool pendingExercise;
    volatile float pendingExerciseSeverity;

    string DataDir => Path.Combine(Application.streamingAssetsPath, dataFolderName);
    string StatesDir => Path.Combine(DataDir, "states");

    // ---- IBioGearsRespiratorySystem ----
    public float RespirationRate => usingNativeEngine ? snapRespirationRate : fallback.RespirationRate;
    public float TidalVolume => usingNativeEngine ? snapTidalVolume : fallback.TidalVolume;
    public float TotalLungVolume => usingNativeEngine ? snapTotalLungVolume : fallback.TotalLungVolume;
    public float FunctionalResidualCapacity => usingNativeEngine ? snapFRC : fallback.FunctionalResidualCapacity;
    public float InspiratoryExpiratoryRatio => usingNativeEngine ? snapIERatio : fallback.InspiratoryExpiratoryRatio;
    public float BreathingPhase => usingNativeEngine ? phase : fallback.BreathingPhase;
    public float ExpansionFactor => usingNativeEngine ? expansion : fallback.ExpansionFactor;
    public float OxygenSaturation => usingNativeEngine ? snapOxygenSaturation : fallback.OxygenSaturation;
    public float HeartRate => usingNativeEngine ? snapHeartRate : fallback.HeartRate;
    public bool IsRunning => usingNativeEngine ? engineRunning : fallback.IsRunning;

    // Extra smoking-relevant readouts not on the shared interface (real markers, not
    // just a cosmetic tint): carboxyhemoglobin rises with smoking independently of
    // COPD, and pulse oximetry is the clinically-displayed SpO2 proxy.
    public float PulseOximetry => usingNativeEngine ? snapPulseOximetry : OxygenSaturation;
    public float CarbonMonoxideSaturation => usingNativeEngine ? snapCarbonMonoxideSaturation : 0f;

    void Awake()
    {
        if (fallback == null)
            fallback = GetComponent<SimulatedBioGearsRespiratory>();

        // Keep the fallback dormant until we actually need it, so its own Update()/
        // debug OnGUI() monitor doesn't run alongside the native engine.
        if (fallback != null)
            fallback.enabled = false;
    }

    void OnEnable()
    {
        Initialize();
    }

    public void Initialize()
    {
        bool windowsTarget = Application.platform == RuntimePlatform.WindowsEditor
            || Application.platform == RuntimePlatform.WindowsPlayer;

        if (!windowsTarget)
        {
            ActivateFallback("Native BioGears only runs on Windows Editor/Standalone (no Android/iOS build of the engine exists). Using the simulated model on this platform.");
            return;
        }

        if (!Directory.Exists(StatesDir))
        {
            ActivateFallback($"No precomputed BioGears states found at {StatesDir}. Using the simulated model.");
            return;
        }

        try
        {
            EnsureDllSearchPath();
            // Touch the DLL now so a missing/mismatched native plugin fails fast and
            // visibly here, rather than crashing the background thread later.
            LastNativeError();
        }
        catch (DllNotFoundException e)
        {
            ActivateFallback("biogears_unity.dll not found (" + e.Message + "). Using the simulated model.");
            return;
        }
        catch (EntryPointNotFoundException e)
        {
            ActivateFallback("biogears_unity.dll is missing an expected export (" + e.Message + "). Rebuild the wrapper. Using the simulated model.");
            return;
        }

        usingNativeEngine = true;
        statusMessage = "Starting BioGears engine thread...";
        wantStop = false;
        engineThread = new Thread(EngineThreadMain) { IsBackground = true, Name = "BioGearsEngine" };
        engineThread.Start();
    }

    void ActivateFallback(string reason)
    {
        Debug.LogWarning("[BioGearsNativeRespiratory] " + reason);
        usingNativeEngine = false;
        statusMessage = reason;
        if (fallback == null)
            fallback = gameObject.AddComponent<SimulatedBioGearsRespiratory>();
        fallback.enabled = true;
        fallback.Initialize();
    }

    void Update()
    {
        if (!usingNativeEngine)
            return;

        if (!engineRunning)
            return;

        // Advance the local breathing waveform every frame from the last-published
        // real RespirationRate/IERatio - see class comment for why this isn't driven
        // directly off the engine thread's own (much slower) publish cadence.
        float rate, ie;
        lock (dataLock)
        {
            rate = snapRespirationRate > 0.1f ? snapRespirationRate : 14f;
            ie = snapIERatio > 0.01f ? snapIERatio : 0.5f;
        }

        breathCycleTime = 60f / rate;
        phase += Time.deltaTime / breathCycleTime;
        if (phase >= 1f) phase -= 1f;

        float inspiratoryFraction = ie / (1f + ie);
        if (phase < inspiratoryFraction)
        {
            float t = phase / inspiratoryFraction;
            expansion = SmoothStep(t);
        }
        else
        {
            float t = (phase - inspiratoryFraction) / (1f - inspiratoryFraction);
            expansion = 1f - SmoothStep(t);
        }
    }

    static float SmoothStep(float t) => t * t * (3f - 2f * t);

    public void AdvanceTime(float deltaTime)
    {
        // Native mode paces itself in real time on its own thread (see
        // EngineThreadMain) so callers don't drive it directly; forward to the
        // fallback so the interface behaves identically in either mode.
        if (!usingNativeEngine)
            fallback.AdvanceTime(deltaTime);
    }

    public void ApplyCondition(string conditionName, float severity)
    {
        severity = Mathf.Clamp01(severity);

        if (!usingNativeEngine)
        {
            fallback.ApplyCondition(conditionName, severity);
            return;
        }

        switch (conditionName.ToLowerInvariant())
        {
            case "copd":
            case "smoking":
                RequestCOPDSeverity(severity);
                break;
            case "asthma":
                asthmaSeverity = severity;
                hasAsthmaSeverity = true;
                asthmaDirty = true;
                break;
            case "exercise":
                pendingExerciseSeverity = severity;
                pendingExercise = true;
                break;
            default:
                Debug.LogWarning($"[BioGearsNativeRespiratory] Unhandled condition: {conditionName}");
                break;
        }
    }

    /// <summary>
    /// Injects a real measured SpO2 reading (0-1 fraction, e.g. 0.97 for 97%) taken
    /// from the actual patient - something no CT scan can tell you. This overrides
    /// BioGears' own computed oxygen saturation rather than trying to reverse-engineer
    /// a condition that would produce this exact number. No-op on the simulated
    /// fallback, which has no override mechanism.
    /// </summary>
    public void SetMeasuredOxygenSaturation(float oxygenSaturation01)
    {
        if (!usingNativeEngine) return;
        spo2Override = Mathf.Clamp01(oxygenSaturation01);
        hasSpO2Override = true;
        spo2Dirty = true;
    }

    /// <summary>
    /// Sets the patient's resting respiratory rate (breaths/min) -> breathing
    /// frequency. On the native engine this is a physiological output rather than an
    /// input, so it is applied to the simulated model, which is also what runs on
    /// Quest (BioGears itself is Windows-only).
    /// </summary>
    public void SetRespirationRate(float breathsPerMinute)
    {
        if (fallback != null)
            fallback.SetBaseRespirationRate(breathsPerMinute);

        if (usingNativeEngine)
            WarnNativeOwnsRespiratoryMechanics("respiratory rate");
    }

    /// <summary>
    /// Sets the patient's resting tidal volume (mL) on the respiratory model. Note the
    /// lung deformation no longer scales with this - the breathing animation was
    /// reverted to its earlier form, which uses a fixed amplitude.
    /// </summary>
    public void SetTidalVolume(float milliliters)
    {
        if (fallback != null)
            fallback.SetBaseTidalVolume(milliliters);

        if (usingNativeEngine)
            WarnNativeOwnsRespiratoryMechanics("tidal volume");
    }

    // Respiratory rate and tidal volume are patient *baselines* on the simulated model,
    // but on the real engine they are derived outputs of the physiology solve - there
    // is no input to set them short of a BioGears SEOverride action, which this wrapper
    // does not currently export. Rather than let the two sliders look wired while
    // silently writing to a disabled component, say so once.
    bool warnedNativeMechanics;
    void WarnNativeOwnsRespiratoryMechanics(string parameterName)
    {
        if (warnedNativeMechanics)
            return;

        warnedNativeMechanics = true;
        Debug.LogWarning($"[BioGearsNativeRespiratory] The native BioGears engine derives {parameterName} from its own physiology, so this slider has no effect while the native engine is active (Windows Editor/Standalone). It does apply on Quest, where the simulated model drives breathing. Asthma / exercise / smoking affect the lung in both modes.");
    }

    public void ClearMeasuredOxygenSaturation()
    {
        hasSpO2Override = false;
        spo2Dirty = true; // engine thread sees hasSpO2Override==false and clears it
    }

    /// <summary>
    /// Maps a 0-1 COPD severity (typically derived from smoking years - see
    /// VRUIController) onto the nearest precomputed severity bucket. Changing bucket
    /// triggers a state swap on the engine thread; staying within a bucket is a no-op,
    /// which naturally debounces slider dragging without extra timers.
    /// </summary>
    public void RequestCOPDSeverity(float severity01)
    {
        severity01 = Mathf.Clamp01(severity01);
        int nearest = 0;
        float best = float.MaxValue;
        for (int i = 0; i < Buckets.Length; i++)
        {
            // Buckets are indexed 0..N-1 spread evenly across 0-1 severity for lookup.
            float bucketPos = (float)i / (Buckets.Length - 1);
            float d = Mathf.Abs(bucketPos - severity01);
            if (d < best) { best = d; nearest = i; }
        }
        targetBucket = nearest;
    }

    void EngineThreadMain()
    {
        string workingDir = DataDir;

        if (BG_CreateEngine(workingDir) == 0)
        {
            statusMessage = "BG_CreateEngine failed: " + LastNativeError();
            Debug.LogError("[BioGearsNativeRespiratory] " + statusMessage);
            return;
        }

        if (!LoadBucket(targetBucket))
        {
            statusMessage = "Initial LoadState failed, engine thread exiting: " + LastNativeError();
            Debug.LogError("[BioGearsNativeRespiratory] " + statusMessage);
            BG_DestroyEngine();
            return;
        }
        engineRunning = true;
        statusMessage = "Running (native BioGears)";
        ReapplyPersistentActions();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        double lastElapsed = 0;
        double nextNutritionAt = NutritionIntervalSeconds;

        while (!wantStop)
        {
            if (targetBucket != loadedBucket)
            {
                engineRunning = false;
                statusMessage = $"Recalculating patient physiology (severity bucket {targetBucket})...";
                if (LoadBucket(targetBucket))
                {
                    stopwatch.Restart();
                    lastElapsed = 0;
                    nextNutritionAt = NutritionIntervalSeconds;
                    engineRunning = true;
                    statusMessage = "Running (native BioGears)";
                    ReapplyPersistentActions();
                }
                else
                {
                    statusMessage = "LoadState failed for bucket " + targetBucket + ": " + LastNativeError();
                    Debug.LogWarning("[BioGearsNativeRespiratory] " + statusMessage);
                    // Keep running on whatever was last successfully loaded rather than
                    // wedging the simulation.
                    engineRunning = loadedBucket >= 0;
                }
            }

            if (asthmaDirty)
            {
                asthmaDirty = false;
                if (hasAsthmaSeverity)
                    BG_ApplyAsthmaAttack(asthmaSeverity);
            }
            if (spo2Dirty)
            {
                spo2Dirty = false;
                if (hasSpO2Override)
                    BG_SetOxygenSaturationOverride(spo2Override);
                else
                    BG_ClearOxygenSaturationOverride();
            }
            if (pendingExercise)
            {
                pendingExercise = false;
                BG_ApplyExercise(pendingExerciseSeverity);
            }

            double now = stopwatch.Elapsed.TotalSeconds;
            double dt = now - lastElapsed;
            lastElapsed = now;
            dt = Math.Min(dt, 0.5); // guard against long stalls (breakpoints, GC, etc.)

            if (engineRunning && dt > 0.0)
            {
                if (BG_AdvanceTime(dt) != 0)
                {
                    PublishSnapshot();
                }
            }

            if (engineRunning && now >= nextNutritionAt)
            {
                nextNutritionAt += NutritionIntervalSeconds;
                // Small trickle dose, not a meal - just enough to offset basal
                // consumption so a long-running session doesn't drift into
                // starvation/dehydration pathology unrelated to smoking. BioGears
                // models a full closed-system patient with no automatic intake.
                BG_ApplyNutrition(carbohydrate_g: 3.0, fat_g: 1.0, protein_g: 1.0, sodium_g: 0.05, water_mL: 50.0);
            }

            Thread.Sleep(50); // ~20Hz publish cadence - plenty for a monitor readout
        }

        BG_DestroyEngine();
    }

    // Marks any patient-entered actions (asthma severity, measured SpO2 override) as
    // needing to be resent to the engine - called after every successful LoadBucket,
    // since recreating the engine wipes them. The actual native calls happen on the
    // next loop iteration via the asthmaDirty/spo2Dirty checks, keeping "send it" in
    // one place instead of duplicated here and in the loop.
    void ReapplyPersistentActions()
    {
        if (hasAsthmaSeverity) asthmaDirty = true;
        if (hasSpO2Override) spo2Dirty = true;
    }

    bool LoadBucket(int index)
    {
        index = Mathf.Clamp(index, 0, Buckets.Length - 1);
        string stateFile = Path.Combine(StatesDir, Buckets[index].StateFile);

        if (File.Exists(stateFile) && BG_LoadState(stateFile) != 0)
        {
            loadedBucket = index;
            return true;
        }

        // No precomputed state for this bucket (or it failed to load) - fall back to a
        // live stabilization. This is correct but can take several minutes; it exists
        // so a missing precomputed bucket degrades to "slow but right" instead of
        // "silently wrong".
        Debug.LogWarning($"[BioGearsNativeRespiratory] No usable precomputed state at {stateFile}, running a live stabilization instead (this can take minutes).");
        var bucket = Buckets[index];
        if (BG_InitializeEngineWithCOPD(patientFile, bucket.Bronchitis, bucket.Emphysema) != 0)
        {
            loadedBucket = index;
            return true;
        }

        return false;
    }

    void PublishSnapshot()
    {
        BG_RespiratoryData data = default;
        if (BG_GetRespiratorySnapshot(ref data) == 0)
            return;

        lock (dataLock)
        {
            snapRespirationRate = (float)data.respirationRate;
            snapTidalVolume = (float)data.tidalVolume_mL;
            snapFRC = (float)data.functionalResidualCapacity_mL;
            snapTotalLungVolume = (float)data.totalLungVolume_mL;
            snapIERatio = (float)data.ieRatio;
            snapHeartRate = (float)data.heartRate;
            snapOxygenSaturation = (float)data.oxygenSaturation;
            snapPulseOximetry = (float)data.pulseOximetry;
            snapCarbonMonoxideSaturation = (float)data.carbonMonoxideSaturation;
        }
    }

    void OnDisable()
    {
        wantStop = true;
    }

    void OnDestroy()
    {
        wantStop = true;
        if (engineThread != null && engineThread.IsAlive)
            engineThread.Join(2000);
    }
}
