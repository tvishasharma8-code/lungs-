using UnityEngine;
using TMPro;

/// <summary>
/// The console header's second line: whose lungs are on show and how many tumors were
/// found - visible on every tab, so the user always knows what they are looking at.
/// (The first line, the scenario, is ScenarioPresets' read-out.)
/// </summary>
public class ConsoleStatus : MonoBehaviour
{
    public TextMeshProUGUI text;
    public PatientLungLoader loader;
    public TumorMarkers tumors;

    float nextUpdate;

    void Start()
    {
        if (loader == null) loader = FindFirstObjectByType<PatientLungLoader>();
        if (tumors == null) tumors = FindFirstObjectByType<TumorMarkers>();
    }

    void Update()
    {
        if (text == null || Time.unscaledTime < nextUpdate)
            return;
        nextUpdate = Time.unscaledTime + 0.5f;

        bool patient = loader != null && !string.IsNullOrEmpty(loader.loadedPatientId);
        string lungs = patient ? loader.loadedLabel : "Built-in lung model";
        int n = tumors != null ? tumors.Count : 0;
        text.text = $"Lungs: {lungs}" + (n > 0 ? $"   |   {n} tumor{(n == 1 ? "" : "s")} found" : "");
    }
}
