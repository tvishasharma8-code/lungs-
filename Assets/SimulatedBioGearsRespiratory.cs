using UnityEngine;

/// <summary>
/// Simulated BioGears respiratory physiology model.
/// Produces realistic respiratory parameters based on published physiological data.
/// Can be replaced with real BioGears engine when DLLs are available.
/// 
/// References:
/// - Normal adult tidal volume: 500 mL
/// - Normal respiratory rate: 12-20 breaths/min
/// - Total lung capacity: ~6000 mL
/// - Functional residual capacity: ~2400 mL
/// - I:E ratio: 1:2 (normal)
/// - SpO2: 95-100%
/// - Heart rate: 60-100 bpm
/// </summary>
public class SimulatedBioGearsRespiratory : MonoBehaviour, IBioGearsRespiratorySystem
{
    [Header("Patient Parameters")]
    [SerializeField] private float patientAge = 30f;
    [SerializeField] private float patientWeightKg = 70f;

    [Header("Baseline Physiology")]
    [SerializeField] private float baseRespirationRate = 14f;   // breaths/min
    [SerializeField] private float baseTidalVolume = 500f;      // mL
    [SerializeField] private float totalLungCapacity = 6000f;   // mL
    [SerializeField] private float baseFRC = 2400f;             // mL
    [SerializeField] private float baseIERatio = 0.5f;          // I:E = 1:2
    [SerializeField] private float baseSpO2 = 0.98f;
    [SerializeField] private float baseHeartRate = 72f;         // bpm

    [Header("Conditions")]
    [SerializeField] [Range(0, 1)] private float asthmaSeverity = 0f;
    [SerializeField] [Range(0, 1)] private float copdSeverity = 0f;
    [SerializeField] [Range(0, 1)] private float respiratoryDistressSeverity = 0f;
    [SerializeField] [Range(0, 1)] private float exerciseIntensity = 0f;

    [Header("Variability")]
    [SerializeField] private bool enableNaturalVariability = true;
    [SerializeField] private float variabilityAmount = 0.05f;

    // Internal state
    private float simulationTime;
    private float currentPhase;
    private float currentExpansion;
    private float breathCycleTime;
    private float currentTidalVolume;
    private float currentRespirationRate;
    private float currentSpO2;
    private float currentHeartRate;
    private float currentFRC;
    private float currentIERatio;
    private float currentTotalLungVolume;
    private bool initialized;

    // Noise for natural variability
    private float breathVariation;
    private float nextBreathVariation;
    private int breathCount;

    // IBioGearsRespiratorySystem implementation
    public float RespirationRate => currentRespirationRate;
    public float TidalVolume => currentTidalVolume;
    public float TotalLungVolume => currentTotalLungVolume;
    public float FunctionalResidualCapacity => currentFRC;
    public float InspiratoryExpiratoryRatio => currentIERatio;
    public float BreathingPhase => currentPhase;
    public float ExpansionFactor => currentExpansion;
    public float OxygenSaturation => currentSpO2;
    public float HeartRate => currentHeartRate;
    public bool IsRunning => initialized;

    void Start()
    {
        Initialize();
    }

    public void Initialize()
    {
        simulationTime = 0f;
        breathCount = 0;
        breathVariation = 0f;
        nextBreathVariation = Random.Range(-variabilityAmount, variabilityAmount);
        UpdatePhysiologyParameters();
        initialized = true;
        Debug.Log("[BioGears Simulated] Respiratory system initialized");
    }

    void Update()
    {
        if (!initialized) return;
        AdvanceTime(Time.deltaTime);
    }

    public void AdvanceTime(float deltaTime)
    {
        simulationTime += deltaTime;
        UpdatePhysiologyParameters();
        UpdateBreathingCycle(deltaTime);
    }

    /// <summary>
    /// Sets the resting respiratory rate (breaths/min). Conditions still modify this
    /// on top, so it acts as the patient's baseline rather than a hard override.
    /// Drives breathing frequency in the visualisation.
    /// </summary>
    public void SetBaseRespirationRate(float breathsPerMinute)
    {
        baseRespirationRate = Mathf.Clamp(breathsPerMinute, 4f, 40f);
    }

    /// <summary>
    /// Sets the resting tidal volume (mL) - the air moved per breath. Drives how far
    /// the lung visibly inflates on each cycle.
    /// </summary>
    public void SetBaseTidalVolume(float milliliters)
    {
        baseTidalVolume = Mathf.Clamp(milliliters, 150f, 1200f);
    }

    public float BaseRespirationRate => baseRespirationRate;
    public float BaseTidalVolume => baseTidalVolume;

    public void ApplyCondition(string conditionName, float severity)
    {
        severity = Mathf.Clamp01(severity);
        switch (conditionName.ToLower())
        {
            case "asthma":
                asthmaSeverity = severity;
                break;
            case "copd":
                copdSeverity = severity;
                break;
            case "respiratory_distress":
            case "ards":
                respiratoryDistressSeverity = severity;
                break;
            case "exercise":
                exerciseIntensity = severity;
                break;
            default:
                Debug.LogWarning($"[BioGears Simulated] Unknown condition: {conditionName}");
                break;
        }
        Debug.Log($"[BioGears Simulated] Applied condition: {conditionName} severity={severity:F2}");
    }

