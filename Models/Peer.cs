using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;

namespace RadCord.Models;

public partial class Peer : ObservableObject
{
    [ObservableProperty] private string       _deviceId   = "";
    [ObservableProperty] private string       _nickname   = "";
    [ObservableProperty] private string       _avatarColor = "#7289da";
    [ObservableProperty] private string       _ip         = "";
    [ObservableProperty] private bool         _inVoice;
    [ObservableProperty] private bool         _isSpeaking;
    [ObservableProperty] private bool         _muted;
    [ObservableProperty] private bool         _deafened;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingWatchButton), nameof(IsShowingStream),
        nameof(CanControlStream), nameof(CanShowStreamMuted), nameof(CanShowStreamUnmuted))]
    private bool _isSharing;
    [ObservableProperty] private int          _ping;
    [ObservableProperty] private string       _voiceChannel = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private float _volume = 1.0f;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowVoiceNotMuted), nameof(CanShowVoiceMuted))]
    private bool  _localMuted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowStreamMuted), nameof(CanShowStreamUnmuted))]
    private bool  _streamMuted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMuteVoice), nameof(CanControlStream),
        nameof(CanShowVoiceNotMuted), nameof(CanShowVoiceMuted),
        nameof(CanShowStreamMuted), nameof(CanShowStreamUnmuted))]
    private bool  _isSelf;
    [ObservableProperty] private double _streamVolume;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingWatchButton), nameof(IsShowingStream))]
    private BitmapSource? _streamFrameSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingWatchButton), nameof(IsShowingStream))]
    private bool _isWatching;

    [ObservableProperty] private double _cardWidth   = 220;
    [ObservableProperty] private double _cardHeight  = 135;
    [ObservableProperty] private double _cardOffsetX;
    [ObservableProperty] private double _cardOffsetY;

    public bool IsShowingWatchButton  => IsSharing && !IsWatching;
    public bool IsShowingStream       => IsSharing &&  IsWatching;

    public bool CanMuteVoice          => !IsSelf;
    public bool CanControlStream      => IsSharing && !IsSelf;
    public bool CanShowVoiceNotMuted  => !IsSelf && !LocalMuted;
    public bool CanShowVoiceMuted     => !IsSelf && LocalMuted;
    public bool CanShowStreamMuted    => IsSharing && !IsSelf && StreamMuted;
    public bool CanShowStreamUnmuted  => IsSharing && !IsSelf && !StreamMuted;

    public double VolumePercent
    {
        get => Volume * 100.0;
        set { Volume = (float)(value / 100.0); }
    }

    partial void OnIsSharingChanged(bool value)
    {
        if (!value) { IsWatching = false; StreamFrameSource = null; CardWidth = 220; CardHeight = 135; CardOffsetX = 0; CardOffsetY = 0; }
    }

    public HashSet<string> NetworkIds { get; set; } = new();

    // for display
    public void RefreshSpeaking() => OnPropertyChanged(nameof(IsSpeaking));

    public string Initial => string.IsNullOrEmpty(Nickname) ? "?" : Nickname[0].ToString().ToUpper();

    public string PingDisplay => Ping <= 0 ? "" : $"{Ping}ms";
}
