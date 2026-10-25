using UnityEngine;
using UnityEngine.UI;
using TMPro;


public class VRUIController : MonoBehaviour
{
    // Reference to your BioGears system
    public BioGearsNativeRespiratory bio;
    public LungMeshDeformer lungVisual;

    // Sliders from UI
    public Slider asthmaSlider;
    [Tooltip("Read-out for the asthma slider. Without this the panel kept showing the label's authored text while the severity was actually changing.")]
    public TextMeshProUGUI asthmaText;
    public Slider exerciseSlider;
    [Tooltip("Read-out for the exercise slider. Without it the label kept its authored '0%' while the value changed.")]
    public TextMeshProUGUI exerciseText;
    public Slider smokingYearsSlider;
    public TextMeshProUGUI smokingYearsText;
    public float smokingYearsSliderMax = 40f;

    [Header("Pneumonia and ARDS")]
    [Tooltip("Lobar pneumonia severity 0-1. On the real engine this switches to a precomputed mild/severe patient, which replaces the smoking (COPD) baseline while set.")]
    public Slider pneumoniaSlider;
    public TextMeshProUGUI pneumoniaText;
    [Tooltip("ARDS severity 0-1, applied live.")]
    public Slider ardsSlider;
    public TextMeshProUGUI ardsText;

    [Header("Smoking exposure")]
    [Tooltip("Cigarettes per day. Years alone is not enough - 40/day for 10 years is four times the exposure of 10/day for 10 years.")]
    public Slider cigarettesPerDaySlider;
    public TextMeshProUGUI cigarettesPerDayText;
    public float cigarettesPerDayMax = 40f;

    [Tooltip("Read-out of pack-years and the resulting illustrative damage stage.")]
    public TextMeshProUGUI packYearsText;
    public TextMeshProUGUI damageStageText;

    [Header("Respiratory mechanics - these two directly drive the lung animation")]
    [Tooltip("Breaths per minute -> how often the lung cycles.")]
    public Slider respiratoryRateSlider;
    public TextMeshProUGUI respiratoryRateText;
    public float respiratoryRateMin = 6f;
    public float respiratoryRateMax = 30f;

    [Tooltip("Millilitres per breath -> how far the lung inflates on each cycle.")]
    public Slider tidalVolumeSlider;
    public TextMeshProUGUI tidalVolumeText;
    public float tidalVolumeMin = 200f;
    public float tidalVolumeMax = 1000f;

    [Tooltip("Optional read-out of minute ventilation (rate x tidal volume). Derived, not an input.")]
    public TextMeshProUGUI minuteVentilationText;

    [Header("Measured SpO2 (from a real pulse oximeter reading, not derivable from a CT scan)")]
    [Tooltip("Optional. If assigned, drives BioGearsNativeRespiratory.SetMeasuredOxygenSaturation directly instead of letting the engine compute it.")]
    public Slider spo2Slider;
    public TextMeshProUGUI spo2Text;
    [Tooltip("Slider range is percent (e.g. 70-100), converted to a 0-1 fraction for BioGears.")]
    public float spo2SliderMin = 70f;
    public float spo2SliderMax = 100f;

    [Header("Disease-region highlight")]
    [Tooltip("Toggles the pulsing glow over affected regions on the lung.")]
    public Button highlightButton;
    public TextMeshProUGUI highlightButtonText;
    public Color highlightOffColor = new Color(0.06f, 0.10f, 0.13f, 1f);
    public Color highlightOnColor = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color highlightOffLabel = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color highlightOnLabel = new Color(0.02f, 0.03f, 0.04f, 1f);

    [Header("Airflow")]
    [Tooltip("Toggles the airflow particles. Shares the on/off colours above.")]
    public Button airflowButton;
    public TextMeshProUGUI airflowButtonText;
    public AirflowVisualizer airflow;

