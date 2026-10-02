using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RadCord.Models;

namespace RadCord.Core;

/// <summary>
/// P2P WebSocket mesh.  
/// Server  → Fleck (no admin rights needed).
/// Clients → System.Net.WebSockets.ClientWebSocket.
/// Protocol is 100% compatible with the Python websockets implementation.
/// </summary>
public class MeshNetwork
{
    private readonly AppConfig _config;
    private WebSocketServer?   _server;

    // device_id → PeerConnection
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
    // IPs currently dialing
    private readonly ConcurrentDictionary<string, byte> _connecting = new();
    // Dedup ring buffer
    private readonly HashSet<string>           _seenIds    = new();
    private readonly Queue<string>             _seenQueue  = new();
    private const int SeenMax = 2000;

    public Action<Peer>?                        OnPeerJoin            { get; set; }
    public Action<Peer>?                        OnPeerLeave           { get; set; }
    public Action<Peer, JObject>?               OnTextMessage         { get; set; }
    public Action<Peer, byte[]>?                OnVoiceFrame          { get; set; }
    public Action<Peer, byte[]>?                OnScreenFrame         { get; set; }
    public Action<Peer, byte[]>?                OnStreamAudioFrame    { get; set; }
    public Action<Peer>?                        OnVoiceState          { get; set; }
    public Action<Peer>?                        OnScreenStateChange   { get; set; }
    public Action<string, string, bool>?         OnChannelAdd          { get; set; }  // netId, name, isVoice
    public Action<string, string, bool>?         OnChannelRemove       { get; set; }  // netId, name, isVoice
    public Action<string, int>?                 OnPingUpdated         { get; set; }  // deviceId, ms
    public Action<Peer, JObject>?               OnFileMeta            { get; set; }
    public Action<Peer, JObject>?               OnFileChunk           { get; set; }

    public IReadOnlyDictionary<string, PeerConnection> Peers => _peers;


    public void Start()
    {
        _server = new WebSocketServer($"ws://0.0.0.0:{Protocol.WsPort}");
        _server.RestartAfterListenError = true;
        _server.Start(ws =>
        {
            ws.OnOpen    = () => { };
            ws.OnMessage = msg  => HandleIncomingHandshake(ws, msg);
            ws.OnBinary  = data => { /* handshake is text only */ };
            ws.OnClose   = () => { };
            ws.OnError   = ex  => { };
        });

        _ = Task.Run(PingLoopAsync);
    }

    public void Stop()
    {
        try { _server?.Dispose(); } catch { }
        foreach (var pc in _peers.Values)
            try { pc.Close(); } catch { }
        _peers.Clear();
    }


    private void HandleIncomingHandshake(IWebSocketConnection ws, string raw)
    {
        // After first message, replace handlers with the real peer handler
        ws.OnMessage = _ => { };
        ws.OnBinary  = _ => { };

        try
        {
            var msg = JObject.Parse(raw);
            if (!ValidIdentity(msg)) { ws.Close(); return; }

            var devId = msg["device_id"]!.ToString();
            if (_peers.ContainsKey(devId)) { ws.Close(); return; }

            // Reply with our identity
            ws.Send(IdentityJson());

            var peer = BuildPeer(msg, ws.ConnectionInfo.ClientIpAddress);
            var conn = new PeerConnection(peer, null, ws);
            RegisterPeer(peer, conn);

            // Re-wire handlers to the registered peer
            ws.OnMessage = m    => HandleJson(peer, m);
            ws.OnBinary  = data => HandleBinary(peer, data);
            ws.OnClose   = ()   => RemovePeer(peer);
            ws.OnError   = _    => RemovePeer(peer);
        }
        catch { ws.Close(); }
    }


    public void ConnectToPeer(JObject hello)
    {
        var ip = hello["ip"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(ip)) return;
        if (_peers.Values.Any(p => p.Peer.Ip == ip)) return;
        if (_connecting.ContainsKey(ip)) return;
        _connecting[ip] = 0;
        _ = Task.Run(() => OutgoingConnectAsync(ip));
    }

