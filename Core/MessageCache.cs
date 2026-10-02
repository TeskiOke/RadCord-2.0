using System.IO;
using System.Text;
using Newtonsoft.Json;
using RadCord.Models;

namespace RadCord.Core;

public static class MessageCache
{
    private const int MaxMessages = 150;

    private static readonly string CacheDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

    private static string GetPath(string networkId, string channel)
    {
        var safe = new string(networkId.Where(char.IsLetterOrDigit).ToArray())[..Math.Min(16, networkId.Length)];
        return Path.Combine(CacheDir, $"net_{safe}_{channel}.json");
    }

    public static List<ChatMessage> Load(string networkId, string channel)
    {
        if (string.IsNullOrEmpty(networkId)) return new();
        var path = GetPath(networkId, channel);
        if (!File.Exists(path)) return new();
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(string networkId, string channel, ChatMessage msg)
    {
        if (string.IsNullOrEmpty(networkId)) return;
        if (msg.FromId == "__system__") return;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var msgs = Load(networkId, channel);

            // Dedup by MsgId
            if (!string.IsNullOrEmpty(msg.MsgId) && msgs.Any(m => m.MsgId == msg.MsgId))
                return;

            msgs.Add(msg);
            if (msgs.Count > MaxMessages)
                msgs = msgs.TakeLast(MaxMessages).ToList();

            var json = JsonConvert.SerializeObject(msgs, Formatting.None);
            File.WriteAllText(GetPath(networkId, channel), json, Encoding.UTF8);
        }
        catch { /* best effort */ }
    }

    public static void ClearNetwork(string networkId)
    {
        if (!Directory.Exists(CacheDir)) return;
        var safe = new string(networkId.Where(char.IsLetterOrDigit).ToArray())[..Math.Min(16, networkId.Length)];
        foreach (var f in Directory.GetFiles(CacheDir, $"net_{safe}_*.json"))
        {
            try { File.Delete(f); } catch { }
        }
    }
}
