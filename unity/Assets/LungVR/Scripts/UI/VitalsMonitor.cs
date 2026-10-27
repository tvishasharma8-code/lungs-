using UnityEngine;
using TMPro;

/// <summary>
/// Bedside-monitor style panel: big tiles for the key numbers, a live lung-volume
/// waveform plus SpO2 and respiratory rate trends (Overview tab), and the other vitals,
/// gas exchange and lung mechanics (Details tab) - see VitalsMonitorBuilder.
///
/// Samples are taken on fixed timers rather than once per frame, so each graph's x axis
/// is a fixed time window regardless of frame rate.
/// </summary>
public class VitalsMonitor : MonoBehaviour
{
    public BioGearsNativeRespiratory bio;

    [Header("Lung volume waveform (breath by breath)")]
    public TrendGraph volumeGraph;
    public TextMeshProUGUI volumeText;
    [Tooltip("Seconds of history across the graph.")]
    public float volumeWindowSeconds = 12f;

    [Header("Slow trends")]
    public TrendGraph spo2Graph;
    public TextMeshProUGUI spo2Text;
    public TrendGraph rateGraph;
    public TextMeshProUGUI rateText;
    [Tooltip("Seconds of history across the SpO2 and respiratory-rate graphs.")]
    public float trendWindowSeconds = 120f;

    [Header("Overview tiles (big numbers)")]
    public TextMeshProUGUI rateTileText;
    public TextMeshProUGUI spo2TileText;
    public TextMeshProUGUI tidalTileText;
    public TextMeshProUGUI heartTileText;

    [Header("Other vitals")]
    public TextMeshProUGUI leftColumnText;
    public TextMeshProUGUI rightColumnText;
    [Tooltip("Which model the numbers come from: the real BioGears engine or the simulated fallback.")]
    public TextMeshProUGUI sourceText;

    [Header("Gas exchange and lung mechanics")]
    public TextMeshProUGUI gasLeftColumnText;
    public TextMeshProUGUI gasRightColumnText;

    float volumeInterval, trendInterval;
    float volumeTimer, trendTimer, textTimer;

    void Start()
    {
        if (bio == null)
            bio = FindFirstObjectByType<BioGearsNativeRespiratory>();
        if (bio == null)
        {
            Debug.LogError("VitalsMonitor: no BioGearsNativeRespiratory found.");
            enabled = false;
            return;
        }

        volumeInterval = volumeWindowSeconds / Mathf.Max(1, Capacity(volumeGraph) - 1);
        trendInterval = trendWindowSeconds / Mathf.Max(1, Capacity(trendGraphForInterval) - 1);
    }

    TrendGraph trendGraphForInterval => spo2Graph != null ? spo2Graph : rateGraph;

    static int Capacity(TrendGraph g) => g != null ? g.capacity : 240;

    void Update()
    {
        if (!bio.IsRunning)
            return;

        float dt = Time.deltaTime;

        // Volume tracks the visible breathing: FRC plus the fraction of the tidal
        // volume the lung is currently inflated by. On the native engine the engine's
        // own lung-volume snapshot only updates a few times a second, so it would step
        // out of sync with the animation; this stays locked to it in both modes.
        volumeTimer += dt;
        while (volumeTimer >= volumeInterval)
        {
            volumeTimer -= volumeInterval;
            if (volumeGraph != null)
                volumeGraph.AddSample(CurrentLungVolume());
        }

        trendTimer += dt;
        while (trendTimer >= trendInterval)
        {
            trendTimer -= trendInterval;
            if (spo2Graph != null) spo2Graph.AddSample(bio.OxygenSaturation * 100f);
            if (rateGraph != null) rateGraph.AddSample(bio.RespirationRate);
        }

        // Text doesn't need to change every frame; 5 Hz reads as live and avoids
        // re-meshing the text each frame.
        textTimer += dt;
        if (textTimer >= 0.2f)
        {
            textTimer = 0f;
            UpdateText();
        }
    }

    float CurrentLungVolume() => bio.FunctionalResidualCapacity + bio.TidalVolume * bio.ExpansionFactor;