    private void UpdatePhysiologyParameters()
    {
        // Respiration rate affected by conditions
        currentRespirationRate = baseRespirationRate;
        currentRespirationRate += asthmaSeverity * 10f;          // Asthma: increased rate
        currentRespirationRate += copdSeverity * 6f;             // COPD: moderately increased
        currentRespirationRate += respiratoryDistressSeverity * 16f; // ARDS: significantly increased
        currentRespirationRate += exerciseIntensity * 20f;       // Exercise: up to 34 breaths/min

        // Tidal volume
        currentTidalVolume = baseTidalVolume;
        currentTidalVolume *= (1f - asthmaSeverity * 0.3f);      // Asthma: reduced tidal volume
        currentTidalVolume *= (1f - copdSeverity * 0.25f);       // COPD: reduced
        currentTidalVolume *= (1f - respiratoryDistressSeverity * 0.4f); // ARDS: greatly reduced
        currentTidalVolume += exerciseIntensity * 1500f;         // Exercise: up to ~2000 mL

        // Functional residual capacity
        currentFRC = baseFRC;
        currentFRC *= (1f + copdSeverity * 0.4f);               // COPD: hyperinflation
        currentFRC *= (1f - respiratoryDistressSeverity * 0.2f); // ARDS: decreased compliance

        // I:E ratio
        currentIERatio = baseIERatio;
        currentIERatio *= (1f - asthmaSeverity * 0.3f);          // Asthma: prolonged expiration
        currentIERatio *= (1f - copdSeverity * 0.4f);            // COPD: prolonged expiration

        // SpO2
        currentSpO2 = baseSpO2;
        currentSpO2 -= asthmaSeverity * 0.08f;
        currentSpO2 -= copdSeverity * 0.06f;
        currentSpO2 -= respiratoryDistressSeverity * 0.15f;
        currentSpO2 = Mathf.Clamp(currentSpO2, 0.7f, 1f);

        // Heart rate
        currentHeartRate = baseHeartRate;
        currentHeartRate += asthmaSeverity * 20f;
        currentHeartRate += respiratoryDistressSeverity * 30f;
        currentHeartRate += exerciseIntensity * 80f;             // Exercise: up to 150+ bpm

        // Total lung volume (FRC + current tidal volume contribution)
        currentTotalLungVolume = currentFRC + currentTidalVolume * currentExpansion;

        // Apply natural variability
        if (enableNaturalVariability)
        {
            float variation = Mathf.Lerp(breathVariation, nextBreathVariation, currentPhase);
            currentRespirationRate *= (1f + variation * 0.5f);
            currentTidalVolume *= (1f + variation);
        }

        breathCycleTime = 60f / currentRespirationRate;
    }

    private void UpdateBreathingCycle(float deltaTime)
    {
        // Calculate phase within breath cycle
        float prevPhase = currentPhase;
        currentPhase += deltaTime / breathCycleTime;

        // New breath cycle
        if (currentPhase >= 1f)
        {
            currentPhase -= 1f;
            breathCount++;
            breathVariation = nextBreathVariation;
            nextBreathVariation = Random.Range(-variabilityAmount, variabilityAmount);
        }

        // Calculate expansion factor using asymmetric waveform (I:E ratio)
        // Inspiratory phase is faster, expiratory phase is slower
        float ieRatio = currentIERatio;
        float inspiratoryFraction = ieRatio / (1f + ieRatio); // e.g., 0.33 for I:E = 1:2

        if (currentPhase < inspiratoryFraction)
        {
            // Inspiration phase - use smoothstep for natural curve
            float t = currentPhase / inspiratoryFraction;
            currentExpansion = SmoothStep(t);
        }
        else
        {
            // Expiration phase - slower, passive recoil curve
            float t = (currentPhase - inspiratoryFraction) / (1f - inspiratoryFraction);
            currentExpansion = 1f - SmoothStep(t);
        }
    }

    /// <summary>
    /// Attempt to use and replicate the respiratory muscle pressure curve
    /// Pmus(t) similar to BioGears' respiratory driver model.
    /// This creates a more physiologically accurate breathing pattern.
    /// </summary>
    private float SmoothStep(float t)
    {
        // Hermite interpolation for smooth acceleration/deceleration
        return t * t * (3f - 2f * t);
    }


    // Debug visualization in Unity Editor
    void OnGUI()
    {
        if (!initialized) return;

        GUILayout.BeginArea(new Rect(10, 10, 300, 250));
        GUILayout.Box("BioGears Respiratory Monitor");
        GUILayout.Label($"Resp. Rate: {currentRespirationRate:F1} breaths/min");
        GUILayout.Label($"Tidal Volume: {currentTidalVolume:F0} mL");
        GUILayout.Label($"Total Lung Vol: {currentTotalLungVolume:F0} mL");
        GUILayout.Label($"FRC: {currentFRC:F0} mL");
        GUILayout.Label($"I:E Ratio: 1:{(1f / currentIERatio):F1}");
        GUILayout.Label($"SpO2: {(currentSpO2 * 100f):F1}%");
        GUILayout.Label($"Heart Rate: {currentHeartRate:F0} bpm");
        GUILayout.Label($"Expansion: {(currentExpansion * 100f):F0}%");
        GUILayout.Label($"Breath #: {breathCount}");
        GUILayout.EndArea();
    }
}
