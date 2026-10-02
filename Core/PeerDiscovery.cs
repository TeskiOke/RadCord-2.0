using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RadCord.Core;

/// <summary>UDP broadcast peer discovery — mirrors discovery.py exactly.</summary>
public class PeerDiscovery
{
    private readonly AppConfig _config;
    private readonly Action<JObject> _onPeerFound;
    private CancellationTokenSource? _cts;
    private UdpClient? _recvClient;

    // Live-updated cache of discovered networks for NetworkScanner
    public static readonly Dictionary<string, JObject> DiscoveredNetworks = new();

    public PeerDiscovery(AppConfig config, Action<JObject> onPeerFound)
    {
        _config      = config;
        _onPeerFound = onPeerFound;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _recvClient?.Close(); } catch { }
    }


    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            _recvClient = new UdpClient();
            _recvClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _recvClient.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.UdpDiscoveryPort));
            _recvClient.EnableBroadcast = true;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _recvClient.ReceiveAsync(ct);
                    var json   = Encoding.UTF8.GetString(result.Buffer);
                    var msg    = JObject.Parse(json);
                    if (msg["type"]?.ToString() != "hello") continue;

                    // Feed discovery cache regardless of network match
                    FeedPacket(msg, result.RemoteEndPoint.Address.ToString());

                    var senderId = msg["device_id"]?.ToString() ?? "";
                    if (senderId == _config.DeviceId) continue;

                    var packetNetId = msg["network_id"]?.ToString() ?? "";
                    var myNetIds    = _config.Networks.Select(n => n.Id).ToHashSet();
                    if (myNetIds.Contains(packetNetId))
                        _onPeerFound(msg);
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore malformed packets */ }
            }
        }
        catch { }
    }


    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        while (!ct.IsCancellationRequested)
        {
            var myIp = GetLocalIp();
            foreach (var net in _config.Networks)
            {
                var hello = JsonConvert.SerializeObject(new
                {
                    type         = "hello",
                    device_id    = _config.DeviceId,
                    nickname     = _config.Nickname,
                    avatar_color = _config.AvatarColor,
                    network_id   = net.Id,
                    network_name = net.ShownName,
                    ip           = myIp,
                    ws_port      = Protocol.WsPort,
                    version      = Protocol.AppVersion,
                });
                var bytes = Encoding.UTF8.GetBytes(hello);

                foreach (var addr in Protocol.UdpBroadcastAddrs)
                    TrySend(sender, bytes, addr);

                TrySend(sender, bytes, "127.255.255.255");

                if (myIp != "127.0.0.1")
                {
                    var parts = myIp.Split('.');
                    if (parts.Length == 4)
                        TrySend(sender, bytes, $"{parts[0]}.{parts[1]}.{parts[2]}.255");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Protocol.UdpInterval), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void TrySend(UdpClient c, byte[] data, string addr)
    {
        try { c.Send(data, data.Length, addr, Protocol.UdpDiscoveryPort); }
        catch { }
    }


    public static string GetLocalIp()
    {
        // Prefer Radmin VPN IP (26.x.x.x)
        foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var ua in iface.GetIPProperties().UnicastAddresses)
            {
                var s = ua.Address.ToString();
                if (s.StartsWith("26.")) return s;
            }
        }
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 80);
            return (s.LocalEndPoint as IPEndPoint)!.Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    public static void FeedPacket(JObject msg, string ip)
    {
        var nid  = msg["network_id"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(nid)) return;
        var name = msg["network_name"]?.ToString() ?? "Неизвестная сеть";
        var nick = msg["nickname"]?.ToString()     ?? "?";

        if (!DiscoveredNetworks.TryGetValue(nid, out var entry))
        {
            entry = new JObject
            {
                ["name"]       = name,
                ["network_id"] = nid,
                ["users"]      = new JArray(),
                ["ips"]        = new JArray(),
            };
            DiscoveredNetworks[nid] = entry;
        }

        var users = (JArray)entry["users"]!;
        if (!users.Any(u => u.ToString() == nick)) users.Add(nick);

        var ips = (JArray)entry["ips"]!;
        if (!string.IsNullOrEmpty(ip) && !ips.Any(u => u.ToString() == ip)) ips.Add(ip);
    }

    /// <summary>Actively scan for ~duration seconds and return found networks.</summary>
    public static async Task<List<JObject>> ScanAsync(double duration = 4.0)
    {
        DiscoveredNetworks.Clear();

        UdpClient? scan = null;
        CancellationTokenSource cts = new();
        try
        {
            scan = new UdpClient();
            scan.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            scan.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.UdpDiscoveryPort));
            scan.EnableBroadcast = true;

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var r   = await scan.ReceiveAsync(cts.Token);
                        var msg = JObject.Parse(Encoding.UTF8.GetString(r.Buffer));
                        if (msg["type"]?.ToString() == "hello")
                            FeedPacket(msg, r.RemoteEndPoint.Address.ToString());
                    }
                    catch { break; }
                }
            });
        }
        catch { /* port already in use — PeerDiscovery is running and will feed us */ }

        await Task.Delay(TimeSpan.FromSeconds(duration));
        cts.Cancel();
        try { scan?.Close(); } catch { }

        return DiscoveredNetworks.Values.OrderByDescending(n => ((JArray)n["users"]!).Count).ToList();
    }
}
