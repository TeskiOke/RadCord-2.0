using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RadCord.Core;

public class ProfileData
{
    [JsonProperty("nickname")]    public string  Nickname        { get; set; } = "";
    [JsonProperty("avatar_color")]public string  AvatarColor     { get; set; } = "#7289da";
    [JsonProperty("device_id")]   public string  DeviceId        { get; set; } = "";
    [JsonProperty("app_color")]   public string  AppColor        { get; set; } = "#5865f2";
    [JsonProperty("theme")]       public string  Theme           { get; set; } = "dark";
    [JsonProperty("audio_input_device")]  public string? AudioInputDevice  { get; set; }
    [JsonProperty("audio_output_device")] public string? AudioOutputDevice { get; set; }
    [JsonProperty("ptt_key")]             public string? PushToTalkKey     { get; set; }
    [JsonProperty("mic_mute_key")]        public string? MicMuteKey        { get; set; }
    [JsonProperty("deafen_key")]          public string? FullMuteKey       { get; set; }
    [JsonProperty("voice_mode")]          public string  VoiceMode         { get; set; } = "vad";
    [JsonProperty("vad_pct")]             public int     VadPct            { get; set; } = 15;
}

public class NetworkData
{
    [JsonProperty("name")]           public string       Name          { get; set; } = "";
    [JsonProperty("password")]       public string       Password      { get; set; } = "";
    [JsonProperty("id")]             public string       Id            { get; set; } = "";
    [JsonProperty("voice_channels")] public List<string> VoiceChannels { get; set; } = new();
    [JsonProperty("text_channels")]  public List<string> TextChannels  { get; set; } = new();
    [JsonProperty("display_name")]   public string?      DisplayName   { get; set; }

    [JsonIgnore] public string ShownName => DisplayName ?? Name;
}

public class AppConfig
{
    public static string ProfileDirectory { get; set; } =
        AppDomain.CurrentDomain.BaseDirectory;

    private static string ConfigPath =>
        Path.Combine(ProfileDirectory, "config.json");

    [JsonProperty("profile")]  public ProfileData     Profile  { get; set; } = new();
    [JsonProperty("networks")] public List<NetworkData> Networks { get; set; } = new();

    // Runtime helpers (not persisted)
    [JsonIgnore] public string DeviceId  => Profile.DeviceId;
    [JsonIgnore] public string Nickname  => Profile.Nickname;
    [JsonIgnore] public string AvatarColor => Profile.AvatarColor;


    public static AppConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var obj  = JObject.Parse(json);
            if (!obj.ContainsKey("profile")) return null;
            var cfg = obj.ToObject<AppConfig>();
            if (cfg != null)
            {
                foreach (var net in cfg.Networks)
                {
                    net.VoiceChannels = net.VoiceChannels.Distinct().ToList();
                    net.TextChannels  = net.TextChannels .Distinct().ToList();
                }
            }
            return cfg;
        }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(ProfileDirectory);
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(ConfigPath, json, Encoding.UTF8);
    }


    public static AppConfig Create(string nickname, string avatarColor)
    {
        var cfg = new AppConfig
        {
            Profile = new ProfileData
            {
                Nickname    = nickname.Trim(),
                AvatarColor = avatarColor,
                DeviceId    = Guid.NewGuid().ToString("N")[..16],
                AppColor    = "#7289da",
            }
        };
        cfg.Save();
        return cfg;
    }


    public static string MakeNetworkId(string name, string password)
    {
        var raw  = $"{name.Trim().ToLower()}:{password}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLower()[..32];
    }

    public NetworkData? AddNetwork(string name, string password)
    {
        var id = MakeNetworkId(name, password);
        if (Networks.Any(n => n.Name.ToLower() == name.Trim().ToLower()))
            return Networks.First(n => n.Name.ToLower() == name.Trim().ToLower());

        var net = new NetworkData
        {
            Name          = name.Trim(),
            Password      = password,
            Id            = id,
            VoiceChannels = new List<string> { "Голосовой" },
            TextChannels  = new List<string> { "general"   },
        };
        Networks.Add(net);
        Save();
        return net;
    }

    public void RemoveNetwork(string netId)
    {
        Networks.RemoveAll(n => n.Id == netId);
        Save();
    }

    public void AddVoiceChannel(string netId, string channelName)
    {
        var net = Networks.FirstOrDefault(n => n.Id == netId);
        if (net == null) return;
        if (!net.VoiceChannels.Contains(channelName))
            net.VoiceChannels.Add(channelName);
        Save();
    }

    public void RemoveVoiceChannel(string netId, string channelName)
    {
        var net = Networks.FirstOrDefault(n => n.Id == netId);
        net?.VoiceChannels.Remove(channelName);
        Save();
    }

    public void AddTextChannel(string netId, string channelName)
    {
        var net = Networks.FirstOrDefault(n => n.Id == netId);
        if (net == null) return;
        if (!net.TextChannels.Contains(channelName))
            net.TextChannels.Add(channelName);
        Save();
    }

    public void RemoveTextChannel(string netId, string channelName)
    {
        var net = Networks.FirstOrDefault(n => n.Id == netId);
        net?.TextChannels.Remove(channelName);
        Save();
    }

    public void RenameNetwork(string netId, string newName)
    {
        var net = Networks.FirstOrDefault(n => n.Id == netId);
        if (net == null) return;
        net.DisplayName = newName.Trim();
        Save();
    }
}
