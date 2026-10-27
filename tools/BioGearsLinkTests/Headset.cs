using System;
using System.Diagnostics;
using System.Threading;

// Fake headset: finds the PC running the scene in Play mode (BioGearsStreamServer), prints
// what it streams, and drives it the way the Quest console would - so the PC side can be
// checked without building to the headset.
static class Headset
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "headset")
            return Program.SelfTest();

        string address = args.Length > 1 ? args[1] : null;
        using var client = new BioGearsLinkClient(address);
        Console.WriteLine(address == null ? "Looking for a BioGears PC on the network..." : $"Connecting to {address}...");

        var sw = Stopwatch.StartNew();
        double nextPrint = 0;
        bool asthmaSent = false, slowSent = false;
        while (sw.Elapsed.TotalSeconds < 120)
        {
            client.Tick();
            double t = sw.Elapsed.TotalSeconds;

            // A short script, like a user at the console: asthma, then slow shallow breathing.
            if (!asthmaSent && t > 20) { client.SetDesired("cond:asthma", 0.7f); asthmaSent = true; Console.WriteLine(">> asthma 70%"); }
            if (!slowSent && t > 50) { client.SetDesired("rr", 8f); client.SetDesired("tv", 300f); slowSent = true; Console.WriteLine(">> paced 8/min x 300 mL"); }

            if (t >= nextPrint)
            {
                nextPrint = t + 5;
                if (client.TryGetLatest(out var s, out double age) && age < 3)
                    Console.WriteLine($"t={t,5:F0}s  PC '{client.ServerName}'  RR={s[BioGearsLinkProtocol.Field.RespirationRate],5:F1}  " +
                                      $"TV={s[BioGearsLinkProtocol.Field.TidalVolume],5:F0}  SpO2={s[BioGearsLinkProtocol.Field.OxygenSaturation] * 100,5:F1}%  " +
                                      $"PaO2={s[BioGearsLinkProtocol.Field.ArterialO2Pressure],5:F1}  PaCO2={s[BioGearsLinkProtocol.Field.ArterialCO2Pressure],5:F1}  " +
                                      $"Raw={s[BioGearsLinkProtocol.Field.AirwayResistance],5:F2}  [{s.Status}]");
                else
                    Console.WriteLine($"t={t,5:F0}s  not connected" + (client.ServerName != "" ? $" (heard '{client.ServerName}')" : ""));
            }
            Thread.Sleep(20);
        }

        // Leave the PC's patient as we found it.
        client.SetDesired("cond:asthma", 0f);
        client.SetDesired("rr", 14f);
        client.SetDesired("tv", 500f);
        Thread.Sleep(200);
        return 0;
    }
}
