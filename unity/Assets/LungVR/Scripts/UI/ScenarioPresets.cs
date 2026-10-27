using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// One-tap patient scenarios for the console. A preset only moves the existing
/// sliders - it never talks to BioGears directly - so every value still flows through
/// VRUIController's listeners exactly as if the user had dragged each slider by hand.
/// That keeps one code path for physiology, the lung stain and the read-outs.
///
/// Runs after VRUIController (execution order) so the default scenario is applied on
/// top of the sliders' start-up values rather than being overwritten by them.
/// </summary>
[DefaultExecutionOrder(100)]
public class ScenarioPresets : MonoBehaviour
{
    [Serializable]
    public class Preset
    {
        [Tooltip("Short name shown on the button.")]
        public string buttonLabel;
        [Tooltip("Longer description shown in the scenario read-out.")]
        public string description;

        public float respiratoryRate;    // breaths/min
        public float tidalVolume;        // mL
        public float spo2Percent;        // measured SpO2, %
        [Range(0, 1)] public float asthma;
        public float smokingYears;
        public float cigarettesPerDay;
        [Range(0, 1)] public float exercise;
        [Range(0, 1)] public float pneumonia;
        [Range(0, 1)] public float ards;
    }

    public VRUIController ui;

    [Tooltip("One button per preset, in the same order as the presets list.")]
    public Button[] presetButtons;

    [Tooltip("Shows which scenario is active, or 'Custom' once a slider is moved by hand.")]
    public TextMeshProUGUI scenarioText;

    [Tooltip("Preset applied when the scene starts. -1 leaves the sliders at their start-up values.")]
    public int defaultPreset = 0;

    public Color idleButtonColor = new Color(0.06f, 0.10f, 0.13f, 1f);
    public Color activeButtonColor = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color idleLabelColor = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color activeLabelColor = new Color(0.02f, 0.03f, 0.04f, 1f);

    // Values are illustrative textbook figures for an adult, chosen to sit inside the
    // console's slider ranges (rate 6-30, tidal volume 200-1000, SpO2 70-100).
    public Preset[] presets = DefaultPresets();

    public static Preset[] DefaultPresets() => new[]
    {
        new Preset { buttonLabel = "Healthy", description = "Healthy adult at rest",
                     respiratoryRate = 14, tidalVolume = 500, spo2Percent = 98 },
        new Preset { buttonLabel = "Asthma", description = "Moderate asthma attack",
                     respiratoryRate = 26, tidalVolume = 350, spo2Percent = 91, asthma = 0.7f },
        new Preset { buttonLabel = "Smoker", description = "Smoker, 20 pack-years",
                     respiratoryRate = 18, tidalVolume = 450, spo2Percent = 94,
                     smokingYears = 20, cigarettesPerDay = 20 },
        new Preset { buttonLabel = "COPD", description = "Severe COPD, 45 pack-years",
                     respiratoryRate = 24, tidalVolume = 380, spo2Percent = 88,
                     smokingYears = 30, cigarettesPerDay = 30 },
        new Preset { buttonLabel = "Exercise", description = "Healthy adult, vigorous exercise",
                     respiratoryRate = 30, tidalVolume = 1000, spo2Percent = 97, exercise = 0.7f },
        new Preset { buttonLabel = "Pneumonia", description = "Severe right lower lobe pneumonia",
                     respiratoryRate = 26, tidalVolume = 380, spo2Percent = 90, pneumonia = 0.7f },
        new Preset { buttonLabel = "ARDS", description = "Acute respiratory distress syndrome",
                     respiratoryRate = 30, tidalVolume = 320, spo2Percent = 85, ards = 0.7f },
    };

    int activePreset = -1;
    bool applying;

    void Start()
    {
        if (ui == null)
            ui = FindFirstObjectByType<VRUIController>();
        if (ui == null)
        {
            Debug.LogError("ScenarioPresets: no VRUIController found.");
            enabled = false;
            return;
        }

        if (presetButtons != null)
        {
            for (int i = 0; i < presetButtons.Length; i++)
            {
                if (presetButtons[i] == null) continue;
                int index = i; // capture per button, not the loop variable
                presetButtons[i].onClick.AddListener(() => Apply(index));
            }
        }

        // Any hand-made change after a preset turns the read-out to "Custom".
        foreach (Slider s in Sliders())
            if (s != null)
                s.onValueChanged.AddListener(_ => { if (!applying) SetActive(-1); });

        if (defaultPreset >= 0 && defaultPreset < presets.Length)
            Apply(defaultPreset);
        else
            SetActive(-1);
    }

    public void Apply(int index)
    {
        if (index < 0 || index >= presets.Length)
            return;

        Preset p = presets[index];
        applying = true;
        try
        {
            // Cigarettes/day before years: the years listener reads the lung's current
            // cigarettes/day to compute pack-years, so it must already be updated.
            Set(ui.cigarettesPerDaySlider, p.cigarettesPerDay);
            Set(ui.smokingYearsSlider, p.smokingYears);
            Set(ui.asthmaSlider, p.asthma);
            Set(ui.exerciseSlider, p.exercise);
            Set(ui.pneumoniaSlider, p.pneumonia);
            Set(ui.ardsSlider, p.ards);
            Set(ui.respiratoryRateSlider, p.respiratoryRate);
            Set(ui.tidalVolumeSlider, p.tidalVolume);
            Set(ui.spo2Slider, p.spo2Percent);
        }
        finally
        {
            applying = false;
        }

        SetActive(index);
        Debug.Log($"[ScenarioPresets] Applied '{p.description}'.");
    }

    // Setting Slider.value fires onValueChanged, which runs VRUIController's handler.
    // The slider clamps to its own range, so an out-of-range preset degrades safely.
    static void Set(Slider slider, float value)
    {
        if (slider != null)
            slider.value = value;
    }

    Slider[] Sliders() => new[]
    {
        ui.cigarettesPerDaySlider, ui.smokingYearsSlider, ui.asthmaSlider, ui.exerciseSlider,
        ui.respiratoryRateSlider, ui.tidalVolumeSlider, ui.spo2Slider,
        ui.pneumoniaSlider, ui.ardsSlider,
    };

    void SetActive(int index)
    {
        activePreset = index;

        if (scenarioText != null)
            scenarioText.text = index >= 0 ? $"Scenario: {presets[index].description}" : "Scenario: Custom";

        if (presetButtons == null)
            return;

        for (int i = 0; i < presetButtons.Length; i++)
        {
            Button b = presetButtons[i];
            if (b == null) continue;

            bool active = i == activePreset;
            if (b.targetGraphic != null)
                b.targetGraphic.color = active ? activeButtonColor : idleButtonColor;

            TextMeshProUGUI label = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
                label.color = active ? activeLabelColor : idleLabelColor;
        }
    }
}
