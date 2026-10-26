using UnityEngine;
using UnityEngine.UI;

public class VRControls : MonoBehaviour
{
    public BioGearsNativeRespiratory sim;

    public Slider asthmaSlider;
    public Slider copdSlider;
    public Slider exerciseSlider;

    void Start()
    {
        asthmaSlider.onValueChanged.AddListener(UpdateAsthma);
        copdSlider.onValueChanged.AddListener(UpdateCOPD);
        exerciseSlider.onValueChanged.AddListener(UpdateExercise);
    }

    void UpdateAsthma(float value)
    {
        sim.ApplyCondition("asthma", value);
    }

    void UpdateCOPD(float value)
    {
        sim.ApplyCondition("copd", value);
    }

    void UpdateExercise(float value)
    {
        sim.ApplyCondition("exercise", value);
    }
}