    private async Task OutgoingConnectAsync(string ip)
    {
        try
        {
            var ws  = new ClientWebSocket();
            var uri = new Uri($"ws://{ip}:{Protocol.WsPort}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(uri, cts.Token);

            // Send our identity
            await WsSendText(ws, IdentityJson(), CancellationToken.None);

            // Wait for their identity
            var rawResp = await WsReceiveText(ws, TimeSpan.FromSeconds(5));
            if (rawResp == null) { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return; }

            var msg = JObject.Parse(rawResp);
            if (!ValidIdentity(msg)) { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return; }

            if (_peers.ContainsKey(msg["device_id"]!.ToString()))
            { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return; }

            var peer = BuildPeer(msg, ip);
            var conn = new PeerConnection(peer, ws, null);
            RegisterPeer(peer, conn);

            // Read loop
            _ = Task.Run(() => ClientReadLoopAsync(peer, conn, ws));
        }
        catch { }
        finally { _connecting.TryRemove(ip, out _); }
    }

    private async Task ClientReadLoopAsync(Peer peer, PeerConnection conn, ClientWebSocket ws)
    {
        var buf = new byte[1024 * 256];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var ms     = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) goto done;
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                    HandleJson(peer, Encoding.UTF8.GetString(data));
                else if (result.MessageType == WebSocketMessageType.Binary)
                    HandleBinary(peer, data);
            }
        }
        catch { }
        done:
        RemovePeer(peer);
    }


    private void HandleJson(Peer peer, string raw)
    {
        try
        {
            var msg = JObject.Parse(raw);
            var mid = msg["msg_id"]?.ToString();
            if (mid != null)
            {
                lock (_seenIds)
                {
                    if (_seenIds.Contains(mid)) return;
                    _seenIds.Add(mid);
                    _seenQueue.Enqueue(mid);
                    while (_seenQueue.Count > SeenMax)
                        _seenIds.Remove(_seenQueue.Dequeue());
                }
            }

            var type = msg["type"]?.ToString();
            switch (type)
            {
                case Protocol.MsgText:
                    OnTextMessage?.Invoke(peer, msg);
                    break;

                case Protocol.MsgVoiceJoin:
                    peer.InVoice      = true;
                    peer.VoiceChannel = msg["channel"]?.ToString() ?? "Голосовой";
                    OnVoiceState?.Invoke(peer);
                    break;

                case Protocol.MsgVoiceLeave:
                    peer.InVoice      = false;
                    peer.VoiceChannel = "";
                    OnVoiceState?.Invoke(peer);
                    break;

                case Protocol.MsgVoiceSpeaking:
                    peer.IsSpeaking = msg["speaking"]?.Value<bool>() ?? false;
                    OnVoiceState?.Invoke(peer);
                    break;

                case Protocol.MsgVoiceState:
                    peer.Muted    = msg["muted"]?.Value<bool>()    ?? false;
                    peer.Deafened = msg["deafened"]?.Value<bool>() ?? false;
                    OnVoiceState?.Invoke(peer);
                    break;

                case Protocol.MsgScreenStart:
                    peer.IsSharing = true;
                    OnScreenStateChange?.Invoke(peer);
                    break;

                case Protocol.MsgScreenStop:
                    peer.IsSharing = false;
                    OnScreenStateChange?.Invoke(peer);
                    break;

                case Protocol.MsgChannelAdd:
                    var netIdAdd = msg["network_id"]?.ToString() ?? "";
                    var chName   = msg["name"]?.ToString()       ?? "";
                    var chIsVc   = msg["is_voice"]?.Value<bool>() ?? true;
                    OnChannelAdd?.Invoke(netIdAdd, chName, chIsVc);
                    break;

                case Protocol.MsgChannelRemove:
                    var netIdRm  = msg["network_id"]?.ToString() ?? "";
                    var chRm     = msg["name"]?.ToString()       ?? "";
                    var chRmIsVc = msg["is_voice"]?.Value<bool>() ?? true;
                    OnChannelRemove?.Invoke(netIdRm, chRm, chRmIsVc);
                    break;

                case Protocol.MsgFileMeta:
                    OnFileMeta?.Invoke(peer, msg);
                    break;

                case Protocol.MsgFileChunk:
                    OnFileChunk?.Invoke(peer, msg);
                    break;

                case Protocol.MsgPing:
                    _ = SendToPeerAsync(peer, new { type = "pong" });
                    break;

                case Protocol.MsgPong:
                    if (_peers.TryGetValue(peer.DeviceId, out var pc) && pc.PingSentAt > 0)
                    {
                        var rtt = (int)((Stopwatch.GetTimestamp() - pc.PingSentAt) * 1000.0 / Stopwatch.Frequency);
                        peer.Ping = rtt;
                        OnPingUpdated?.Invoke(peer.DeviceId, rtt);
                    }
                    break;
            }
        }
        catch { }
    }

    private void HandleBinary(Peer peer, byte[] data)
    {
        if (data.Length < 1) return;
        var frameType = data[0];
        var payload   = data[1..];
        switch (frameType)
        {
            case Protocol.BinAudio:       OnVoiceFrame?.Invoke(peer, payload);       break;
            case Protocol.BinScreen:      OnScreenFrame?.Invoke(peer, payload);      break;
            case Protocol.BinStreamAudio: OnStreamAudioFrame?.Invoke(peer, payload); break;
        }
    }


    private async Task PingLoopAsync()
    {
        while (true)
        {
            await Task.Delay(5000);
            foreach (var (_, pc) in _peers)
            {
                pc.PingSentAt = Stopwatch.GetTimestamp();
                _ = SendToPeerAsync(pc.Peer, new { type = "ping" });
            }
        }
    }


    public Task BroadcastJsonAsync(object msg, string? networkId = null)
    {
        var obj = JObject.FromObject(msg);
        if (networkId != null) obj["network_id"] = networkId;
        if (!obj.ContainsKey("msg_id")) obj["msg_id"] = Guid.NewGuid().ToString();

        var mid = obj["msg_id"]!.ToString();
        lock (_seenIds)
        {
            _seenIds.Add(mid);
            _seenQueue.Enqueue(mid);
            while (_seenQueue.Count > SeenMax) _seenIds.Remove(_seenQueue.Dequeue());
        }

        var json  = obj.ToString(Formatting.None);
        var tasks = new List<Task>();
        foreach (var pc in _peers.Values)
        {
            if (networkId != null && pc.Peer.NetworkIds.Count > 0 &&
                !pc.Peer.NetworkIds.Contains(networkId)) continue;
            tasks.Add(pc.SendTextAsync(json));
        }
        return Task.WhenAll(tasks);
    }

    public Task BroadcastBinaryAsync(byte frameType, byte[] payload, string? networkId = null)
    {
        var data  = new byte[1 + payload.Length];
        data[0]   = frameType;
        Buffer.BlockCopy(payload, 0, data, 1, payload.Length);

        var tasks = new List<Task>();
        foreach (var pc in _peers.Values)
        {
            if (networkId != null && pc.Peer.NetworkIds.Count > 0 &&
                !pc.Peer.NetworkIds.Contains(networkId)) continue;
            tasks.Add(pc.SendBinaryAsync(data));
        }
        return Task.WhenAll(tasks);
    }


    public Task SendTextAsync(string channel, string content, string networkId)
    {
        var msg = new
        {
            type         = Protocol.MsgText,
            channel,
            content,
            from         = _config.DeviceId,
            nickname     = _config.Nickname,
            avatar_color = _config.AvatarColor,
            ts           = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            network_id   = networkId,
        };
        return BroadcastJsonAsync(msg, networkId);
    }

    public Task JoinVoiceAsync(string channel, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgVoiceJoin, channel }, networkId);

    public Task LeaveVoiceAsync(string channel, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgVoiceLeave, channel }, networkId);

    public Task SetSpeakingAsync(bool speaking, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgVoiceSpeaking, speaking }, networkId);

    public Task BroadcastVoiceStateAsync(bool muted, bool deafened, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgVoiceState, muted, deafened }, networkId);

    public Task StartScreenShareAsync(string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgScreenStart }, networkId);

    public Task StopScreenShareAsync(string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgScreenStop }, networkId);

    public Task SendFileMetaAsync(string fileId, string filename, long fileSize, int totalChunks,
        string channel, string networkId, string from, string nickname, string avatarColor)
    {
        var msg = new {
            type          = Protocol.MsgFileMeta,
            file_id       = fileId,
            filename,
            file_size     = fileSize,
            total_chunks  = totalChunks,
            channel,
            from,
            nickname,
            avatar_color  = avatarColor,
            ts            = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            network_id    = networkId,
        };
        return BroadcastJsonAsync(msg, networkId);
    }

    public Task SendFileChunkAsync(string fileId, int chunkIdx, string dataBase64, string networkId)
    {
        var msg = new { type = Protocol.MsgFileChunk, file_id = fileId, chunk_idx = chunkIdx, data = dataBase64 };
        return BroadcastJsonAsync(msg, networkId);
    }

    public Task AddVoiceChannelAsync(string name, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgChannelAdd, name, is_voice = true }, networkId);

    public Task AddTextChannelAsync(string name, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgChannelAdd, name, is_voice = false }, networkId);

    public Task RemoveVoiceChannelAsync(string name, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgChannelRemove, name, is_voice = true }, networkId);

    public Task RemoveTextChannelAsync(string name, string networkId)
        => BroadcastJsonAsync(new { type = Protocol.MsgChannelRemove, name, is_voice = false }, networkId);

    public Task BroadcastAudioAsync(byte[] payload, string networkId)
        => BroadcastBinaryAsync(Protocol.BinAudio, payload, networkId);

    public Task BroadcastStreamAudioAsync(byte[] payload, string networkId)
        => BroadcastBinaryAsync(Protocol.BinStreamAudio, payload, networkId);

    public Task BroadcastScreenAsync(byte[] payload, string networkId)
        => BroadcastBinaryAsync(Protocol.BinScreen, payload, networkId);


    private Task SendToPeerAsync(Peer peer, object msg)
    {
        if (!_peers.TryGetValue(peer.DeviceId, out var pc)) return Task.CompletedTask;
        return pc.SendTextAsync(JsonConvert.SerializeObject(msg));
    }

    private void RegisterPeer(Peer peer, PeerConnection conn)
    {
        if (_peers.TryAdd(peer.DeviceId, conn))
            OnPeerJoin?.Invoke(peer);
    }

    private void RemovePeer(Peer peer)
    {
        if (_peers.TryRemove(peer.DeviceId, out _))
            OnPeerLeave?.Invoke(peer);
    }

    private Peer BuildPeer(JObject msg, string ip)
    {
        var peerNetIds = (msg["network_ids"] as JArray)?.Select(t => t.ToString()).ToHashSet()
                         ?? new HashSet<string>();
        var myNetIds   = _config.Networks.Select(n => n.Id).ToHashSet();
        var shared     = peerNetIds.Intersect(myNetIds).ToHashSet();
        if (shared.Count == 0 && msg["network_id"] != null)
            shared.Add(msg["network_id"]!.ToString());
        if (shared.Count == 0) shared = peerNetIds;

        return new Peer
        {
            DeviceId    = msg["device_id"]!.ToString(),
            Nickname    = msg["nickname"]?.ToString()    ?? "?",
            AvatarColor = msg["avatar_color"]?.ToString() ?? "#7289da",
            Ip          = ip,
            NetworkIds  = shared,
        };
    }

    private bool ValidIdentity(JObject msg)
        => msg["type"]?.ToString() == Protocol.MsgIdentify
        && !string.IsNullOrEmpty(msg["device_id"]?.ToString())
        && msg["device_id"]!.ToString() != _config.DeviceId;

    private string IdentityJson()
    {
        var obj = new JObject
        {
            ["type"]        = Protocol.MsgIdentify,
            ["device_id"]   = _config.DeviceId,
            ["nickname"]    = _config.Nickname,
            ["avatar_color"]= _config.AvatarColor,
            ["network_ids"] = new JArray(_config.Networks.Select(n => n.Id)),
        };
        return obj.ToString(Formatting.None);
    }


    private static async Task WsSendText(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var buf = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(buf, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> WsReceiveText(ClientWebSocket ws, TimeSpan timeout)
    {
        var buf = new byte[1024 * 64];
        var ms  = new System.IO.MemoryStream();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch { return null; }
    }

    public MeshNetwork(AppConfig config) => _config = config;
}


public class PeerConnection
{
    public Peer                Peer       { get; }
    private ClientWebSocket?   _client;
    private IWebSocketConnection? _server;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public long PingSentAt { get; set; }

    public PeerConnection(Peer peer, ClientWebSocket? client, IWebSocketConnection? server)
    {
        Peer    = peer;
        _client = client;
        _server = server;
    }

    public async Task SendTextAsync(string text)
    {
        await _lock.WaitAsync();
        try
        {
            if (_client?.State == WebSocketState.Open)
                await _client.SendAsync(Encoding.UTF8.GetBytes(text),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            else
                _server?.Send(text);
        }
        catch { }
        finally { _lock.Release(); }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        await _lock.WaitAsync();
        try
        {
            if (_client?.State == WebSocketState.Open)
                await _client.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
            else
                _server?.Send(data);
        }
        catch { }
        finally { _lock.Release(); }
    }

    public void Close()
    {
        try { _client?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
        try { _server?.Close(); } catch { }
    }
}
