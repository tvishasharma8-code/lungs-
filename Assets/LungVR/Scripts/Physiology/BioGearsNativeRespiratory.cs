using System;
using System.Collections.Generic;
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

    [Header("Remote engine (standalone Quest -> PC running BioGearsStreamServer)")]
    [Tooltip("Where native BioGears can't run (the Quest), find a PC on the same Wi-Fi that is running this project with BioGearsStreamServer and use its real engine. The simulated model covers any time the PC isn't reachable.")]
    public bool connectToPcServer = true;
    [Tooltip("PC address to use instead of finding it automatically (leave empty to discover). For networks that block broadcast.")]
    public string pcServerAddress = "";
    [Tooltip("Testing only: on Windows, skip the native engine and connect to a PC server as a headset would.")]
    public bool useRemoteEvenOnWindows = false;

    [Header("Status (read-only)")]
    public bool usingNativeEngine;
    public string statusMessage = "Not started";

    BioGearsLinkClient remote;
    bool remoteActive;
    BioGearsLinkProtocol.Snapshot lastRemoteSnapshot;
    PneumoniaExtent remotePneumonia;
    AndroidJavaObject multicastLock;

    /// <summary>True while the physiology is coming from a PC's real BioGears over the network.</summary>
    public bool usingRemoteEngine => remoteActive;

    /// <summary>IP of the BioGears PC found on the network (headset only), else null.</summary>
    public string PcAddress => remote?.ServerAddress;

    // Values come from the snapshot fields (filled by the native engine thread, or from
    // the PC over the network) rather than from the simulated model.
    bool UseSnapshot => usingNativeEngine || remoteActive;

    /// <summary>Where the numbers come from, for the monitor.</summary>
    public string SourceDescription =>
        usingNativeEngine ? "BioGears engine"
        : remoteActive ? $"BioGears on PC '{remote.ServerName}' (Wi-Fi)"
        : remote != null ? "simulated model - looking for BioGears PC..."
        : "simulated model";

    struct SeverityBucket
    {
        public string StateFile;
        public double Bronchitis;
        public double Emphysema;
        // Lobar pneumonia (0 = none): severity and fraction of each lung consolidated.
        public double Pneumonia;
        public double PneumoniaLeft;
        public double PneumoniaRight;
        public SeverityBucket(string stateFile, double bronchitis, double emphysema)
        {
            StateFile = stateFile;
            Bronchitis = bronchitis;
            Emphysema = emphysema;
            Pneumonia = PneumoniaLeft = PneumoniaRight = 0.0;
        }
        public static SeverityBucket WithPneumonia(string stateFile, double severity, double left, double right) =>
            new SeverityBucket(stateFile, 0.0, 0.0) { Pneumonia = severity, PneumoniaLeft = left, PneumoniaRight = right };
    }

    // Must match the buckets pre-stabilized offline (BioGears/wrapper/precompute_states.ps1
    // - same file names, same values). If you add/change buckets here you must also
    // regenerate the matching state files, otherwise LoadState fails and this falls back
    // to a live (multi-minute) stabilization.
    //
    // A patient has one chronic baseline at a time: the COPD buckets (driven by
    // smoking) or a pneumonia bucket. Pneumonia takes priority while it is set.
    static readonly SeverityBucket[] Buckets =
    {
        new SeverityBucket("healthy.xml",     0.00, 0.00),
        new SeverityBucket("mild.xml",        0.15, 0.00),
        new SeverityBucket("moderate.xml",    0.35, 0.15),
        new SeverityBucket("significant.xml", 0.55, 0.35),
        new SeverityBucket("severe.xml",      0.75, 0.60),
        SeverityBucket.WithPneumonia("pneumonia_mild.xml",   0.30, 0.00, 0.40),
        SeverityBucket.WithPneumonia("pneumonia_severe.xml", 0.70, 0.00, 0.70),
    };
    const int CopdBucketCount = 5;
    const int PneumoniaMildBucket = 5;
    const int PneumoniaSevereBucket = 6;

    // The two baselines are requested independently; targetBucket is whichever wins.
    volatile int copdBucket;
    volatile int pneumoniaBucket = -1;

    #region P/Invoke

    const string DLL = "biogears_unity";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectory(string lpPathName);

    [DllImport(DLL)] static extern int BG_CreateEngine(string workingDir);
    [DllImport(DLL)] static extern int BG_CreateEngineWithLog(string workingDir, string logFile);
    [DllImport(DLL)] static extern int BG_LoadState(string stateFile);
    [DllImport(DLL)] static extern int BG_InitializeEngineWithCOPD(string patientFile, double bronchitisSeverity, double emphysemaSeverity);
    [DllImport(DLL)] static extern int BG_AdvanceTime(double seconds);
    [DllImport(DLL)] static extern void BG_DestroyEngine();
    [DllImport(DLL)] static extern IntPtr BG_GetLastError();
    [DllImport(DLL)] static extern int BG_ApplyAsthmaAttack(double severity);
    [DllImport(DLL)] static extern int BG_ApplyAcuteRespiratoryDistress(double severity);
    // 1 = breath queued, 2 = previous breath still running (nothing queued), 0 = error.
    [DllImport(DLL)] static extern int BG_PacedBreath(double tidalVolume_mL, double inhaleSeconds, double exhaleSeconds);
    [DllImport(DLL)] static extern int BG_InitializeEngineWithPneumonia(string patientFile, double severity, double leftAffected, double rightAffected);
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
    // Added to the wrapper for gas exchange / lung mechanics. Must match the C++ struct
    // field for field; -1 means BioGears has not computed that value.
    [StructLayout(LayoutKind.Sequential)]
    struct BG_GasExchangeData
    {
        public double oxygenConsumption_mL_per_min;
        public double carbonDioxideProduction_mL_per_min;
        public double respiratoryExchangeRatio;
        public double pulmonaryResistance_cmH2O_s_per_L;
        public double pulmonaryCompliance_L_per_cmH2O;
        public double alveolarVentilation_L_per_min;
        public double deadSpaceVentilation_L_per_min;
        public double alveolarArterialGradient_mmHg;
        public double arterialO2Pressure_mmHg;
        public double arterialCO2Pressure_mmHg;
        public double carricoIndex_mmHg;
    }

    [DllImport(DLL)] static extern int BG_GetGasExchangeSnapshot(ref BG_GasExchangeData data);
    [DllImport(DLL)] static extern double BG_GetMeanArterialPressure();
    [DllImport(DLL)] static extern double BG_GetEndTidalCarbonDioxideFraction();

    static string LastNativeError()
    {
        try { return Marshal.PtrToStringAnsi(BG_GetLastError()); }
        catch { return "(unavailable)"; }
    }

    /// <summary>
    /// Engine log in the per-user data folder: next to the data it would sit inside
    /// Assets/StreamingAssets, where the Editor re-imports it on every write (and a
    /// player's StreamingAssets may be read-only). Older wrapper DLLs only have
    /// BG_CreateEngine, which logs next to the data. Set on the main thread before the
    /// engine thread starts (Unity's Application paths are main-thread API).
    /// </summary>
    static string engineLogPath;
    static int CreateEngine(string workingDir)
    {
        if (string.IsNullOrEmpty(engineLogPath))
            return BG_CreateEngine(workingDir);
        try
        {
            return BG_CreateEngineWithLog(workingDir, engineLogPath);
        }
        catch (EntryPointNotFoundException)
        {
            return BG_CreateEngine(workingDir);
        }
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
    float snapLeftLungVolume = float.NaN;
    float snapRightLungVolume = float.NaN;
    float snapMinuteVentilation;
    float snapTranspulmonaryPressure = float.NaN;
    float snapMeanArterialPressure = float.NaN;
    float snapEndTidalCO2 = float.NaN;
    BG_GasExchangeData snapGas = UnavailableGas();
    // False once the loaded DLL turns out to predate BG_GetGasExchangeSnapshot, so an
    // old plugin just leaves those read-outs blank instead of failing every publish.
    static bool gasExchangeExport = true;
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

    // Exercise is a live action too: a bucket load wiped it while the slider still showed
    // it, so it is reapplied after loads like asthma (when not at rest).
    volatile bool pendingExercise;
    volatile float pendingExerciseSeverity;
    volatile bool hasExercise;

    // Set by the engine thread when the native engine can't start (create or first state
    // load failed); Update() then switches to the simulated model on the main thread.
    volatile bool nativeFailed;
    // Every condition the UI set, latest value (main thread only), so a switch to the
    // simulated model mid-start can hand it the patient as it was set up.
    readonly Dictionary<string, float> lastConditions = new Dictionary<string, float>();

    // ARDS is a live action like asthma: persistent state, reapplied after bucket loads.
    volatile bool hasArdsSeverity;
    volatile float ardsSeverity;
    volatile bool ardsDirty;
    static bool ardsExport = true; // false once an older DLL without it is detected

    /// <summary>Lobar pneumonia severity last requested from the UI (0-1).</summary>
    public float PneumoniaSeverity { get; private set; }

    /// <summary>Pneumonia in the patient actually being simulated: overall severity and
    /// the fraction (0-1) of each lung consolidated.</summary>
    public struct PneumoniaExtent
    {
        public float severity, left, right;
        public bool Same(PneumoniaExtent o) =>
            Mathf.Approximately(severity, o.severity) && Mathf.Approximately(left, o.left) && Mathf.Approximately(right, o.right);
    }

    /// <summary>
    /// What the lungs should look like, taken from the patient rather than the slider.
    /// Real engine: the loaded state's own pneumonia settings, so a patient with both
    /// lungs affected shows both, one lung shows one - and during a reload the old
    /// patient is still shown until the new one is in. Simulated model: it has no
    /// per-lung data, so it borrows the split of the real-engine patient for that severity.
    /// </summary>
    public PneumoniaExtent ActivePneumonia
    {
        get
        {
            if (remoteActive)
                return remotePneumonia; // the PC's loaded patient

            int b = usingNativeEngine ? loadedBucket : PneumoniaBucketFor(PneumoniaSeverity);
            if (b < 0 || b >= Buckets.Length || Buckets[b].Pneumonia <= 0.0)
                return default;
            SeverityBucket k = Buckets[b];
            return new PneumoniaExtent
            {
                severity = usingNativeEngine ? (float)k.Pneumonia : PneumoniaSeverity,
                left = (float)k.PneumoniaLeft,
                right = (float)k.PneumoniaRight,
            };
        }
    }

    // Slider severity -> which precomputed pneumonia patient (or -1 for none).
    static int PneumoniaBucketFor(float severity01) =>
        severity01 < 0.15f ? -1 : severity01 < 0.55f ? PneumoniaMildBucket : PneumoniaSevereBucket;
    /// <summary>ARDS severity last requested (0-1).</summary>
    public float ArdsSeverity { get; private set; }

    string DataDir => Path.Combine(Application.streamingAssetsPath, dataFolderName);
    string StatesDir => Path.Combine(DataDir, "states");

    // ---- IBioGearsRespiratorySystem ----
    public float RespirationRate => UseSnapshot ? snapRespirationRate : fallback.RespirationRate;
    public float TidalVolume => UseSnapshot ? snapTidalVolume : fallback.TidalVolume;
    public float TotalLungVolume => UseSnapshot ? snapTotalLungVolume : fallback.TotalLungVolume;
    public float FunctionalResidualCapacity => UseSnapshot ? snapFRC : fallback.FunctionalResidualCapacity;
    public float InspiratoryExpiratoryRatio => UseSnapshot ? snapIERatio : fallback.InspiratoryExpiratoryRatio;
    public float BreathingPhase => UseSnapshot ? phase : fallback.BreathingPhase;
    public float ExpansionFactor => UseSnapshot ? expansion : fallback.ExpansionFactor;
    public float OxygenSaturation => UseSnapshot ? snapOxygenSaturation : fallback.OxygenSaturation;
    public float HeartRate => UseSnapshot ? snapHeartRate : fallback.HeartRate;
    public bool IsRunning => UseSnapshot ? engineRunning : fallback.IsRunning;

    // Extra smoking-relevant readouts not on the shared interface (real markers, not
    // just a cosmetic tint): carboxyhemoglobin rises with smoking independently of
    // COPD, and pulse oximetry is the clinically-displayed SpO2 proxy.
    public float PulseOximetry => UseSnapshot ? snapPulseOximetry : OxygenSaturation;
    public float CarbonMonoxideSaturation => UseSnapshot ? snapCarbonMonoxideSaturation : 0f;

    // Further engine outputs for the vitals monitor. The simulated model doesn't
    // compute the left/right split, pressures or CO2, so those are NaN in fallback mode
    // and the monitor shows "--" rather than a made-up number.
    public float LeftLungVolume => UseSnapshot ? snapLeftLungVolume : float.NaN;     // mL
    public float RightLungVolume => UseSnapshot ? snapRightLungVolume : float.NaN;   // mL
    public float MinuteVentilation => UseSnapshot                                    // L/min
        ? snapMinuteVentilation
        : fallback.RespirationRate * fallback.TidalVolume / 1000f;
    public float TranspulmonaryPressure => UseSnapshot ? snapTranspulmonaryPressure : float.NaN; // cmH2O
    public float MeanArterialPressure => UseSnapshot ? snapMeanArterialPressure : float.NaN;     // mmHg
    public float EndTidalCO2Fraction => UseSnapshot ? snapEndTidalCO2 : float.NaN;               // 0-1

    // Gas exchange and lung mechanics (real engine, local or remote; NaN otherwise).
    public float OxygenConsumption => Gas(snapGas.oxygenConsumption_mL_per_min);              // mL/min
    public float CarbonDioxideProduction => Gas(snapGas.carbonDioxideProduction_mL_per_min);  // mL/min
    public float RespiratoryExchangeRatio => Gas(snapGas.respiratoryExchangeRatio);
    public float AirwayResistance => Gas(snapGas.pulmonaryResistance_cmH2O_s_per_L);          // cmH2O.s/L
    public float LungCompliance => Gas(snapGas.pulmonaryCompliance_L_per_cmH2O);              // L/cmH2O
    public float AlveolarVentilation => Gas(snapGas.alveolarVentilation_L_per_min);           // L/min
    public float DeadSpaceVentilation => Gas(snapGas.deadSpaceVentilation_L_per_min);         // L/min
    public float AlveolarArterialGradient => Gas(snapGas.alveolarArterialGradient_mmHg);      // mmHg
    public float ArterialO2Pressure => Gas(snapGas.arterialO2Pressure_mmHg);                  // mmHg
    public float ArterialCO2Pressure => Gas(snapGas.arterialCO2Pressure_mmHg);                // mmHg
    public float CarricoIndex => Gas(snapGas.carricoIndex_mmHg);                              // mmHg

    float Gas(double v) => !UseSnapshot || v < 0.0 ? float.NaN : (float)v;

    static BG_GasExchangeData UnavailableGas() => new BG_GasExchangeData
    {
        oxygenConsumption_mL_per_min = -1, carbonDioxideProduction_mL_per_min = -1,
        respiratoryExchangeRatio = -1, pulmonaryResistance_cmH2O_s_per_L = -1,
        pulmonaryCompliance_L_per_cmH2O = -1, alveolarVentilation_L_per_min = -1,
        deadSpaceVentilation_L_per_min = -1, alveolarArterialGradient_mmHg = -1,
        arterialO2Pressure_mmHg = -1, arterialCO2Pressure_mmHg = -1, carricoIndex_mmHg = -1,
    };

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

        if (!windowsTarget || useRemoteEvenOnWindows)
        {
            ActivateFallback(windowsTarget
                ? "useRemoteEvenOnWindows is on: skipping the native engine and acting as a headset (simulated model until a BioGears PC is found)."
                : "Native BioGears only runs on Windows Editor/Standalone (no Android/iOS build of the engine exists). Using the simulated model until a BioGears PC is found on the network.");
            StartRemoteLink();
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
        nativeFailed = false;
        engineLogPath = Path.Combine(Application.persistentDataPath, "biogears_unity.log");
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

    // The DLL loaded but the engine didn't start: without this the lungs froze and every
    // vital read 0, since only a missing DLL / states folder fell back. The simulated
    // model only heard the settings made while it was active, so it gets them all now.
    void FallBackFromNative()
    {
        ActivateFallback("Native BioGears could not start (" + statusMessage + "). Using the simulated model.");
        if (pacingRequested)
        {
            fallback.SetBaseRespirationRate(pacedRate);
            fallback.SetBaseTidalVolume(pacedTidalVolume);
        }
        foreach (KeyValuePair<string, float> c in lastConditions)
            fallback.ApplyCondition(c.Key, c.Value);
    }

    // ---- remote engine (Quest -> PC) ----

    void StartRemoteLink()
    {
        if (!connectToPcServer || remote != null)
            return;

        AcquireMulticastLock();
        try
        {
            remote = new BioGearsLinkClient(pcServerAddress);
            Debug.Log(string.IsNullOrWhiteSpace(pcServerAddress)
                ? "[BioGearsNativeRespiratory] Looking for a BioGears PC on the network..."
                : $"[BioGearsNativeRespiratory] Connecting to the BioGears PC at {pcServerAddress}...");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BioGearsNativeRespiratory] Could not start the network link to a BioGears PC (" + e.Message + "). Staying on the simulated model.");
            remote = null;
        }
    }

    // Android drops incoming broadcast packets unless an app holds a multicast lock, so
    // without this the Quest would never hear the PC's beacon.
    void AcquireMulticastLock()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            // Unity 6's own handle on the activity (works with GameActivity, which this
            // project uses); not ours to dispose.
            AndroidJavaObject activity = UnityEngine.Android.AndroidApplication.currentActivity;
            using (var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
            {
                multicastLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "BioGearsLink");
                multicastLock.Call("setReferenceCounted", false);
                multicastLock.Call("acquire");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BioGearsNativeRespiratory] Could not acquire the Wi-Fi multicast lock (" + e.Message + "); automatic PC discovery may not work - set pcServerAddress instead.");
        }
