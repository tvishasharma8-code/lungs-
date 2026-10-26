using UnityEngine;

/// <summary>
/// Interface matching BioGears respiratory system outputs.
/// Implement this with either the simulated model or real BioGears DLLs.
/// </summary>
public interface IBioGearsRespiratorySystem
{
    /// <summary>Respiration rate in breaths per minute</summary>
    float RespirationRate { get; }

    /// <summary>Tidal volume in mL</summary>
    float TidalVolume { get; }

    /// <summary>Total lung volume in mL</summary>
    float TotalLungVolume { get; }

    /// <summary>Functional residual capacity in mL</summary>
    float FunctionalResidualCapacity { get; }

    /// <summary>Inspiratory/expiratory ratio</summary>
    float InspiratoryExpiratoryRatio { get; }

    /// <summary>Current phase of breathing: 0 = start of inhale, 0.5 = start of exhale, 1 = end of cycle</summary>
    float BreathingPhase { get; }

    /// <summary>Current lung volume expansion factor (0 = fully exhaled, 1 = fully inhaled)</summary>
    float ExpansionFactor { get; }

    /// <summary>Oxygen saturation (SpO2) as fraction 0-1</summary>
    float OxygenSaturation { get; }

    /// <summary>Heart rate in BPM</summary>
    float HeartRate { get; }

    /// <summary>Whether the engine is initialized and running</summary>
    bool IsRunning { get; }

    /// <summary>Initialize the physiology engine</summary>
    void Initialize();

    /// <summary>Advance the simulation by deltaTime seconds</summary>
    void AdvanceTime(float deltaTime);

    /// <summary>Apply a condition (e.g., asthma, COPD, respiratory distress)</summary>
    void ApplyCondition(string conditionName, float severity);
}
