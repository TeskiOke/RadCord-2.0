using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json.Linq;
using RadCord.Core;
using RadCord.Models;

namespace RadCord.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public  AppConfig     Config  { get; }
    public  MeshNetwork   Mesh    { get; }
    public  AudioManager  Audio   { get; }
    public  SoundService  Sound   { get; private set; } = null!;
    private readonly PeerDiscovery _discovery;
    private ScreenCapture _screen = new();
    private bool _suppressMuteSound;
    private readonly HashSet<string> _sharingPeerIds = new();

    [ObservableProperty] private ObservableCollection<NetworkInfo> _networks = new();
    [ObservableProperty] private NetworkInfo?                      _selectedNetwork;
    [ObservableProperty] private ObservableCollection<Peer>        _peers    = new();
    [ObservableProperty] private ObservableCollection<ChatMessage> _messages = new();
    [ObservableProperty] private ObservableCollection<string>      _textChannels  = new();
    [ObservableProperty] private ObservableCollection<string>           _voiceChannels  = new();
    [ObservableProperty] private ObservableCollection<Peer>             _voicePeers     = new();
    [ObservableProperty] private bool                                   _isViewingVoice;
    public ObservableCollection<VoiceChannelEntry> VoiceChannelEntries { get; } = new();

    [ObservableProperty] private string  _selectedTextChannel  = "general";
    [ObservableProperty] private string? _currentVoiceChannel;
    [ObservableProperty] private string  _messageInput         = "";
    [ObservableProperty] private bool    _isMuted;
    [ObservableProperty] private bool    _isDeafened;
    [ObservableProperty] private bool    _isInVoice;
    [ObservableProperty] private bool    _isSpeaking;
    [ObservableProperty] private bool    _isSharing;
    [ObservableProperty] private float   _rmsLevel;
    [ObservableProperty] private string  _statusText = "Не подключён";

    // File transfer state
    private readonly Dictionary<string, byte[][]>                     _incomingChunks  = new();
    private readonly Dictionary<string, (int total, int recv, string fname, string ch)> _incomingMeta = new();
    private readonly Dictionary<string, ChatMessage>                  _incomingMsg     = new();

    public event Action<string, byte[]>? ScreenFrameReceived;
    public event Action<string>?          PeerStreamStopped;  // deviceId   // deviceId, jpeg
    public event Action<string>?         ChannelAddedByPeer;
    public event Action<string>?         ChannelRemovedByPeer;

    public string SelfDeviceId   => Config.DeviceId;
    public string SelfNickname   => Config.Nickname;
    public string SelfAvatarColor => Config.AvatarColor;

    public void RefreshSelfInfo()
    {
        OnPropertyChanged(nameof(SelfNickname));
        OnPropertyChanged(nameof(SelfAvatarColor));
        RefreshSelfPeer();
    }


    public MainViewModel(AppConfig config)
    {
        Config = config;
        Mesh       = new MeshNetwork(config);
        Audio      = new AudioManager();
        Audio.VadThreshold = Config.Profile.VadPct * 20f;
        Sound      = new SoundService(System.IO.Path.Combine(AppContext.BaseDirectory, "sound"));
        _discovery = new PeerDiscovery(config, peer => Mesh.ConnectToPeer(peer));

        LoadNetworks();
        WireCallbacks();
        StartServices();
    }

    private void LoadNetworks()
    {
        foreach (var n in Config.Networks)
            Networks.Add(NetworkInfo.FromConfig(n));

        // Do not auto-select: user picks a server from the bar
    }


    partial void OnSelectedNetworkChanged(NetworkInfo? value)
    {
        if (value == null) return;
        LoadChannelsForNetwork(value.Id);
        LoadMessagesForChannel(value.Id, SelectedTextChannel);
        EnsureSelfInPeers();
        RefreshPeerList();
    }

    partial void OnSelectedTextChannelChanged(string value)
    {
        if (SelectedNetwork == null) return;
        LoadMessagesForChannel(SelectedNetwork.Id, value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_suppressMuteSound)
            Sound.Play("звук мута_размута_полногомута_полногоразмута.mp3");
        Audio.Muted = value;
        var selfVp = VoicePeers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
        if (selfVp != null) selfVp.Muted = value;
        RefreshVoiceChannelCounts();
        if (SelectedNetwork != null && IsInVoice)
            _ = Mesh.BroadcastVoiceStateAsync(value, IsDeafened, SelectedNetwork.Id);
    }

    partial void OnIsDeafenedChanged(bool value)
    {
        Sound.Play("звук мута_размута_полногомута_полногоразмута.mp3");
        Audio.Deafened = value;
        if (value && !IsMuted) { _suppressMuteSound = true; IsMuted = true; _suppressMuteSound = false; }
        var selfVp = VoicePeers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
        if (selfVp != null) { selfVp.Deafened = value; selfVp.Muted = IsMuted; }
        RefreshVoiceChannelCounts();
        if (SelectedNetwork != null && IsInVoice)
            _ = Mesh.BroadcastVoiceStateAsync(IsMuted, value, SelectedNetwork.Id);
    }

    partial void OnIsSpeakingChanged(bool value)
    {
        RefreshVoiceChannelCounts();
    }

    partial void OnIsSharingChanged(bool value)
    {
        var selfVp = VoicePeers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
        if (selfVp != null) selfVp.IsSharing = value;
    }


    private void StartServices()
    {
        try { Mesh.Start(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Mesh] Start failed: {ex.Message}");
            System.IO.File.AppendAllText("crash.log", $"[Mesh] {ex}\n");
        }
        try { _discovery.Start(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Discovery] Start failed: {ex.Message}");
        }
        try { Audio.StartStreams(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Start failed: {ex.Message}");
            System.IO.File.AppendAllText("crash.log", $"[Audio] {ex}\n");
        }
    }

    public void Shutdown()
    {
        if (IsInVoice && SelectedNetwork != null)
            _ = Mesh.LeaveVoiceAsync(CurrentVoiceChannel!, SelectedNetwork.Id);
        Audio.Dispose();
        Mesh.Stop();
        _discovery.Stop();
    }


    private void WireCallbacks()
    {
        Mesh.OnPeerJoin = peer => App.Current.Dispatcher.Invoke(() =>
        {
            if (!Peers.Any(p => p.DeviceId == peer.DeviceId))
            {
                Peers.Add(peer);
                AddSystemMessage($"{peer.Nickname} подключился к сети", "join");
                Sound.Play("заходит в онлайн.mp3");
            }
            UpdateNetworkCounts();
            // Синхронизируем своё состояние ГК для нового пира
            if (IsInVoice && SelectedNetwork != null)
            {
                _ = Mesh.JoinVoiceAsync(CurrentVoiceChannel!, SelectedNetwork.Id);
                _ = Mesh.BroadcastVoiceStateAsync(IsMuted, IsDeafened, SelectedNetwork.Id);
            }
        });

        Mesh.OnPeerLeave = peer => App.Current.Dispatcher.Invoke(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
            if (existing != null) Peers.Remove(existing);

            var vp = VoicePeers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
            if (vp != null) VoicePeers.Remove(vp);

            Audio.RemovePeer(peer.DeviceId);
            PeerStreamStopped?.Invoke(peer.DeviceId);  // close stream viewer if open
            AddSystemMessage($"{peer.Nickname} отключился от сети", "leave");
            Sound.Play("выходит из сети.mp3");
            UpdateNetworkCounts();
        });

        Mesh.OnTextMessage = (peer, msg) => App.Current.Dispatcher.Invoke(() =>
        {
            var netId = msg["network_id"]?.ToString() ?? "";
            if (SelectedNetwork?.Id != netId) return;
            var ch = msg["channel"]?.ToString() ?? "general";
            if (ch != SelectedTextChannel)
                Sound.Play("новое сообщение.mp3");

            var cm = new ChatMessage
            {
                MsgId       = msg["msg_id"]?.ToString() ?? Guid.NewGuid().ToString(),
                Channel     = ch,
                Content     = msg["content"]?.ToString() ?? "",
                FromId      = msg["from"]?.ToString()    ?? "",
                Nickname    = msg["nickname"]?.ToString() ?? peer.Nickname,
                AvatarColor = msg["avatar_color"]?.ToString() ?? peer.AvatarColor,
                Timestamp   = msg["ts"]?.Value<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                NetworkId   = netId,
            };
            cm.IsSelf = (cm.FromId == Config.DeviceId);
            MessageCache.Save(netId, ch, cm);   // всегда кэшируем
            if (ch == SelectedTextChannel)
                Messages.Add(cm);               // показываем только текущий канал
        });

        Mesh.OnVoiceFrame = (peer, data) =>
        {
            if (!Audio.InVoice) return;
            Audio.ReceivePeerAudio(peer.DeviceId, data);
        };

        Mesh.OnScreenFrame = (peer, data) =>
            ScreenFrameReceived?.Invoke(peer.DeviceId, data);

        Mesh.OnStreamAudioFrame = (peer, data) =>
            Audio.ReceivePeerStreamAudio(peer.DeviceId, data);

        Mesh.OnVoiceState = peer => App.Current.Dispatcher.Invoke(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
            if (existing != null)
            {
                // peer.InVoice already updated — use VoicePeers membership as old-state indicator
                var wasInVoice = VoicePeers.Any(p => p.DeviceId == peer.DeviceId);
                if (peer.InVoice && !wasInVoice)   Sound.Play("заходит в гк.mp3");
                else if (!peer.InVoice && wasInVoice) Sound.Play("выходит из гк.mp3");
                existing.InVoice      = peer.InVoice;
                existing.IsSpeaking   = peer.IsSpeaking;
                existing.Muted        = peer.Muted;
                existing.Deafened     = peer.Deafened;
                existing.VoiceChannel = peer.VoiceChannel;
                // Force UI-thread notification — IsSpeaking was set on network thread
                var vp0 = VoicePeers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
                vp0?.RefreshSpeaking();
            }

            // VoiceGrid management
            if (SelectedNetwork != null && peer.NetworkIds.Contains(SelectedNetwork.Id))
            {
                var vp = VoicePeers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
                if (peer.InVoice && vp == null)
                    VoicePeers.Add(existing ?? peer);
                else if (!peer.InVoice && vp != null)
                    VoicePeers.Remove(vp);
                else if (vp != null)
                {
                    vp.IsSpeaking = peer.IsSpeaking;
                    vp.Muted      = peer.Muted;
                    vp.Deafened   = peer.Deafened;
                }
            }
            RefreshVoiceChannelCounts();
        });

        Mesh.OnScreenStateChange = peer => App.Current.Dispatcher.Invoke(() =>
        {
            // peer.IsSharing already updated — use separate set as old-state indicator
            var wasSharing = _sharingPeerIds.Contains(peer.DeviceId);
            if (peer.IsSharing && !wasSharing)   Sound.Play("дают стрим.mp3");
            else if (!peer.IsSharing && wasSharing) Sound.Play("отключение стрима.mp3");
            if (peer.IsSharing) _sharingPeerIds.Add(peer.DeviceId);
            else                _sharingPeerIds.Remove(peer.DeviceId);

            var existing = Peers.FirstOrDefault(p => p.DeviceId == peer.DeviceId);
            if (existing != null) existing.IsSharing = peer.IsSharing;
            if (!peer.IsSharing)
                PeerStreamStopped?.Invoke(peer.DeviceId);
        });

        Mesh.OnChannelAdd = (netId, name, isVoice) => App.Current.Dispatcher.Invoke(() =>
        {
            var cfg = Config.Networks.FirstOrDefault(n => n.Id == netId);
            if (cfg == null) return;
            if (isVoice)
            {
                if (cfg.VoiceChannels.Contains(name)) return;
                cfg.VoiceChannels.Add(name);
                Config.Save();
                if (SelectedNetwork?.Id == netId && !VoiceChannels.Contains(name))
                {
                    VoiceChannels.Add(name);
                    VoiceChannelEntries.Add(new VoiceChannelEntry(name));
                }
            }
            else
            {
                if (cfg.TextChannels.Contains(name)) return;
                cfg.TextChannels.Add(name);
                Config.Save();
                if (SelectedNetwork?.Id == netId && !TextChannels.Contains(name))
                    TextChannels.Add(name);
            }
            ChannelAddedByPeer?.Invoke(name);
        });

        Mesh.OnChannelRemove = (netId, name, isVoice) => App.Current.Dispatcher.Invoke(() =>
        {
            var cfg = Config.Networks.FirstOrDefault(n => n.Id == netId);
            if (cfg == null) return;
            if (isVoice) { cfg.VoiceChannels.Remove(name); }
            else         { cfg.TextChannels.Remove(name); }
            Config.Save();
            if (SelectedNetwork?.Id == netId)
            {
                if (isVoice)
                {
                    VoiceChannels.Remove(name);
                    var e2 = VoiceChannelEntries.FirstOrDefault(e => e.Name == name);
                    if (e2 != null) VoiceChannelEntries.Remove(e2);
                }
                else TextChannels.Remove(name);
            }
            ChannelRemovedByPeer?.Invoke(name);
        });

        Mesh.OnPingUpdated = (deviceId, ms) => App.Current.Dispatcher.Invoke(() =>
        {
            var peer = Peers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer != null) peer.Ping = ms;
        });

        Mesh.OnFileMeta = (peer, msg) => App.Current.Dispatcher.Invoke(() =>
        {
            if (SelectedNetwork?.Id != msg["network_id"]?.ToString()) return;
            var ch          = msg["channel"]?.ToString()          ?? SelectedTextChannel;
            var fileId      = msg["file_id"]?.ToString()          ?? "";
            var filename    = msg["filename"]?.ToString()         ?? "file";
            var fileSize    = msg["file_size"]?.Value<long>()     ?? 0;
            var totalChunks = msg["total_chunks"]?.Value<int>()   ?? 1;
            var ext = System.IO.Path.GetExtension(filename).ToLower();
            var contentType = new[]{"jpg","jpeg","png","gif","webp","bmp"}.Contains(ext.TrimStart('.')) ? "image" : "file";

            _incomingChunks[fileId] = new byte[totalChunks][];
            _incomingMeta[fileId]   = (totalChunks, 0, filename, ch);

            var cm = new ChatMessage
            {
                MsgId       = fileId,
                Channel     = ch,
                ContentType = contentType,
                FileId      = fileId,
                FileName    = filename,
                FileSize    = fileSize,
                Progress    = 0,
                IsSelf      = false,
                FromId      = msg["from"]?.ToString()          ?? peer.DeviceId,
                Nickname    = msg["nickname"]?.ToString()       ?? peer.Nickname,
                AvatarColor = msg["avatar_color"]?.ToString()   ?? peer.AvatarColor,
                Timestamp   = msg["ts"]?.Value<long>()          ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                NetworkId   = SelectedNetwork.Id,
            };
            _incomingMsg[fileId] = cm;
            if (ch == SelectedTextChannel) Messages.Add(cm);
        });

        Mesh.OnFileChunk = (peer, msg) => App.Current.Dispatcher.Invoke(() =>
        {
            var fileId   = msg["file_id"]?.ToString()   ?? "";
            var chunkIdx = msg["chunk_idx"]?.Value<int>() ?? 0;
            var data     = msg["data"]?.ToString()       ?? "";
            if (!_incomingChunks.TryGetValue(fileId, out var chunks)) return;
            try { chunks[chunkIdx] = Convert.FromBase64String(data); } catch { return; }

            if (!_incomingMeta.TryGetValue(fileId, out var meta)) return;
            int received = chunks.Count(c => c != null);
            _incomingMeta[fileId] = (meta.total, received, meta.fname, meta.ch);

            if (_incomingMsg.TryGetValue(fileId, out var cm))
                cm.Progress = (double)received / meta.total;

            if (received >= meta.total)
            {
                var allBytes = chunks.SelectMany(c => c).ToArray();
                if (_incomingMsg.TryGetValue(fileId, out var cm2))
                {
                    cm2.ImageData = Convert.ToBase64String(allBytes);
                    cm2.Progress  = 1.0;
                    MessageCache.Save(cm2.NetworkId, cm2.Channel, cm2);
                }
                _incomingChunks.Remove(fileId);
                _incomingMeta.Remove(fileId);
            }
        });

        // Audio callbacks
        Audio.OnAudioFrame = async bytes =>
        {
            if (!IsInVoice || SelectedNetwork == null) return;
            await Mesh.BroadcastAudioAsync(bytes, SelectedNetwork.Id);
        };

        Audio.OnSpeakingChange = speaking =>
        {
            // Must update ObservableCollections on UI thread
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                IsSpeaking = speaking;
                var selfVp = VoicePeers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
                if (selfVp != null) selfVp.IsSpeaking = speaking;
            });
            if (SelectedNetwork != null && IsInVoice)
                _ = Mesh.SetSpeakingAsync(speaking, SelectedNetwork.Id);
        };

        Audio.OnRmsLevel = v => App.Current.Dispatcher.Invoke(() => RmsLevel = v);

        Audio.OnStreamAudioReady = async bytes =>
        {
            if (!IsSharing || SelectedNetwork == null) return;
            await Mesh.BroadcastStreamAudioAsync(bytes, SelectedNetwork.Id);
        };

    }



    [RelayCommand]
    public async Task SendMessageAsync()
    {
        var text = MessageInput.Trim();
        if (string.IsNullOrEmpty(text) || SelectedNetwork == null) return;
        MessageInput = "";

        var msg = new ChatMessage
        {
            Channel     = SelectedTextChannel,
            Content     = text,
            FromId      = Config.DeviceId,
            Nickname    = Config.Nickname,
            AvatarColor = Config.AvatarColor,
            NetworkId   = SelectedNetwork.Id,
            IsSelf      = true,
        };
        Messages.Add(msg);
        MessageCache.Save(SelectedNetwork.Id, SelectedTextChannel, msg);

        await Mesh.SendTextAsync(SelectedTextChannel, text, SelectedNetwork.Id);
    }

    [RelayCommand]
    public async Task JoinVoiceAsync(string channelName)
    {
        if (SelectedNetwork == null) return;

        // Leave current if any
        if (IsInVoice && CurrentVoiceChannel != null)
            await LeaveVoiceAsync();

        CurrentVoiceChannel = channelName;
        IsInVoice           = true;
        IsViewingVoice      = true;
        Audio.InVoice       = true;
        StatusText          = $"В канале: {channelName}";

        // Add self to voice peers grid
        var self = new Peer
        {
            DeviceId     = Config.DeviceId,
            Nickname     = Config.Nickname,
            AvatarColor  = Config.AvatarColor,
            InVoice      = true,
            VoiceChannel = channelName,
            IsSelf       = true,
        };
        if (!VoicePeers.Any(p => p.DeviceId == Config.DeviceId))
            VoicePeers.Add(self);

        await Mesh.JoinVoiceAsync(channelName, SelectedNetwork.Id);
        RefreshVoiceChannelCounts();
    }

    [RelayCommand]
    public async Task LeaveVoiceAsync()
    {
        if (!IsInVoice || SelectedNetwork == null) return;

        var ch = CurrentVoiceChannel ?? "";
        await Mesh.LeaveVoiceAsync(ch, SelectedNetwork.Id);

        IsInVoice           = false;
        IsViewingVoice      = false;
        Audio.InVoice       = false;
        CurrentVoiceChannel = null;
        IsSpeaking          = false;
        StatusText          = "Не подключён";

        // Remove self from voice grid
        var self = VoicePeers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
        if (self != null) VoicePeers.Remove(self);

        if (IsSharing) await StopSharingAsync();
        RefreshVoiceChannelCounts();
    }

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    public void ToggleDeafen() => IsDeafened = !IsDeafened;

    [RelayCommand]
    public void ToggleLocalMute(string deviceId)
    {
        if (Audio.IsLocallyMuted(deviceId))
            Audio.UnmuteLocal(deviceId);
        else
            Audio.MuteLocal(deviceId);
        var peer = VoicePeers.FirstOrDefault(p => p.DeviceId == deviceId);
        if (peer != null) peer.LocalMuted = Audio.IsLocallyMuted(deviceId);
    }

    [RelayCommand]
    public void SetPeerVolume(object? param)
    {
        if (param is not object?[] arr || arr.Length < 2) return;
        if (arr[0] is string deviceId && arr[1] is double vol)
            Audio.SetPeerVolume(deviceId, (float)vol);
    }

    [RelayCommand]
    public async Task StartSharingAsync(ScreenCaptureSource source)
    {
        if (!IsInVoice || SelectedNetwork == null) return;
        IsSharing = true;
        _screen.Configure(source);
        _screen.OnFrame = async bytes =>
        {
            if (!IsSharing || SelectedNetwork == null) return;
            await Mesh.BroadcastScreenAsync(bytes, SelectedNetwork.Id);
            ScreenFrameReceived?.Invoke(Config.DeviceId, bytes); // селф-превью своего стрима
        };
        _screen.Start();
        if (source.CaptureSystemAudio)
            Audio.StartSystemAudio();
        await Mesh.StartScreenShareAsync(SelectedNetwork.Id);
    }

    public async Task SendFileAsync(string filePath)
    {
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
            await SendFileBytesAsync(System.IO.Path.GetFileName(filePath), bytes);
        }
        catch { }
    }

    public async Task SendFileBytesAsync(string filename, byte[] bytes)
    {
        if (SelectedNetwork == null || SelectedTextChannel == null) return;
        var fileId      = Guid.NewGuid().ToString();
        var chunkSize   = Protocol.FileChunkSize;
        var totalChunks = (bytes.Length + chunkSize - 1) / chunkSize;
        var ext         = System.IO.Path.GetExtension(filename).ToLower();
        var contentType = new[]{"jpg","jpeg","png","gif","webp","bmp"}.Contains(ext.TrimStart('.')) ? "image" : "file";

        var cm = new ChatMessage
        {
            MsgId       = fileId,
            Channel     = SelectedTextChannel,
            ContentType = contentType,
            FileId      = fileId,
            FileName    = filename,
            FileSize    = bytes.Length,
            IsSelf      = true,
            Progress    = 1.0,
            FromId      = Config.DeviceId,
            Nickname    = Config.Nickname,
            AvatarColor = Config.AvatarColor,
            Timestamp   = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NetworkId   = SelectedNetwork.Id,
        };
        if (contentType == "image")
            cm.ImageData = Convert.ToBase64String(bytes);
        else
            cm.ImageData = Convert.ToBase64String(bytes); // store for download
        Messages.Add(cm);
        MessageCache.Save(SelectedNetwork.Id, SelectedTextChannel, cm);

        await Mesh.SendFileMetaAsync(fileId, filename, bytes.Length, totalChunks,
            SelectedTextChannel, SelectedNetwork.Id, Config.DeviceId, Config.Nickname, Config.AvatarColor);

        for (int i = 0; i < totalChunks; i++)
        {
            var start  = i * chunkSize;
            var length = Math.Min(chunkSize, bytes.Length - start);
            var chunk  = new byte[length];
            Buffer.BlockCopy(bytes, start, chunk, 0, length);
            await Mesh.SendFileChunkAsync(fileId, i, Convert.ToBase64String(chunk), SelectedNetwork.Id);
            await Task.Delay(5);
        }
    }

    [RelayCommand]
    public async Task StopSharingAsync()
    {
        IsSharing = false;
        _screen.Stop();
        Audio.StopSystemAudio();
        if (SelectedNetwork != null)
            await Mesh.StopScreenShareAsync(SelectedNetwork.Id);
    }

    [RelayCommand]
    public void SelectNetwork(NetworkInfo? net)
    {
        if (net == null || net == SelectedNetwork) return;
        VoicePeers.Clear();
        SelectedNetwork = net;
    }

    [RelayCommand]
    public void AddNetwork(NetworkData netData)
    {
        var ni = NetworkInfo.FromConfig(netData);
        if (!Networks.Any(n => n.Id == ni.Id))
            Networks.Add(ni);
        SelectedNetwork = Networks.FirstOrDefault(n => n.Id == ni.Id);
    }

    [RelayCommand]
    public void RemoveNetwork(NetworkInfo net)
    {
        Config.RemoveNetwork(net.Id);
        MessageCache.ClearNetwork(net.Id);
        Networks.Remove(net);
        if (SelectedNetwork?.Id == net.Id)
            SelectedNetwork = Networks.FirstOrDefault();
    }

    [RelayCommand]
    public async Task AddVoiceChannelAsync(string name)
    {
        if (SelectedNetwork == null || string.IsNullOrWhiteSpace(name)) return;
        if (VoiceChannels.Contains(name)) return;
        VoiceChannels.Add(name);
        VoiceChannelEntries.Add(new VoiceChannelEntry(name));
        Config.AddVoiceChannel(SelectedNetwork.Id, name);
        await Mesh.AddVoiceChannelAsync(name, SelectedNetwork.Id);
    }

    [RelayCommand]
    public async Task RemoveVoiceChannelAsync(string name)
    {
        if (SelectedNetwork == null) return;
        if (IsInVoice && CurrentVoiceChannel == name)
            await LeaveVoiceAsync();
        VoiceChannels.Remove(name);
        var entry = VoiceChannelEntries.FirstOrDefault(e => e.Name == name);
        if (entry != null) VoiceChannelEntries.Remove(entry);
        Config.RemoveVoiceChannel(SelectedNetwork.Id, name);
        await Mesh.RemoveVoiceChannelAsync(name, SelectedNetwork.Id);
    }


    private void LoadChannelsForNetwork(string netId)
    {
        VoiceChannels.Clear();
        VoiceChannelEntries.Clear();
        TextChannels.Clear();
        var net = Config.Networks.FirstOrDefault(n => n.Id == netId);
        if (net == null) return;
        foreach (var ch in net.VoiceChannels.Distinct())
        {
            VoiceChannels.Add(ch);
            VoiceChannelEntries.Add(new VoiceChannelEntry(ch));
        }
        foreach (var ch in net.TextChannels.Distinct())
            TextChannels.Add(ch);
        if (TextChannels.Count == 0) TextChannels.Add("general");
        RefreshVoiceChannelCounts();
    }

    private void RefreshVoiceChannelCounts()
    {
        foreach (var entry in VoiceChannelEntries)
        {
            entry.Participants.Clear();

            // Self
            if (IsInVoice && CurrentVoiceChannel == entry.Name)
            {
                var self = new Peer
                {
                    DeviceId    = Config.DeviceId,
                    Nickname    = Config.Nickname,
                    AvatarColor = Config.AvatarColor,
                    InVoice     = true,
                    IsSpeaking  = IsSpeaking,
                    Muted       = IsMuted,
                    Deafened    = IsDeafened,
                    IsSelf      = true,
                };
                entry.Participants.Add(self);
            }

            // Remote peers
            foreach (var p in Peers.Where(p => p.InVoice && p.VoiceChannel == entry.Name))
                entry.Participants.Add(p);

            entry.Count = entry.Participants.Count;
        }
    }

    private void LoadMessagesForChannel(string netId, string channel)
    {
        Messages.Clear();
        var cached = MessageCache.Load(netId, channel);
        foreach (var m in cached)
        {
            m.IsSelf = (m.FromId == Config.DeviceId);
            Messages.Add(m);
        }
    }

    private void RefreshPeerList()
    {
        VoicePeers.Clear();
        if (SelectedNetwork == null) return;
        foreach (var peer in Peers)
        {
            if (peer.InVoice && peer.NetworkIds.Contains(SelectedNetwork.Id))
                VoicePeers.Add(peer);
        }
    }

    private void EnsureSelfInPeers()
    {
        if (Peers.Any(p => p.DeviceId == Config.DeviceId)) return;
        Peers.Insert(0, new Peer
        {
            DeviceId    = Config.DeviceId,
            Nickname    = Config.Nickname,
            AvatarColor = Config.AvatarColor,
        });
    }

    public void RefreshSelfPeer()
    {
        var self = Peers.FirstOrDefault(p => p.DeviceId == Config.DeviceId);
        if (self != null)
        {
            self.Nickname    = Config.Nickname;
            self.AvatarColor = Config.AvatarColor;
        }
    }

    private void AddSystemMessage(string text, string? systemType = null)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (SelectedNetwork == null) return;
            var channel = SelectedTextChannel ?? "general";
            var cm = new ChatMessage
            {
                FromId     = "__system__",
                Nickname   = "Система",
                Content    = text,
                Channel    = channel,
                NetworkId  = SelectedNetwork.Id,
                SystemType = systemType,
            };
            MessageCache.Save(SelectedNetwork.Id, channel, cm);
            Messages.Add(cm);
        });
    }

    private void UpdateNetworkCounts()
    {
        foreach (var net in Networks)
        {
            net.OnlineCount = Peers.Count(p => p.NetworkIds.Contains(net.Id));
        }
    }
}