    void Start()
    {
        if (bio == null || asthmaSlider == null || exerciseSlider == null)
        {
            Debug.LogError("VRUIController is missing required references.");
            enabled = false;
            return;
        }

        // Before any listener runs: several of them push values to the lung visual.
        if (lungVisual == null)
            lungVisual = FindFirstObjectByType<LungMeshDeformer>();

        // Attach listeners (when slider moves → these functions run).
        // The initial push matters: the scene had asthma/COPD/exercise severities
        // serialized near 1.0 from earlier testing, and without sending the slider's
        // own starting value the patient stayed pinned at those extremes no matter
        // where the sliders appeared to sit.
        asthmaSlider.minValue = 0f;
        asthmaSlider.maxValue = 1f;
        asthmaSlider.value = 0f;
        asthmaSlider.onValueChanged.AddListener(OnAsthmaChanged);
        OnAsthmaChanged(asthmaSlider.value);

        exerciseSlider.minValue = 0f;
        exerciseSlider.maxValue = 1f;
        exerciseSlider.value = 0f;
        exerciseSlider.onValueChanged.AddListener(OnExerciseChanged);
        OnExerciseChanged(exerciseSlider.value);

        if (smokingYearsSlider != null)
        {
            smokingYearsSlider.minValue = 0f;
            smokingYearsSlider.maxValue = Mathf.Max(1f, smokingYearsSliderMax);
            smokingYearsSlider.value = 0f;
            smokingYearsSlider.onValueChanged.AddListener(OnSmokingYearsChanged);
            OnSmokingYearsChanged(smokingYearsSlider.value);
        }

        if (respiratoryRateSlider != null)
        {
            respiratoryRateSlider.minValue = respiratoryRateMin;
            respiratoryRateSlider.maxValue = respiratoryRateMax;
            respiratoryRateSlider.value = 14f;
            respiratoryRateSlider.onValueChanged.AddListener(OnRespiratoryRateChanged);
            OnRespiratoryRateChanged(respiratoryRateSlider.value);
        }

        if (tidalVolumeSlider != null)
        {
            tidalVolumeSlider.minValue = tidalVolumeMin;
            tidalVolumeSlider.maxValue = tidalVolumeMax;
            tidalVolumeSlider.value = 500f;
            tidalVolumeSlider.onValueChanged.AddListener(OnTidalVolumeChanged);
            OnTidalVolumeChanged(tidalVolumeSlider.value);
        }

        if (cigarettesPerDaySlider != null)
        {
            cigarettesPerDaySlider.minValue = 0f;
            cigarettesPerDaySlider.maxValue = cigarettesPerDayMax;
            cigarettesPerDaySlider.value = 20f;
            cigarettesPerDaySlider.onValueChanged.AddListener(OnCigarettesPerDayChanged);
            OnCigarettesPerDayChanged(cigarettesPerDaySlider.value);
        }

        if (spo2Slider != null)
        {
            spo2Slider.minValue = spo2SliderMin;
            spo2Slider.maxValue = Mathf.Max(spo2SliderMin + 1f, spo2SliderMax);
            spo2Slider.value = spo2Slider.maxValue;
            spo2Slider.onValueChanged.AddListener(OnSpO2Changed);
            OnSpO2Changed(spo2Slider.value);
        }

        if (pneumoniaSlider != null)
        {
            pneumoniaSlider.minValue = 0f;
            pneumoniaSlider.maxValue = 1f;
            pneumoniaSlider.value = 0f;
            pneumoniaSlider.onValueChanged.AddListener(OnPneumoniaChanged);
            OnPneumoniaChanged(pneumoniaSlider.value);
        }

        if (ardsSlider != null)
        {
            ardsSlider.minValue = 0f;
            ardsSlider.maxValue = 1f;
            ardsSlider.value = 0f;
            ardsSlider.onValueChanged.AddListener(OnArdsChanged);
            OnArdsChanged(ardsSlider.value);
        }

        if (highlightButton != null)
        {
            highlightButton.onClick.AddListener(ToggleHighlight);
            RefreshHighlightButton();
        }

        if (airflow == null)
            airflow = FindFirstObjectByType<AirflowVisualizer>();
        if (airflowButton != null)
        {
            airflowButton.onClick.AddListener(ToggleAirflow);
            RefreshToggle(airflowButton, airflowButtonText, "Show airflow", airflow != null && airflow.showAirflow);
        }
    }

    void ToggleAirflow()
    {
        if (airflow == null)
            return;

        airflow.SetShowAirflow(!airflow.showAirflow);
        RefreshToggle(airflowButton, airflowButtonText, "Show airflow", airflow.showAirflow);
    }

    void RefreshToggle(Button button, TextMeshProUGUI label, string name, bool on)
    {
        if (button != null && button.targetGraphic != null)
            button.targetGraphic.color = on ? highlightOnColor : highlightOffColor;

        if (label != null)
        {
            label.text = $"{name}: {(on ? "ON" : "OFF")}";
            label.color = on ? highlightOnLabel : highlightOffLabel;
        }
    }

    void ToggleHighlight()
    {
        if (lungVisual == null)
            return;

        lungVisual.SetHighlightAffectedRegions(!lungVisual.highlightAffectedRegions);
        RefreshHighlightButton();
    }

    void RefreshHighlightButton() =>
        RefreshToggle(highlightButton, highlightButtonText, "Highlight regions",
                      lungVisual != null && lungVisual.highlightAffectedRegions);

    // Called when Asthma slider changes
    void OnAsthmaChanged(float value)
    {
        bio.ApplyCondition("asthma", value);

        // Physiology above, appearance here: inflamed, mucus-glossy airways.
        if (lungVisual != null)
            lungVisual.SetAsthmaSeverity(value);

        if (asthmaText != null)
            asthmaText.text = $"Asthma severity: {value * 100f:F0}%";

        Debug.Log("Asthma: " + value);
    }

