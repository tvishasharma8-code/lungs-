using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Exercises BioGearsLinkServer <-> BioGearsLinkClient on this machine:
// discovery by beacon, commands up, snapshots down, resync after a server restart,
// and the client noticing the server going away.
static partial class Program
{
    internal static int SelfTest()
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) failures++; }

        var server = new BioGearsLinkServer("TestPC");
        var client = new BioGearsLinkClient();
        client.SetDesired("cond:asthma", 0.6f);   // set before any server is known
        client.SetDesired("rr", 8f);

        var received = new Dictionary<string, float>();
        var sw = Stopwatch.StartNew();
        double nextSnap = 0;
        int seq = 0;

        void Pump(BioGearsLinkServer srv, double until)
        {
            while (sw.Elapsed.TotalSeconds < until)
            {
                srv?.Tick();
                client.Tick();
                if (srv != null)
                {
                    while (srv.TryGetCommand(out var n, out var v)) received[n] = v;
                    if (sw.Elapsed.TotalSeconds >= nextSnap)
                    {
                        nextSnap = sw.Elapsed.TotalSeconds + 0.05;
                        var s = new BioGearsLinkProtocol.Snapshot { EngineRunning = true, NativeEngine = true, Status = "Running" };
                        for (int i = 0; i < BioGearsLinkProtocol.FieldCount; i++) s.Values[i] = float.NaN;
                        s[BioGearsLinkProtocol.Field.RespirationRate] = 14f + (seq++ % 3);
                        s[BioGearsLinkProtocol.Field.PneumoniaRight] = 0.7f;
                        srv.Send(s);
                    }
                }
                Thread.Sleep(10);
            }
        }

        Console.WriteLine("Discovery + commands + snapshots (3 s):");
        Pump(server, 3.0);
        Check(client.ServerName == "TestPC", $"client found server by beacon (name='{client.ServerName}')");
        Check(server.ClientCount == 1, $"server has the headset as a client (count={server.ClientCount})");
        Check(received.TryGetValue("cond:asthma", out var a) && Math.Abs(a - 0.6f) < 1e-6, "setting made before connect reached the server (asthma 0.6)");
        Check(received.TryGetValue("rr", out var rr) && Math.Abs(rr - 8f) < 1e-6, "rr 8 reached the server");
        bool got = client.TryGetLatest(out var snap, out var age);
        Check(got && client.IsConnected(), $"client is connected, latest snapshot {age:F2}s old");
        Check(got && snap[BioGearsLinkProtocol.Field.RespirationRate] >= 14f && snap.NativeEngine, "snapshot values arrived (RR, native flag)");
        Check(got && float.IsNaN(snap[BioGearsLinkProtocol.Field.OxygenConsumption]), "NaN (not computed) survives the wire");

        Console.WriteLine("Live change while connected:");
        client.SetDesired("spo2", float.NaN);
        Pump(server, 3.5);
        Check(received.TryGetValue("spo2", out var sp) && float.IsNaN(sp), "spo2 clear (NaN) reached the server");

        Console.WriteLine("Server goes away (4 s):");
        server.Dispose();
        Pump(null, 7.5);
        Check(!client.IsConnected(), "client reports disconnected after the server stops");

        Console.WriteLine("Server comes back (3 s):");
        received.Clear();
        var server2 = new BioGearsLinkServer("TestPC");
        Pump(server2, 10.5);
        Check(client.IsConnected(), "client reconnected to the restarted server");
        Check(received.ContainsKey("cond:asthma") && received.ContainsKey("rr"), "full settings were resent to the restarted server");

        server2.Dispose();
        client.Dispose();
        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
