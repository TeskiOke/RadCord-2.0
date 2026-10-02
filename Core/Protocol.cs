namespace RadCord.Core;

public static class Protocol
{
    // JSON message types
    public const string MsgText          = "text";
    public const string MsgVoiceJoin     = "voice_join";
    public const string MsgVoiceLeave    = "voice_leave";
    public const string MsgVoiceSpeaking = "voice_speaking";
    public const string MsgScreenStart   = "screen_start";
    public const string MsgScreenStop    = "screen_stop";
    public const string MsgIdentify      = "identify";
    public const string MsgVoiceState    = "voice_state";
    public const string MsgPing          = "ping";
    public const string MsgPong          = "pong";
    public const string MsgChannelAdd    = "channel_add";
    public const string MsgChannelRemove = "channel_remove";
    public const string MsgFileMeta      = "file_meta";
    public const string MsgFileChunk     = "file_chunk";
    public const int    FileChunkSize    = 32768; // 32 KB

    // Binary frame type prefixes
    public const byte BinAudio       = 0x01;
    public const byte BinScreen      = 0x02;
    public const byte BinStreamAudio = 0x03;

    // Network
    public const int    WsPort             = 8765;
    public const int    UdpDiscoveryPort   = 8766;
    public const double UdpInterval        = 5.0;
    public const string AppVersion         = "1.0";
    public static readonly string[] UdpBroadcastAddrs = { "26.255.255.255", "255.255.255.255" };

    // Audio — must match Python side
    public const int   AudioSampleRate   = 48000;
    public const int   AudioChannels     = 1;
    public const int   AudioChunkFrames  = 960;    // 20ms @ 48kHz
    public const float AudioRmsThreshold = 400f;

    // Screen share defaults
    public const int ScreenFps         = 15;
    public const int ScreenWidth       = 1280;
    public const int ScreenHeight      = 720;
    public const int ScreenJpegQuality = 60;

    public static readonly string[] AvatarColors =
    {
        "#7289da", "#43b581", "#faa61a", "#f04747",
        "#593695", "#1abc9c", "#e91e63", "#ff9800",
    };

    public static readonly (string Label, int W, int H, int Q)[] QualityPresets =
    {
        ("360p  — Низкое",   640,  360, 35),
        ("480p  — Среднее",  854,  480, 50),
        ("720p  — Высокое",  1280, 720, 65),
        ("1080p — Отличное", 1920, 1080, 82),
    };

    public static readonly int[] FpsOptions = { 15, 20, 24, 30, 45, 60 };
}
