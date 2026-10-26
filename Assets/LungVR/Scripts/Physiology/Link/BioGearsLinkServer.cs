using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// PC end of the BioGears link. Announces itself on the local network, keeps track of
/// headsets that say hello, sends them physiology snapshots and hands their commands
/// to the caller. Plain C#: the caller (BioGearsStreamServer in Unity) owns the engine.
///
/// Not thread-affine except where noted: Tick/Send/TryGetCommand are meant for one
/// caller thread; the receive loop runs on its own background thread.
/// </summary>
public sealed class BioGearsLinkServer : IDisposable
{
    readonly UdpClient udp;
    readonly Thread receiveThread;
    readonly string serverName;
    readonly byte[] beacon;
    readonly Stopwatch clock = Stopwatch.StartNew();
    volatile bool running = true;

    // Headset endpoint -> last time we heard from it (clock seconds).
    readonly Dictionary<IPEndPoint, double> clients = new Dictionary<IPEndPoint, double>();
    readonly object clientsLock = new object();
    readonly ConcurrentQueue<(string name, float value)> commands = new ConcurrentQueue<(string, float)>();

    double nextBeacon;
    double nextInterfaceScan;
    List<IPEndPoint> beaconTargets = new List<IPEndPoint>();

    /// <summary>A headset that has been silent this long is dropped.</summary>
    public double ClientTimeoutSeconds = 5.0;

    public BioGearsLinkServer(string name, int port = BioGearsLinkProtocol.ServerPort)
    {
        serverName = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
        beacon = BioGearsLinkProtocol.EncodeBeacon(serverName);

        udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)) { EnableBroadcast = true };
        BioGearsLinkProtocol.DisableUdpConnReset(udp.Client);

        receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BioGearsLinkServer" };
        receiveThread.Start();
    }

    public int ClientCount
    {
        get { lock (clientsLock) return clients.Count; }
    }

    double Now => clock.Elapsed.TotalSeconds;

    void ReceiveLoop()
    {
        while (running)
        {
            try
            {
                IPEndPoint from = null;
                byte[] data = udp.Receive(ref from);
                if (!BioGearsLinkProtocol.TryReadHeader(data, out var kind, out var reader))
                    continue;

                using (reader)
                {
                    if (kind == BioGearsLinkProtocol.Kind.Hello || kind == BioGearsLinkProtocol.Kind.Command)
                    {
                        lock (clientsLock) clients[from] = Now;
                    }
                    if (kind == BioGearsLinkProtocol.Kind.Command)
                    {
                        BioGearsLinkProtocol.ReadCommand(reader, out string name, out float value);
                        commands.Enqueue((name, value));
                    }
                }
            }
            catch (SocketException)
            {
                if (!running) break; // socket closed by Dispose
                // Otherwise a transient error (e.g. a reset from a vanished peer): keep going.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // Malformed packet - ignore it.
            }
        }
    }

    /// <summary>Call regularly (e.g. every frame): sends the beacon once a second and drops silent headsets.</summary>
    public void Tick()
    {
        double now = Now;

        if (now >= nextInterfaceScan)
        {
            beaconTargets = FindBroadcastTargets();
            nextInterfaceScan = now + 10.0;
        }

        if (now >= nextBeacon)
        {
            nextBeacon = now + 1.0;
            foreach (IPEndPoint target in beaconTargets)
            {
                try { udp.Send(beacon, beacon.Length, target); }
                catch (SocketException) { /* interface went away - rescanned soon */ }
            }
        }

        lock (clientsLock)
        {
            List<IPEndPoint> stale = null;
            foreach (var kv in clients)
                if (now - kv.Value > ClientTimeoutSeconds)
                    (stale ??= new List<IPEndPoint>()).Add(kv.Key);
            if (stale != null)
                foreach (IPEndPoint ep in stale)
                    clients.Remove(ep);
        }
    }

    /// <summary>Sends a snapshot to every connected headset.</summary>
    public void Send(BioGearsLinkProtocol.Snapshot snapshot)
    {
        IPEndPoint[] targets;
        lock (clientsLock)
        {
            if (clients.Count == 0) return;
            targets = new IPEndPoint[clients.Count];
            clients.Keys.CopyTo(targets, 0);
        }

        byte[] packet = BioGearsLinkProtocol.EncodeSnapshot(snapshot);
        foreach (IPEndPoint ep in targets)
        {
            try { udp.Send(packet, packet.Length, ep); }
            catch (SocketException) { /* headset gone - it will time out */ }
        }
    }

    public bool TryGetCommand(out string name, out float value)
    {
        if (commands.TryDequeue(out var c))
        {
            name = c.name;
            value = c.value;
            return true;
        }
        name = null;
        value = 0f;
        return false;
    }

    /// <summary>
    /// The broadcast address of every active IPv4 network, plus 255.255.255.255. A PC
    /// often has several adapters (Wi-Fi, Ethernet, Hyper-V/WSL/VirtualBox), and a bare
    /// 255.255.255.255 broadcast may leave through the wrong one - so the beacon goes out
    /// on each network's own broadcast address.
    /// </summary>
    static List<IPEndPoint> FindBroadcastTargets()
    {
        var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, BioGearsLinkProtocol.BeaconPort) };
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null)
                        continue;
                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;
                    byte[] bc = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bc[i] = (byte)(ip[i] | ~mask[i]);
                    var ep = new IPEndPoint(new IPAddress(bc), BioGearsLinkProtocol.BeaconPort);
                    if (!targets.Exists(t => t.Equals(ep)))
                        targets.Add(ep);
                }
            }
        }
        catch (Exception)
        {
            // Interface enumeration can fail on some platforms; 255.255.255.255 still goes out.
        }
        return targets;
    }

    public void Dispose()
    {
        running = false;
        try { udp.Close(); } catch { }
        try { receiveThread.Join(500); } catch { }
    }
}
