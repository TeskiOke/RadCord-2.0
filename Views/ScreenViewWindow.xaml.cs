using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RadCord.Core;
using RadCord.Models;

namespace RadCord.Views;

public partial class ScreenViewWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
    }

    private readonly Peer         _peer;
    private readonly AudioManager _audio;
    private bool _hasFrame;

    private bool _muted;

    public ScreenViewWindow(Peer peer, AudioManager audio, bool mini = false)
    {
        _peer  = peer;
        _audio = audio;
        InitializeComponent();

        Title           = $"Экран: {peer.Nickname}";
        TitleLabel.Text = $"\uD83D\uDDB5  {peer.Nickname}";

        _audio.SetPeerStreamVolume(peer.DeviceId, 1.0f);
        VolumeSlider.Value = 100;

        if (!mini)
        {
            Width  = 800;
            Height = 500;
        }
    }

    public void OnFrame(string deviceId, byte[] jpeg)
    {
        if (deviceId != _peer.DeviceId) return;
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(jpeg);
                bmp.BeginInit();
                bmp.CacheOption   = BitmapCacheOption.OnLoad;
                bmp.StreamSource  = ms;
                bmp.EndInit();
                bmp.Freeze();
                StreamImage.Source = bmp;

                if (!_hasFrame)
                {
                    _hasFrame = true;
                    NoSignalPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        });
    }

    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_muted) return;
        var v = (float)(e.NewValue / 100.0);
        _audio.SetPeerStreamVolume(_peer.DeviceId, v);
        if (VolLabel != null) VolLabel.Text = $"{(int)e.NewValue}%";
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        _audio.SetPeerStreamVolume(_peer.DeviceId, _muted ? 0f : (float)(VolumeSlider.Value / 100.0));
        MuteBtn.Content = _muted ? "\uD83D\uDD07" : "\uD83D\uDD0A";
        MuteBtn.ToolTip = _muted ? "Включить стрим" : "Заглушить стрим";
        MuteBtn.Foreground = _muted
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 71, 71))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 221, 222));
    }

    private void ExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        Width  = 900;
        Height = 560;
        ExpandBtn.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _audio.SetPeerStreamVolume(_peer.DeviceId, 0f);
    }
}