    void UpdateText()
    {
        if (volumeText != null) volumeText.text = $"{CurrentLungVolume():F0} mL";
        if (spo2Text != null) spo2Text.text = $"{bio.OxygenSaturation * 100f:F1} %";
        if (rateText != null) rateText.text = $"{bio.RespirationRate:F0} /min";
        if (rateTileText != null) rateTileText.text = $"{bio.RespirationRate:F0}<size=45%> /min</size>";
        if (spo2TileText != null) spo2TileText.text = $"{bio.OxygenSaturation * 100f:F0}<size=45%> %</size>";
        if (tidalTileText != null) tidalTileText.text = $"{bio.TidalVolume:F0}<size=45%> mL</size>";
        if (heartTileText != null) heartTileText.text = $"{bio.HeartRate:F0}<size=45%> bpm</size>";

        // Short clinical labels, values aligned on a tab stop so two columns fit the
        // panel at a size that is readable in the headset.
        if (leftColumnText != null)
            leftColumnText.text =
                Row("Heart rate", $"{bio.HeartRate:F0} bpm") +
                Row("MAP", $"{Num(bio.MeanArterialPressure, "F0")} mmHg") +
                Row("Tidal vol.", $"{bio.TidalVolume:F0} mL") +
                Row("Minute vent.", $"{bio.MinuteVentilation:F1} L/min") +
                Row("I:E", IERatio(bio.InspiratoryExpiratoryRatio));

        if (rightColumnText != null)
            rightColumnText.text =
                Row("FRC", $"{bio.FunctionalResidualCapacity:F0} mL") +
                Row("Lung L/R", $"{Num(bio.LeftLungVolume, "F0")}/{Num(bio.RightLungVolume, "F0")} mL") +
                Row("Transpulm.", $"{Num(bio.TranspulmonaryPressure, "F1")} cmH2O") +
                Row("ETCO2", $"{Num(bio.EndTidalCO2Fraction * 100f, "F1")} %") +
                Row("CO-Hb", $"{bio.CarbonMonoxideSaturation * 100f:F1} %");

        // How much oxygen the body uses and how well the lungs get it into the blood,
        // plus how hard the lungs are to ventilate (resistance) and how stretchy
        // (compliance) - the two mechanics asthma and COPD change.
        if (gasLeftColumnText != null)
            gasLeftColumnText.text =
                Row("VO2", $"{Num(bio.OxygenConsumption, "F0")} mL/min") +
                Row("VCO2", $"{Num(bio.CarbonDioxideProduction, "F0")} mL/min") +
                Row("RER", Num(bio.RespiratoryExchangeRatio, "F2")) +
                Row("Alv. vent.", $"{Num(bio.AlveolarVentilation, "F1")} L/min") +
                Row("Dead space", $"{Num(bio.DeadSpaceVentilation, "F1")} L/min");

        if (gasRightColumnText != null)
            gasRightColumnText.text =
                Row("PaO2", $"{Num(bio.ArterialO2Pressure, "F0")} mmHg") +
                Row("PaCO2", $"{Num(bio.ArterialCO2Pressure, "F0")} mmHg") +
                Row("A-a grad.", $"{Num(bio.AlveolarArterialGradient, "F0")} mmHg") +
                Row("Airway res.", $"{Num(bio.AirwayResistance, "F1")} cmH2O/L/s") +
                Row("Compliance", $"{Num(bio.LungCompliance, "F2")} L/cmH2O");

        if (sourceText != null)
            sourceText.text = "Source: " + bio.SourceDescription
                + (bio.usingNativeEngine || bio.usingRemoteEngine ? "" : "  (-- = not modelled)");
    }

    static string Row(string label, string value) => $"{label}<pos=46%>{value}\n";

    // NaN means the active model doesn't compute this value.
    static string Num(float v, string format) => float.IsNaN(v) ? "--" : v.ToString(format);

    // BioGears gives I:E as inspiratory / expiratory time (0.5 = 1:2); clinicians read it as 1:x.
    static string IERatio(float ie) => ie > 0.01f ? $"1 : {1f / ie:F1}" : "--";
}
