using System;
using System.IO;
using System.Text;

/// <summary>
/// Wire format for running BioGears on a PC and showing it on a standalone Quest.
/// Plain C# (no UnityEngine) so the network layer can be tested outside Unity.
///
///   PC  (BioGearsLinkServer, UDP ServerPort)  --Beacon (broadcast, 1/s)-->   Quest (BeaconPort)
///   PC  <--Hello (keep-alive) / Command--   Quest
///   PC  --Snapshot (~20/s)-->   Quest
///
/// Every packet starts with "BGL" + version byte, then a kind byte. Numbers are
/// little-endian binary; NaN means "not computed".
/// </summary>
public static class BioGearsLinkProtocol
{
    public const int BeaconPort = 47810;   // Quest listens here (beacons and snapshots)
    public const int ServerPort = 47811;   // PC listens here (hello and commands)
    public const byte Version = 1;

    public enum Kind : byte { Beacon = 1, Hello = 2, Command = 3, Snapshot = 4 }

    // Order is the wire order - append only, and bump Version if anything is removed.
    public enum Field
    {
        RespirationRate, TidalVolume, FunctionalResidualCapacity, TotalLungVolume,
        InspiratoryExpiratoryRatio, HeartRate, OxygenSaturation, PulseOximetry,
        CarbonMonoxideSaturation, LeftLungVolume, RightLungVolume, MinuteVentilation,
        TranspulmonaryPressure, MeanArterialPressure, EndTidalCO2Fraction,
        OxygenConsumption, CarbonDioxideProduction, RespiratoryExchangeRatio,
        AirwayResistance, LungCompliance, AlveolarVentilation, DeadSpaceVentilation,
        AlveolarArterialGradient, ArterialO2Pressure, ArterialCO2Pressure, CarricoIndex,
        PneumoniaSeverity, PneumoniaLeft, PneumoniaRight,
        Count
    }

    public const int FieldCount = (int)Field.Count;

    public sealed class Snapshot
    {
        public readonly float[] Values = new float[FieldCount];
        public bool EngineRunning;
        public bool NativeEngine;
        public string Status = "";

        public float this[Field f]
        {
            get => Values[(int)f];
            set => Values[(int)f] = value;
        }
    }

    static BinaryWriter Begin(MemoryStream ms, Kind kind)
    {
        var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)'B'); w.Write((byte)'G'); w.Write((byte)'L'); w.Write(Version);
        w.Write((byte)kind);
        return w;
    }

    public static byte[] EncodeBeacon(string serverName)
    {
        using var ms = new MemoryStream();
        using var w = Begin(ms, Kind.Beacon);
        w.Write(serverName ?? "");
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] EncodeHello()
    {
        using var ms = new MemoryStream();
        using var w = Begin(ms, Kind.Hello);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A desired value, e.g. ("cond:asthma", 0.7), ("rr", 14). NaN clears ("spo2").</summary>
    public static byte[] EncodeCommand(string name, float value)
    {
        using var ms = new MemoryStream();
        using var w = Begin(ms, Kind.Command);
        w.Write(name ?? "");
        w.Write(value);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] EncodeSnapshot(Snapshot s)
    {
        using var ms = new MemoryStream();
        using var w = Begin(ms, Kind.Snapshot);
        w.Write((byte)FieldCount);
        for (int i = 0; i < FieldCount; i++)
            w.Write(s.Values[i]);
        w.Write(s.EngineRunning);
        w.Write(s.NativeEngine);
        w.Write(s.Status ?? "");
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Reads the header. Returns false for anything that isn't a packet of ours (other
    /// traffic on the port, or a different protocol version).
    /// </summary>
    public static bool TryReadHeader(byte[] data, out Kind kind, out BinaryReader reader)
    {
        kind = 0;
        reader = null;
        if (data == null || data.Length < 5 || data[0] != 'B' || data[1] != 'G' || data[2] != 'L' || data[3] != Version)
            return false;
        kind = (Kind)data[4];
        reader = new BinaryReader(new MemoryStream(data, 5, data.Length - 5), Encoding.UTF8);
        return true;
    }

    public static string ReadBeacon(BinaryReader r) => r.ReadString();

    public static void ReadCommand(BinaryReader r, out string name, out float value)
    {
        name = r.ReadString();
        value = r.ReadSingle();
    }

    public static Snapshot ReadSnapshot(BinaryReader r)
    {
        var s = new Snapshot();
        int count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            float v = r.ReadSingle();
            if (i < FieldCount) s.Values[i] = v; // newer server, extra fields: ignore
        }
        for (int i = count; i < FieldCount; i++)
            s.Values[i] = float.NaN;             // older server, missing fields: unknown
        s.EngineRunning = r.ReadBoolean();
        s.NativeEngine = r.ReadBoolean();
        s.Status = r.ReadString();
        return s;
    }

    /// <summary>
    /// Windows reports "connection reset" on a UDP socket after sending to a peer that has
    /// gone away, which then breaks Receive. Turn that off (Windows-only control code;
    /// a no-op failure elsewhere).
    /// </summary>
    public static void DisableUdpConnReset(System.Net.Sockets.Socket socket)
    {
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch
        {
            // Not Windows, or not supported: Receive loops also tolerate the error.
        }
    }
}