#endif
    }

    // Copies the PC's newest snapshot into the same fields the native engine thread
    // fills, so everything downstream works unchanged. Falls back to the simulated
    // model if nothing has arrived for 3 seconds.
    void PollRemote()
    {
        remote.Tick();

        bool fresh = remote.TryGetLatest(out BioGearsLinkProtocol.Snapshot s, out double age) && age < 3.0;
        if (fresh && !ReferenceEquals(s, lastRemoteSnapshot))
        {
            lastRemoteSnapshot = s;
            lock (dataLock)
            {
                snapRespirationRate = s[BioGearsLinkProtocol.Field.RespirationRate];
                snapTidalVolume = s[BioGearsLinkProtocol.Field.TidalVolume];
                snapFRC = s[BioGearsLinkProtocol.Field.FunctionalResidualCapacity];
                snapTotalLungVolume = s[BioGearsLinkProtocol.Field.TotalLungVolume];
                snapIERatio = s[BioGearsLinkProtocol.Field.InspiratoryExpiratoryRatio];
                snapHeartRate = s[BioGearsLinkProtocol.Field.HeartRate];
                snapOxygenSaturation = s[BioGearsLinkProtocol.Field.OxygenSaturation];
                snapPulseOximetry = s[BioGearsLinkProtocol.Field.PulseOximetry];
                snapCarbonMonoxideSaturation = s[BioGearsLinkProtocol.Field.CarbonMonoxideSaturation];
                snapLeftLungVolume = s[BioGearsLinkProtocol.Field.LeftLungVolume];
                snapRightLungVolume = s[BioGearsLinkProtocol.Field.RightLungVolume];
                snapMinuteVentilation = s[BioGearsLinkProtocol.Field.MinuteVentilation];
                snapTranspulmonaryPressure = s[BioGearsLinkProtocol.Field.TranspulmonaryPressure];
                snapMeanArterialPressure = s[BioGearsLinkProtocol.Field.MeanArterialPressure];
                snapEndTidalCO2 = s[BioGearsLinkProtocol.Field.EndTidalCO2Fraction];
                snapGas = new BG_GasExchangeData
                {
                    oxygenConsumption_mL_per_min = Unknown(s, BioGearsLinkProtocol.Field.OxygenConsumption),
                    carbonDioxideProduction_mL_per_min = Unknown(s, BioGearsLinkProtocol.Field.CarbonDioxideProduction),
                    respiratoryExchangeRatio = Unknown(s, BioGearsLinkProtocol.Field.RespiratoryExchangeRatio),
                    pulmonaryResistance_cmH2O_s_per_L = Unknown(s, BioGearsLinkProtocol.Field.AirwayResistance),
                    pulmonaryCompliance_L_per_cmH2O = Unknown(s, BioGearsLinkProtocol.Field.LungCompliance),
                    alveolarVentilation_L_per_min = Unknown(s, BioGearsLinkProtocol.Field.AlveolarVentilation),
                    deadSpaceVentilation_L_per_min = Unknown(s, BioGearsLinkProtocol.Field.DeadSpaceVentilation),
                    alveolarArterialGradient_mmHg = Unknown(s, BioGearsLinkProtocol.Field.AlveolarArterialGradient),
                    arterialO2Pressure_mmHg = Unknown(s, BioGearsLinkProtocol.Field.ArterialO2Pressure),
                    arterialCO2Pressure_mmHg = Unknown(s, BioGearsLinkProtocol.Field.ArterialCO2Pressure),
                    carricoIndex_mmHg = Unknown(s, BioGearsLinkProtocol.Field.CarricoIndex),
                };
            }
            engineRunning = s.EngineRunning;
            remotePneumonia = new PneumoniaExtent
            {
                severity = Zero(s[BioGearsLinkProtocol.Field.PneumoniaSeverity]),
                left = Zero(s[BioGearsLinkProtocol.Field.PneumoniaLeft]),
                right = Zero(s[BioGearsLinkProtocol.Field.PneumoniaRight]),
            };
            statusMessage = $"Running on PC '{remote.ServerName}': {s.Status}";
        }

        if (fresh != remoteActive)
        {
            remoteActive = fresh;
            if (fresh)
            {
                Debug.Log($"[BioGearsNativeRespiratory] Connected to BioGears on PC '{remote.ServerName}' - using its real engine.");
            }
            else
            {
                statusMessage = "Lost the BioGears PC - simulated model until it is back.";
                Debug.LogWarning("[BioGearsNativeRespiratory] " + statusMessage);
            }
        }
    }

    // NaN (not computed) -> the -1 sentinel the gas-exchange getters already treat as unavailable.
    static double Unknown(BioGearsLinkProtocol.Snapshot s, BioGearsLinkProtocol.Field f)
    {
        float v = s[f];
        return float.IsNaN(v) ? -1.0 : v;
    }

    static float Zero(float v) => float.IsNaN(v) ? 0f : v;

    void Update()
    {
        if (nativeFailed && usingNativeEngine)
            FallBackFromNative();

        if (remote != null)
            PollRemote();

        if (!UseSnapshot)
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
        string name = conditionName.ToLowerInvariant();

        // Remembered in both modes so the lung visuals can follow them.
        if (name == "pneumonia") PneumoniaSeverity = severity;
        if (name == "ards" || name == "respiratory_distress") ArdsSeverity = severity;
        // Under the simulated model's names ("smoking" is COPD there).
        lastConditions[name == "smoking" ? "copd" : name == "respiratory_distress" ? "ards" : name] = severity;

        // Headset: the PC's engine gets it too; the local simulated model below keeps
        // the same state so a dropped link changes nothing the user set.
        remote?.SetDesired("cond:" + name, severity);

        if (!usingNativeEngine)
        {
            fallback.ApplyCondition(conditionName, severity);
            return;
        }

        switch (name)
        {
            case "copd":
            case "smoking":
                RequestCOPDSeverity(severity);
                break;
            case "pneumonia":
                RequestPneumoniaSeverity(severity);
                break;
            case "ards":
            case "respiratory_distress":
                ardsSeverity = severity;
                hasArdsSeverity = true;
                ardsDirty = true;
                break;
            case "asthma":
                asthmaSeverity = severity;
                hasAsthmaSeverity = true;
                asthmaDirty = true;
                break;
            case "exercise":
                pendingExerciseSeverity = severity;
                hasExercise = true;
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
        remote?.SetDesired("spo2", Mathf.Clamp01(oxygenSaturation01));
        if (!usingNativeEngine) return;
        spo2Override = Mathf.Clamp01(oxygenSaturation01);
        hasSpO2Override = true;
        spo2Dirty = true;
    }

    /// <summary>
    /// Sets the breathing rate (breaths/min). On the real engine the patient is paced:
    /// every breath is a BioGears conscious-respiration inhale/exhale at this rate and
    /// the tidal volume below, so the lungs really breathe that way and gas exchange
    /// follows (see BG_PacedBreath). This overrides BioGears' own breathing control, so
    /// conditions no longer change the rate by themselves - the presets set a fitting
    /// rate for each scenario instead. On the simulated model it sets the baseline.
    /// </summary>
    public void SetRespirationRate(float breathsPerMinute)
    {
        if (fallback != null)
            fallback.SetBaseRespirationRate(breathsPerMinute);
        remote?.SetDesired("rr", breathsPerMinute);

        pacedRate = Mathf.Clamp(breathsPerMinute, 4f, 60f);
        pacingRequested = true;
    }

    /// <summary>
    /// Sets the depth of each breath (mL). Real engine: the paced tidal volume (see
    /// SetRespirationRate). Simulated model: the baseline tidal volume.
    /// </summary>
    public void SetTidalVolume(float milliliters)
    {
        if (fallback != null)
            fallback.SetBaseTidalVolume(milliliters);
        remote?.SetDesired("tv", milliliters);

        pacedTidalVolume = Mathf.Clamp(milliliters, 100f, 3000f);
        pacingRequested = true;
    }

    // Paced breathing on the real engine. BioGears' SEOverride respiration-rate and
    // tidal-volume fields only replace the REPORTED values after the physiology runs,
    // so they would make the monitor lie; pacing drives the breathing circuit itself.
    volatile float pacedRate = 14f;
    volatile float pacedTidalVolume = 500f;
    volatile bool pacingRequested;
    static bool pacedExport = true; // false once an older DLL without BG_PacedBreath is detected

    // BioGears' conscious-breathing model reaches its target volume only roughly (in
    // testing 600 mL gave ~640, 300 gave ~490, 900 gave ~780), so the volume actually
    // requested is corrected breath by breath from what BioGears reports it delivered.
    // Engine thread only.
    float commandTidalVolume = 500f;
    float commandTarget = -1f;

    // Inhale takes a third of each breath (I:E 1:2, the normal resting ratio).
    void PaceNextBreath()
    {
        if (!pacingRequested || !pacedExport)
            return;

        float target = pacedTidalVolume;
        if (!Mathf.Approximately(target, commandTarget))
        {
            // New slider value: start from it and let the correction refine it.
            commandTarget = target;
            commandTidalVolume = target;
        }

        float cycle = 60f / pacedRate;
        try
        {
            // Queues only when the previous breath has finished; otherwise a no-op.
            int result = BG_PacedBreath(commandTidalVolume, cycle / 3.0, cycle * 2.0 / 3.0);
            if (result == 1)
            {
                // A new breath has started, so the snapshot's tidal volume is the breath
                // that just finished. Nudge the next command toward the target - gently,
                // so one odd breath can't swing it.
                float delivered;
                lock (dataLock) delivered = snapTidalVolume;
                if (delivered > 50f)
                {
                    float step = Mathf.Clamp(target / delivered, 0.85f, 1.18f);
                    commandTidalVolume = Mathf.Clamp(commandTidalVolume * step, target * 0.4f, target * 2.5f);
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
            pacedExport = false;
            Debug.LogWarning("[BioGearsNativeRespiratory] The loaded biogears_unity.dll has no BG_PacedBreath - the respiratory rate / tidal volume sliders won't drive the real engine until the rebuilt DLL is installed (Unity closed).");
        }
    }

    public void ClearMeasuredOxygenSaturation()
    {
        remote?.SetDesired("spo2", float.NaN); // NaN = clear
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
        for (int i = 0; i < CopdBucketCount; i++)
        {
            // COPD buckets are indexed 0..N-1 spread evenly across 0-1 severity.
            float bucketPos = (float)i / (CopdBucketCount - 1);
            float d = Mathf.Abs(bucketPos - severity01);
            if (d < best) { best = d; nearest = i; }
        }
        copdBucket = nearest;
        UpdateTargetBucket();
    }

    /// <summary>
    /// Lobar pneumonia severity (0-1) -> none / mild / severe precomputed state. While
    /// pneumonia is set it replaces the COPD baseline; clearing it returns to the COPD
    /// bucket the smoking slider last asked for.
    /// </summary>
    public void RequestPneumoniaSeverity(float severity01)
    {
        severity01 = Mathf.Clamp01(severity01);
        pneumoniaBucket = PneumoniaBucketFor(severity01);
        UpdateTargetBucket();
    }

    void UpdateTargetBucket() => targetBucket = pneumoniaBucket >= 0 ? pneumoniaBucket : copdBucket;

    void EngineThreadMain()
    {
        string workingDir = DataDir;

        if (CreateEngine(workingDir) == 0)
        {
            statusMessage = "BG_CreateEngine failed: " + LastNativeError();
            Debug.LogError("[BioGearsNativeRespiratory] " + statusMessage);
            nativeFailed = true;
            return;
        }

        if (!LoadBucket(targetBucket))
        {
            statusMessage = "Initial LoadState failed, engine thread exiting: " + LastNativeError();
            Debug.LogError("[BioGearsNativeRespiratory] " + statusMessage);
            BG_DestroyEngine();
            nativeFailed = true;
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
            if (ardsDirty)
            {
                ardsDirty = false;
                if (hasArdsSeverity && ardsExport)
                {
                    try { BG_ApplyAcuteRespiratoryDistress(ardsSeverity); }
                    catch (EntryPointNotFoundException)
                    {
                        ardsExport = false;
                        Debug.LogWarning("[BioGearsNativeRespiratory] The loaded biogears_unity.dll has no BG_ApplyAcuteRespiratoryDistress - ARDS only affects the simulated model until the rebuilt DLL is installed (Unity closed).");
                    }
                }
            }

            double now = stopwatch.Elapsed.TotalSeconds;
            double dt = now - lastElapsed;
            lastElapsed = now;
            dt = Math.Min(dt, 0.5); // guard against long stalls (breakpoints, GC, etc.)

            if (engineRunning)
                PaceNextBreath();

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
        if (hasArdsSeverity) ardsDirty = true;
        if (hasExercise && pendingExerciseSeverity > 0f) pendingExercise = true; // a fresh state is at rest
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
        bool ok;
        if (bucket.Pneumonia > 0.0)
        {
            try { ok = BG_InitializeEngineWithPneumonia(patientFile, bucket.Pneumonia, bucket.PneumoniaLeft, bucket.PneumoniaRight) != 0; }
            catch (EntryPointNotFoundException) { ok = false; } // older DLL: keep the last good state
        }
        else
        {
            ok = BG_InitializeEngineWithCOPD(patientFile, bucket.Bronchitis, bucket.Emphysema) != 0;
        }

        if (ok)
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

        // Not part of the snapshot struct; separate getters, called here on the engine
        // thread so they never block the main thread. They return -1 when unavailable.
        double map = BG_GetMeanArterialPressure();
        double etco2 = BG_GetEndTidalCarbonDioxideFraction();

        BG_GasExchangeData gas = UnavailableGas();
        if (gasExchangeExport)
        {
            try
            {
                if (BG_GetGasExchangeSnapshot(ref gas) == 0)
                    gas = UnavailableGas();
            }
            catch (EntryPointNotFoundException)
            {
                gasExchangeExport = false;
                gas = UnavailableGas();
                Debug.LogWarning("[BioGearsNativeRespiratory] The loaded biogears_unity.dll predates BG_GetGasExchangeSnapshot - gas exchange read-outs will show '--'. Replace Assets/Plugins/x86_64/biogears_unity.dll with the rebuilt one (Unity closed).");
            }
        }

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
            // The snapshot reports a missing value as 0, and a real lung is never empty.
            snapLeftLungVolume = data.leftLungVolume_mL > 0.0 ? (float)data.leftLungVolume_mL : float.NaN;
            snapRightLungVolume = data.rightLungVolume_mL > 0.0 ? (float)data.rightLungVolume_mL : float.NaN;
            snapMinuteVentilation = (float)(data.totalPulmonaryVentilation_mL_per_min / 1000.0);
            // Can legitimately be zero or negative mid-breath, so no "unavailable" check.
            snapTranspulmonaryPressure = (float)data.transpulmonaryPressure_cmH2O;
            snapMeanArterialPressure = OrNaN(map);
            snapEndTidalCO2 = OrNaN(etco2);
            snapGas = gas;
        }
    }

    // The single-value getters report "not available" as -1.
    static float OrNaN(double v) => v < 0.0 ? float.NaN : (float)v;

    void OnDisable()
    {
        wantStop = true;
    }

    void OnDestroy()
    {
        wantStop = true;
        if (engineThread != null && engineThread.IsAlive)
            engineThread.Join(2000);

        remote?.Dispose();
        remote = null;
        remoteActive = false;
        if (multicastLock != null)
        {
            try { multicastLock.Call("release"); } catch { }
            multicastLock.Dispose();
            multicastLock = null;
        }
    }
}