    void OnPneumoniaChanged(float value)
    {
        // Physiology only. The lung's appearance follows the patient BioGears ends up
        // simulating (which lungs it affects), not this slider - see LungMeshDeformer.
        bio.ApplyCondition("pneumonia", value);

        // The real engine only has two pneumonia patients (mild / severe), so name the
        // one in use rather than imply every percent is distinct.
        if (pneumoniaText != null)
        {
            string level = value < 0.15f ? "none" : value < 0.55f ? "mild" : "severe";
            pneumoniaText.text = $"Pneumonia: {value * 100f:F0}% ({level})";
        }
    }

    void OnArdsChanged(float value)
    {
        bio.ApplyCondition("ards", value);
        if (lungVisual != null)
            lungVisual.SetArdsSeverity(value);

        if (ardsText != null)
            ardsText.text = $"ARDS severity: {value * 100f:F0}%";
    }

    // Called when Exercise slider changes
    void OnExerciseChanged(float value)
    {
        bio.ApplyCondition("exercise", value);

        if (exerciseText != null)
            exerciseText.text = $"Exercise intensity: {value * 100f:F0}%";

        Debug.Log("Exercise: " + value);
    }

    // Called when Smoking Years slider changes
    void OnSmokingYearsChanged(float years)
    {
        if (lungVisual != null)
            lungVisual.SetSmokingExposure(years, lungVisual.cigarettesPerDay);

        // Drive real physiology (COPD severity in the respiratory sim), not just the
        // cosmetic lung stain. Reuses the same curve as the visual staining so the
        // patient's breathing performance and lung appearance stay coherent - worse
        // looking lungs correspond to worse measured lung function.
        if (bio != null)
        {
            float t = lungVisual != null
                ? lungVisual.smokingColorCurve.Evaluate(Mathf.Clamp01(years / Mathf.Max(0.01f, lungVisual.maxSmokingYears)))
                : Mathf.Clamp01(years / smokingYearsSliderMax);
            bio.ApplyCondition("copd", t);
        }

        if (smokingYearsText != null)
            smokingYearsText.text = $"Years Smoked: {years:F1}";

        UpdateExposureReadout();

        Debug.Log("Smoking years: " + years);
    }

    // Breaths per minute -> breathing frequency in the visualisation.
    void OnRespiratoryRateChanged(float breathsPerMinute)
    {
        if (bio != null)
            bio.SetRespirationRate(breathsPerMinute);

        if (respiratoryRateText != null)
            respiratoryRateText.text = $"Respiratory Rate: {breathsPerMinute:F0} /min";

        UpdateMinuteVentilation();
    }

    // Millilitres per breath -> inflation amplitude in the visualisation.
    void OnTidalVolumeChanged(float milliliters)
    {
        if (bio != null)
            bio.SetTidalVolume(milliliters);

        if (tidalVolumeText != null)
            tidalVolumeText.text = $"Tidal Volume: {milliliters:F0} mL";

        UpdateMinuteVentilation();
    }

    // Derived value (rate x tidal volume), shown as a read-out rather than an input
    // because it is not independently settable in the respiratory model.
    void UpdateMinuteVentilation()
    {
        if (minuteVentilationText == null)
            return;

        float rate = respiratoryRateSlider != null ? respiratoryRateSlider.value : 14f;
        float tidal = tidalVolumeSlider != null ? tidalVolumeSlider.value : 500f;
        minuteVentilationText.text = $"Minute Ventilation: {(rate * tidal) / 1000f:F1} L/min";
    }

    void OnCigarettesPerDayChanged(float cigs)
    {
        if (lungVisual != null)
            lungVisual.cigarettesPerDay = cigs;

        if (cigarettesPerDayText != null)
            cigarettesPerDayText.text = $"Cigarettes/Day: {cigs:F0}";

        // Re-push exposure so the lung updates from the new pack-years immediately.
        if (lungVisual != null)
            lungVisual.SetSmokingYears(lungVisual.smokingYears);

        UpdateExposureReadout();
    }

    /// <summary>
    /// Pack-years and a descriptive stage. Deliberately worded as cumulative exposure
    /// rather than a percentage of lung destroyed - there is no valid "x% damaged per
    /// year" figure, and presenting one would be misleading.
    /// </summary>
    void UpdateExposureReadout()
    {
        if (lungVisual == null)
            return;

        if (packYearsText != null)
            packYearsText.text = $"Pack-Years: {lungVisual.PackYears:F1}";

        if (damageStageText != null)
            damageStageText.text = $"Stage: {lungVisual.DamageStageLabel}";
    }

    // Called when the measured SpO2 slider changes - injects a real reading from the
    // patient (e.g. a pulse oximeter) rather than letting BioGears compute it, since
    // this is exactly the kind of value a CT scan can't provide.
    void OnSpO2Changed(float percent)
    {
        if (bio != null)
            bio.SetMeasuredOxygenSaturation(percent / 100f);

        if (spo2Text != null)
            spo2Text.text = $"SpO2: {percent:F0}%";
    }
}