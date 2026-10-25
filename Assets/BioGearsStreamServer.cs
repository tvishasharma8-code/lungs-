using System.Collections.Generic;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Serves this PC's real BioGears engine to standalone Quest headsets on the same
/// network (BioGearsLinkServer). While it runs, a headset finds the PC by itself,
/// streams the physiology from here, and every slider / preset on the headset is
/// applied to this engine.
///
/// Only serves when the native engine is actually running - on the Quest itself
/// (no native BioGears) this component stays idle, and BioGearsNativeRespiratory
/// there acts as the client instead.
/// </summary>
public class BioGearsStreamServer : MonoBehaviour
{
    public BioGearsNativeRespiratory bio;

    [Tooltip("Let standalone Quest headsets on this network use this PC's BioGears engine.")]
    public bool serveToHeadset = true;

    [Tooltip("Physiology updates sent per second.")]
    public float snapshotsPerSecond = 20f;

    [Tooltip("Name the headset sees for this PC. Empty = the computer's name.")]
    public string serverName = "";

    [Header("Status (read-only)")]
    public int connectedHeadsets;

    BioGearsLinkServer server;
    float nextSnapshot;
    bool startFailed;

    // Headsets resend their whole settings every couple of seconds; only act on changes,
    // so a resend never re-triggers an engine action or a patient reload.
    readonly Dictionary<string, float> applied = new Dictionary<string, float>();

    void Awake()
    {
        if (bio == null)
            bio = GetComponent<BioGearsNativeRespiratory>();
    }

    void Update()
    {
        if (!serveToHeadset || bio == null || startFailed || !bio.usingNativeEngine)
        {
            connectedHeadsets = 0;
            return;
        }

        if (server == null && !StartServer())
            return;

        server.Tick();
        while (server.TryGetCommand(out string name, out float value))
            Apply(name, value);

        connectedHeadsets = server.ClientCount;
        if (connectedHeadsets > 0 && Time.unscaledTime >= nextSnapshot)
        {
            nextSnapshot = Time.unscaledTime + 1f / Mathf.Max(1f, snapshotsPerSecond);
            server.Send(BuildSnapshot());
        }
    }

    bool StartServer()
    {
        try
        {
            string name = string.IsNullOrEmpty(serverName) ? SystemInfo.deviceName : serverName;
            server = new BioGearsLinkServer(name);
            Debug.Log($"[BioGearsStreamServer] Serving BioGears to headsets as '{name}' (UDP {BioGearsLinkProtocol.ServerPort}). If Windows Firewall asks, allow Unity on private networks.");
            return true;
        }
        catch (SocketException e)
        {
            startFailed = true;
            Debug.LogWarning($"[BioGearsStreamServer] Could not open UDP port {BioGearsLinkProtocol.ServerPort} ({e.Message}) - is another copy of the app already serving? Headsets can't connect to this one.");
            return false;
        }
    }

    void Apply(string name, float value)
    {
        if (applied.TryGetValue(name, out float previous) && previous.Equals(value)) // Equals: NaN == NaN
            return;
        applied[name] = value;

        if (name.StartsWith("cond:"))
        {
            bio.ApplyCondition(name.Substring(5), value);
            return;
        }

        switch (name)
        {
            case "rr": bio.SetRespirationRate(value); break;
            case "tv": bio.SetTidalVolume(value); break;
            case "spo2":
                if (float.IsNaN(value)) bio.ClearMeasuredOxygenSaturation();
                else bio.SetMeasuredOxygenSaturation(value);
                break;
            default:
                Debug.LogWarning($"[BioGearsStreamServer] Unknown setting from headset: {name}");
                break;
        }
    }

    BioGearsLinkProtocol.Snapshot BuildSnapshot()
    {
        var s = new BioGearsLinkProtocol.Snapshot
        {
            EngineRunning = bio.IsRunning,
            NativeEngine = bio.usingNativeEngine,
            Status = bio.statusMessage ?? "",
        };

        s[BioGearsLinkProtocol.Field.RespirationRate] = bio.RespirationRate;
        s[BioGearsLinkProtocol.Field.TidalVolume] = bio.TidalVolume;
        s[BioGearsLinkProtocol.Field.FunctionalResidualCapacity] = bio.FunctionalResidualCapacity;
        s[BioGearsLinkProtocol.Field.TotalLungVolume] = bio.TotalLungVolume;
        s[BioGearsLinkProtocol.Field.InspiratoryExpiratoryRatio] = bio.InspiratoryExpiratoryRatio;
        s[BioGearsLinkProtocol.Field.HeartRate] = bio.HeartRate;
        s[BioGearsLinkProtocol.Field.OxygenSaturation] = bio.OxygenSaturation;
        s[BioGearsLinkProtocol.Field.PulseOximetry] = bio.PulseOximetry;
        s[BioGearsLinkProtocol.Field.CarbonMonoxideSaturation] = bio.CarbonMonoxideSaturation;
        s[BioGearsLinkProtocol.Field.LeftLungVolume] = bio.LeftLungVolume;
        s[BioGearsLinkProtocol.Field.RightLungVolume] = bio.RightLungVolume;
        s[BioGearsLinkProtocol.Field.MinuteVentilation] = bio.MinuteVentilation;
        s[BioGearsLinkProtocol.Field.TranspulmonaryPressure] = bio.TranspulmonaryPressure;
        s[BioGearsLinkProtocol.Field.MeanArterialPressure] = bio.MeanArterialPressure;
        s[BioGearsLinkProtocol.Field.EndTidalCO2Fraction] = bio.EndTidalCO2Fraction;
        s[BioGearsLinkProtocol.Field.OxygenConsumption] = bio.OxygenConsumption;
        s[BioGearsLinkProtocol.Field.CarbonDioxideProduction] = bio.CarbonDioxideProduction;
        s[BioGearsLinkProtocol.Field.RespiratoryExchangeRatio] = bio.RespiratoryExchangeRatio;
        s[BioGearsLinkProtocol.Field.AirwayResistance] = bio.AirwayResistance;
        s[BioGearsLinkProtocol.Field.LungCompliance] = bio.LungCompliance;
        s[BioGearsLinkProtocol.Field.AlveolarVentilation] = bio.AlveolarVentilation;
        s[BioGearsLinkProtocol.Field.DeadSpaceVentilation] = bio.DeadSpaceVentilation;
        s[BioGearsLinkProtocol.Field.AlveolarArterialGradient] = bio.AlveolarArterialGradient;
        s[BioGearsLinkProtocol.Field.ArterialO2Pressure] = bio.ArterialO2Pressure;
        s[BioGearsLinkProtocol.Field.ArterialCO2Pressure] = bio.ArterialCO2Pressure;
        s[BioGearsLinkProtocol.Field.CarricoIndex] = bio.CarricoIndex;

        BioGearsNativeRespiratory.PneumoniaExtent pn = bio.ActivePneumonia;
        s[BioGearsLinkProtocol.Field.PneumoniaSeverity] = pn.severity;
        s[BioGearsLinkProtocol.Field.PneumoniaLeft] = pn.left;
        s[BioGearsLinkProtocol.Field.PneumoniaRight] = pn.right;
        return s;
    }

    void OnDisable()
    {
        server?.Dispose();
        server = null;
        connectedHeadsets = 0;
    }
}
