using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// Headset end of the BioGears link. Finds the PC from its beacon (or uses a fixed
/// address), keeps the connection alive, forwards the user's settings and holds the
/// latest physiology snapshot. Plain C# - the Unity side (BioGearsNativeRespiratory)
/// polls it.
///
/// Settings are kept as a desired state (name -> value) and the whole state is resent
/// on connect and every couple of seconds, so a PC that starts late, restarts, or
/// missed a packet still ends up with exactly what the headset shows.
/// </summary>
public sealed class BioGearsLinkClient : IDisposable
{
    readonly UdpClient udp;
    readonly Thread receiveThread;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly bool fixedServer;
    volatile bool running = true;

    readonly object gate = new object();
    IPEndPoint server;
    string serverName = "";
    BioGearsLinkProtocol.Snapshot latest;
    double latestAt = double.NegativeInfinity;
    double serverChosenAt = double.NegativeInfinity;
    readonly Dictionary<string, float> desired = new Dictionary<string, float>();

    double nextHello;
    double nextResync;

    /// <param name="fixedServerAddress">PC address to use instead of discovery (IP or host name), or null/empty to discover.</param>
    public BioGearsLinkClient(string fixedServerAddress = null)
    {
        udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, BioGearsLinkProtocol.BeaconPort));
        udp.EnableBroadcast = true;
        BioGearsLinkProtocol.DisableUdpConnReset(udp.Client);

        if (!string.IsNullOrWhiteSpace(fixedServerAddress))
        {
            IPAddress address;
            if (!IPAddress.TryParse(fixedServerAddress.Trim(), out address))
                address = Dns.GetHostAddresses(fixedServerAddress.Trim())[0];
            server = new IPEndPoint(address, BioGearsLinkProtocol.ServerPort);
            serverName = fixedServerAddress.Trim();
            fixedServer = true;
        }

        receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "BioGearsLinkClient" };
        receiveThread.Start();
    }

    double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Name of the PC we are talking to (empty until one is found).</summary>
    public string ServerName
    {
        get { lock (gate) return serverName; }
    }

    /// <summary>IP address of the PC we are talking to, or null until one is found. Other
    /// services on the same PC (e.g. the LungVision backend) can be reached through it.</summary>
    public string ServerAddress
    {
        get { lock (gate) return server?.Address.ToString(); }
    }

    /// <summary>True while snapshots are arriving (one within the last <paramref name="staleAfterSeconds"/>).</summary>
    public bool IsConnected(double staleAfterSeconds = 3.0)
    {
        lock (gate) return Now - latestAt < staleAfterSeconds;
    }

    /// <summary>The newest snapshot and its age in seconds. False if none has arrived yet.</summary>
    public bool TryGetLatest(out BioGearsLinkProtocol.Snapshot snapshot, out double ageSeconds)
    {
        lock (gate)
        {
            snapshot = latest;
            ageSeconds = Now - latestAt;
            return latest != null;
        }
    }

    /// <summary>Records a setting and sends it now if a PC is known. NaN = clear (e.g. "spo2").</summary>
    public void SetDesired(string name, float value)
    {
        IPEndPoint target;
        lock (gate)
        {
            desired[name] = value;
            target = server;
        }
        if (target != null)
            SendTo(target, BioGearsLinkProtocol.EncodeCommand(name, value));
    }

    /// <summary>Call regularly (e.g. every frame): keep-alive once a second, full resync every two.</summary>
    public void Tick()
    {
        IPEndPoint target;
        lock (gate) target = server;
        if (target == null)
            return;

        double now = Now;
        if (now >= nextHello)
        {
            nextHello = now + 1.0;
            SendTo(target, BioGearsLinkProtocol.EncodeHello());
        }
        if (now >= nextResync)
        {
            nextResync = now + 2.0;
            SendAllDesired(target);
        }
    }

    void SendAllDesired(IPEndPoint target)
    {
        KeyValuePair<string, float>[] all;
        lock (gate)
        {
            all = new KeyValuePair<string, float>[desired.Count];
            ((ICollection<KeyValuePair<string, float>>)desired).CopyTo(all, 0);
        }
        foreach (var kv in all)
            SendTo(target, BioGearsLinkProtocol.EncodeCommand(kv.Key, kv.Value));
    }

    void SendTo(IPEndPoint target, byte[] packet)
    {
        try { udp.Send(packet, packet.Length, target); }
        catch (SocketException) { /* no route right now - retried on the next tick */ }
        catch (ObjectDisposedException) { }
    }

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
                    if (kind == BioGearsLinkProtocol.Kind.Beacon && !fixedServer)
                    {
                        string name = BioGearsLinkProtocol.ReadBeacon(reader);
                        // Beacons come from the server's own socket, so their source is
                        // exactly where hello/commands should go.
                        var ep = new IPEndPoint(from.Address, from.Port);
                        bool isNew;
                        lock (gate)
                        {
                            // Stay with the PC we already have unless it has gone quiet. A
                            // PC with several network adapters beacons from each of its
                            // addresses; without the grace period after choosing one, every
                            // beacon would switch to another address before the first
                            // snapshot had time to arrive.
                            double now = Now;
                            bool current = server != null && (now - latestAt < 3.0 || now - serverChosenAt < 3.0);
                            isNew = !current && (server == null || !server.Equals(ep));
                            if (!current)
                            {
                                server = ep;
                                serverName = name;
                                serverChosenAt = now;
                            }
                        }
                        if (isNew)
                        {
                            SendTo(ep, BioGearsLinkProtocol.EncodeHello());
                            SendAllDesired(ep);
                        }
                    }
                    else if (kind == BioGearsLinkProtocol.Kind.Snapshot)
                    {
                        var snapshot = BioGearsLinkProtocol.ReadSnapshot(reader);
                        lock (gate)
                        {
                            if (server != null && from.Address.Equals(server.Address))
                            {
                                latest = snapshot;
                                latestAt = Now;
                            }
                        }
                    }
                }
            }
            catch (SocketException)
            {
                if (!running) break;
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

    public void Dispose()
    {
        running = false;
        try { udp.Close(); } catch { }
        try { receiveThread.Join(500); } catch { }
    }
}
