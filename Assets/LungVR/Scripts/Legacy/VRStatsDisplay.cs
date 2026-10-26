using UnityEngine;
using TMPro;

public class VRStatsDisplay : MonoBehaviour
{
    public TextMeshProUGUI statsText;
    public BioGearsNativeRespiratory sim;

    void Update()
    {
        statsText.text =
            "Tidal Volume: " + sim.TidalVolume.ToString("F0") + " mL" +
            "\nSpO2: " + (sim.OxygenSaturation * 100f).ToString("F1") + "%" +
            "\nHeart Rate: " + sim.HeartRate.ToString("F0") + " bpm" +
            "\nExpansion: " + (sim.ExpansionFactor * 100f).ToString("F0") + "%" +
            "\nCarboxyhemoglobin: " + (sim.CarbonMonoxideSaturation * 100f).ToString("F1") + "%" +
            (sim.usingNativeEngine ? "\n[BioGears]" : "\n[Simulated]");
    }
}