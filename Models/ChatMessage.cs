using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace RadCord.Models;

public class ChatMessage : INotifyPropertyChanged
{
    [JsonProperty("msg_id")]      public string MsgId      { get; set; } = Guid.NewGuid().ToString();
    [JsonProperty("type")]        public string Type       { get; set; } = "text";
    [JsonProperty("channel")]     public string Channel    { get; set; } = "general";
    [JsonProperty("content")]     public string Content    { get; set; } = "";
    [JsonProperty("from")]        public string FromId     { get; set; } = "";
    [JsonProperty("nickname")]    public string Nickname   { get; set; } = "";
    [JsonProperty("avatar_color")]public string AvatarColor{ get; set; } = "#7289da";
    [JsonProperty("ts")]          public long   Timestamp  { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    [JsonProperty("network_id")]  public string NetworkId  { get; set; } = "";

    // File transfer fields
    [JsonProperty("content_type")] public string ContentType { get; set; } = "text"; // text | image | file
    [JsonProperty("file_id")]      public string? FileId     { get; set; }
    [JsonProperty("file_name")]    public string? FileName   { get; set; }
    [JsonProperty("file_size")]    public long    FileSize   { get; set; }

    private double _progress;
    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set { _progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPct)));
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSizeDisplay))); }
    }

    private string? _imageData;
    [JsonProperty("image_data")]
    public string? ImageData
    {
        get => _imageData;
        set { _imageData = value;
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageData)));
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource))); }
    }

    [JsonIgnore] public bool   IsComplete   => ContentType == "text" || Progress >= 1.0 || !string.IsNullOrEmpty(ImageData);
    [JsonIgnore] public int    ProgressPct  => (int)(_progress * 100);
    [JsonIgnore] public string FileSizeDisplay => FileSize > 0
        ? FileSize >= 1048576 ? $"{FileSize / 1048576.0:0.0} МБ"
        : FileSize >= 1024    ? $"{FileSize / 1024.0:0.0} КБ"
        : $"{FileSize} Б"
        : "";

    [JsonIgnore]
    public BitmapSource? ImageSource
    {
        get
        {
            if (string.IsNullOrEmpty(_imageData)) return null;
            try
            {
                var bytes = Convert.FromBase64String(_imageData);
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    [JsonIgnore]
    public string TimeDisplay =>
        DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime.ToString("HH:mm");

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(Nickname) ? "?" : Nickname[0].ToString().ToUpper();

    [JsonIgnore]
    public bool IsSystem => FromId == "__system__";

    [JsonProperty("system_type")]
    public string? SystemType { get; set; }

    [JsonIgnore] public bool IsJoin       => SystemType == "join";
    [JsonIgnore] public bool IsLeave      => SystemType == "leave";
    [JsonIgnore] public bool IsTextMessage  => ContentType == "text" && !IsSystem;
    [JsonIgnore] public bool IsImageMessage => ContentType == "image";
    [JsonIgnore] public bool IsFileMessage  => ContentType == "file";

    [JsonIgnore]
    public bool IsSelf { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
