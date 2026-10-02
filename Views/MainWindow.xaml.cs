using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RadCord.Core;
using RadCord.Models;
using RadCord.ViewModels;

namespace RadCord.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private SettingsDialog?          _settingsDialog;

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        _vm            = new MainViewModel(config);
        DataContext    = _vm;

        _vm.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        _vm.ScreenFrameReceived  += OnScreenFrameReceived;
        _vm.PeerStreamStopped    += OnPeerStreamStopped;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { }
    }

    private void ScrollToBottom()
    {
        Dispatcher.InvokeAsync(() => ChatScroll.ScrollToEnd());
    }


    private void NetworkIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NetworkInfo net)
            _vm.SelectNetworkCommand.Execute(net);
    }

    private void AddNetwork_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NetworkDialog(_vm.Config) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultNetwork != null)
            _vm.AddNetworkCommand.Execute(dlg.ResultNetwork);
    }

    private void DeleteNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is NetworkInfo net)
        {
            var res = MessageBox.Show($"Удалить сервер «{net.ShownName}»?\nИстория сообщений будет очищена.",
                "Удалить сервер", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
                _vm.RemoveNetworkCommand.Execute(net);
        }
    }

    private void RenameNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is NetworkInfo net)
        {
            var dlg = new InputDialog("Новое название:", "Переименовать сервер", net.ShownName) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
            {
                _vm.Config.RenameNetwork(net.Id, dlg.Result);
                net.DisplayName = dlg.Result;
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsDialog?.IsLoaded == true)
        {
            _settingsDialog.Activate();
            return;
        }
        _settingsDialog = new SettingsDialog(_vm) { Owner = this };
        _settingsDialog.Show();
    }


    private void TextChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ch)
        {
            _vm.SelectedTextChannel = ch;
            _vm.IsViewingVoice = false;
        }
    }

    private void VoiceChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ch)
        {
            if (_vm.IsInVoice && _vm.CurrentVoiceChannel == ch)
                _vm.IsViewingVoice = true;
            else
                _ = _vm.JoinVoiceAsync(ch);
        }
    }

    private void AddTextChannel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("Название нового текстового канала:", "Создать канал", "новый-канал") { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
        {
            var name = dlg.Result.Trim().ToLower().Replace(' ', '-');
            if (!_vm.TextChannels.Contains(name))
            {
                _vm.TextChannels.Add(name);
                if (_vm.SelectedNetwork != null)
                {
                    _vm.Config.AddTextChannel(_vm.SelectedNetwork.Id, name);
                    _ = _vm.Mesh.AddTextChannelAsync(name, _vm.SelectedNetwork.Id);
                }
            }
        }
    }

    private void DeleteTextChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string ch)
        {
            _vm.TextChannels.Remove(ch);
            if (_vm.SelectedNetwork != null)
            {
                _vm.Config.RemoveTextChannel(_vm.SelectedNetwork.Id, ch);
                _ = _vm.Mesh.RemoveTextChannelAsync(ch, _vm.SelectedNetwork.Id);
            }
        }
    }

    private void DeleteVoiceChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string ch)
            _ = _vm.RemoveVoiceChannelAsync(ch);
    }

    private void AddVoiceChannel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("Название нового голосового канала:", "Создать канал", "Голосовой") { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Result))
            _ = _vm.AddVoiceChannelAsync(dlg.Result);
    }


    private void LocalMutePeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string did && did != _vm.Config.DeviceId)
        {
            _vm.ToggleLocalMuteCommand.Execute(did);
            mi.Header = _vm.Audio.IsLocallyMuted(did)
                ? "🔊 Включить звук"
                : "🔇 Заглушить для меня";
        }
    }

    private void PeerVolSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider sl)
        {
            if (sl.Tag is Peer p)
            {
                p.Volume = (float)(e.NewValue / 100.0);
                _vm.Audio.SetPeerVolume(p.DeviceId, p.Volume);
            }
            else if (sl.Tag is string did)
                _vm.Audio.SetPeerVolume(did, (float)(e.NewValue / 100.0));
        }
    }

    private void StreamVolSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider sl && sl.Tag is Peer peer)
        {
            peer.StreamVolume = e.NewValue;
            var vol = (float)(e.NewValue / 100.0);
            _vm.Audio.SetPeerStreamVolume(peer.DeviceId, vol);
            peer.StreamMuted = vol <= 0f;
        }
    }

    private void ToggleMute_Click(object sender, RoutedEventArgs e)
        => _vm.ToggleMuteCommand.Execute(null);

    private void ToggleDeafen_Click(object sender, RoutedEventArgs e)
        => _vm.ToggleDeafenCommand.Execute(null);

    private void LeaveVoice_Click(object sender, RoutedEventArgs e)
        => _ = _vm.LeaveVoiceAsync();

    private void ShareScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSharing)
        {
            _ = _vm.StopSharingAsync();
            return;
        }

        if (!_vm.IsInVoice)
        {
            MessageBox.Show("Войдите в голосовой канал перед трансляцией.", "RadCord",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new ScreenPickerDialog { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedSource != null)
            _ = _vm.StartSharingAsync(picker.SelectedSource!);
    }

    private void WatchOwnStream_Click(object sender, RoutedEventArgs e)
    {
        var selfPeer = _vm.VoicePeers.FirstOrDefault(p => p.DeviceId == _vm.Config.DeviceId);
        if (selfPeer == null) return;
        if (_screenViewers.TryGetValue(selfPeer.DeviceId, out var existing)) { existing.Activate(); return; }
        var win = new ScreenViewWindow(selfPeer, _vm.Audio);
        win.Closed += (_, _) => _screenViewers.Remove(selfPeer.DeviceId);
        _screenViewers[selfPeer.DeviceId] = win;
        win.Owner = this;
        win.Show();
    }

    private void StopShare_Click(object sender, RoutedEventArgs e)
        => _ = _vm.StopSharingAsync();

    private void WatchScreen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Peer peer)
        {
            if (_screenViewers.TryGetValue(peer.DeviceId, out var existing))
            {
                existing.Activate();
                return;
            }
            var win = new ScreenViewWindow(peer, _vm.Audio, mini: false);
            win.Closed += (_, _) => _screenViewers.Remove(peer.DeviceId);
            _screenViewers[peer.DeviceId] = win;
            win.Owner = this;
            win.Show();
        }
    }

    private void WatchStreamStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Peer peer)
        {
            peer.IsWatching   = true;
            peer.StreamMuted  = true;
            peer.StreamVolume = 0;
            _vm.Audio.SetPeerStreamVolume(peer.DeviceId, 0f);
        }
    }

    private void ExitStream_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Peer peer)
            peer.IsWatching = false;
    }

    private void StreamResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb t && t.Tag is Peer peer)
        {
            peer.CardWidth  = Math.Max(160, Math.Min(900, peer.CardWidth  + e.HorizontalChange));
            peer.CardHeight = Math.Max(100, Math.Min(700, peer.CardHeight + e.VerticalChange));
        }
    }


    private void ContextLocalMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Peer peer && peer.DeviceId != _vm.Config.DeviceId)
            _vm.ToggleLocalMuteCommand.Execute(peer.DeviceId);
    }

    private void ContextStreamMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Peer peer)
        {
            peer.StreamMuted = !peer.StreamMuted;
            if (peer.StreamMuted)
            {
                peer.StreamVolume = 0;
                _vm.Audio.SetPeerStreamVolume(peer.DeviceId, 0f);
            }
            else
            {
                peer.StreamVolume = 50;
                _vm.Audio.SetPeerStreamVolume(peer.DeviceId, 0.5f);
            }
        }
    }

    private void ContextOpenStream_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Peer peer)
        {
            if (_screenViewers.TryGetValue(peer.DeviceId, out var existing)) { existing.Activate(); return; }
            var win = new ScreenViewWindow(peer, _vm.Audio, mini: false);
            win.Closed += (_, _) => _screenViewers.Remove(peer.DeviceId);
            _screenViewers[peer.DeviceId] = win;
            win.Owner = this;
            win.Show();
        }
    }


    private Peer?  _dragPeer;
    private Point  _dragStart;
    private double _dragStartOffsetX, _dragStartOffsetY;
    private bool   _dragActive;

    private void VoiceCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        while (src != null)
        {
            if (src is Button || src is Thumb || src is Slider) return;
            if (src == sender) break;
            src = VisualTreeHelper.GetParent(src);
        }
        if (sender is FrameworkElement fe && fe.DataContext is Peer peer)
        {
            _dragPeer         = peer;
            _dragStart        = e.GetPosition(this);
            _dragStartOffsetX = peer.CardOffsetX;
            _dragStartOffsetY = peer.CardOffsetY;
            _dragActive       = false;
            fe.CaptureMouse();
        }
    }

    private void VoiceCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPeer == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        var dx  = pos.X - _dragStart.X;
        var dy  = pos.Y - _dragStart.Y;
        if (!_dragActive && Math.Sqrt(dx * dx + dy * dy) < 5) return;
        _dragActive           = true;
        _dragPeer.CardOffsetX = _dragStartOffsetX + dx;
        _dragPeer.CardOffsetY = _dragStartOffsetY + dy;
    }

    private void VoiceCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragPeer == null) return;
        bool wasDragging = _dragActive;
        _dragPeer   = null;
        _dragActive = false;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        if (wasDragging) e.Handled = true;
    }

    private void ChatImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Image img && img.Source is BitmapSource src)
        {
            var w = new Window
            {
                Title                   = "Изображение",
                Background              = Brushes.Black,
                Width                   = Math.Min(src.PixelWidth  + 40, 1400),
                Height                  = Math.Min(src.PixelHeight + 40, 900),
                WindowStartupLocation   = WindowStartupLocation.CenterOwner,
                Owner                   = this,
                Content                 = new System.Windows.Controls.Image { Source = src, Stretch = Stretch.Uniform },
            };
            w.Show();
        }
    }

    private readonly Dictionary<string, ScreenViewWindow> _screenViewers = new();

    private void OnScreenFrameReceived(string deviceId, byte[] jpeg)
    {
        if (_screenViewers.TryGetValue(deviceId, out var win))
            win.OnFrame(deviceId, jpeg);

        Dispatcher.InvokeAsync(() =>
        {
            var peer = _vm.VoicePeers.FirstOrDefault(p => p.DeviceId == deviceId);
            if (peer == null) return;
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(jpeg);
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                peer.StreamFrameSource = bmp;
            }
            catch { }
        });
    }

    private void OnPeerStreamStopped(string deviceId)
    {
        if (_screenViewers.TryGetValue(deviceId, out var win))
        {
            win.Close();
            _screenViewers.Remove(deviceId);
        }
    }


    private void ChatInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
        {
            e.Handled = true;
            _ = _vm.SendMessageCommand.ExecuteAsync(null);
            return;
        }
        // Ctrl+V — paste image or files from clipboard
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Clipboard.ContainsImage())
            {
                e.Handled = true;
                var bmp = Clipboard.GetImage();
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                _ = _vm.SendFileBytesAsync("image.jpg", ms.ToArray());
                return;
            }
            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = true;
                foreach (string f in Clipboard.GetFileDropList())
                    _ = _vm.SendFileAsync(f);
                return;
            }
        }
    }

    private void AttachFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Прикрепить файл",
            Filter = "Все файлы|*.*|Изображения|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames)
                _ = _vm.SendFileAsync(f);
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ChatMessage cm && cm.ImageData != null)
        {
            var dlg = new SaveFileDialog { FileName = cm.FileName ?? "file", Title = "Сохранить файл" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var bytes = Convert.FromBase64String(cm.ImageData);
                    File.WriteAllBytes(dlg.FileName, bytes);
                }
                catch { }
            }
        }
    }


    private bool _pttActive;

    private bool IsPttKey(string keyStr, Key key)
        => Enum.TryParse<Key>(keyStr, out var k) && k == key;

    private bool IsPttMouse(string keyStr, MouseButton btn)
        => keyStr.StartsWith("Mouse") && keyStr == MouseButtonToStr(btn);

    private static string MouseButtonToStr(MouseButton b) => b switch
    {
        MouseButton.Left     => "Mouse1",
        MouseButton.Right    => "Mouse2",
        MouseButton.Middle   => "Mouse3",
        MouseButton.XButton1 => "Mouse4",
        MouseButton.XButton2 => "Mouse5",
        _                    => ""
    };

    private void ActivatePtt()
    {
        if (_vm.Config.Profile.VoiceMode != "ptt") return;
        if (!_pttActive) { _pttActive = true; _vm.IsMuted = false; }
    }

    private void ReleasePtt()
    {
        if (!_pttActive) return;
        _pttActive = false;
        if (_vm.Config.Profile.VoiceMode == "ptt") _vm.IsMuted = true;
    }

    private static bool IsKeyBind(string? bind, Key key)
        => bind != null && !bind.StartsWith("Mouse") && Enum.TryParse<Key>(bind, out var k) && k == key;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var ptt = _vm.Config.Profile.PushToTalkKey;
        if (IsKeyBind(ptt, e.Key)) { ActivatePtt(); e.Handled = true; return; }

        if (Keyboard.FocusedElement is TextBox) return;

        if (IsKeyBind(_vm.Config.Profile.MicMuteKey, e.Key))
        { _vm.IsMuted = !_vm.IsMuted; e.Handled = true; return; }
        if (IsKeyBind(_vm.Config.Profile.FullMuteKey, e.Key))
        { _vm.IsDeafened = !_vm.IsDeafened; e.Handled = true; }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        var pttKey = _vm.Config.Profile.PushToTalkKey;
        if (pttKey != null && !pttKey.StartsWith("Mouse") && IsPttKey(pttKey, e.Key))
        { ReleasePtt(); e.Handled = true; }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var str = MouseButtonToStr(e.ChangedButton);
        if (str == "") return;
        if (_vm.Config.Profile.PushToTalkKey == str)  { ActivatePtt(); e.Handled = true; }
        if (_vm.Config.Profile.MicMuteKey    == str)  { _vm.IsMuted    = !_vm.IsMuted;    e.Handled = true; }
        if (_vm.Config.Profile.FullMuteKey   == str)  { _vm.IsDeafened = !_vm.IsDeafened; e.Handled = true; }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var ptt = _vm.Config.Profile.PushToTalkKey;
        if (ptt != null && ptt == MouseButtonToStr(e.ChangedButton))
        { ReleasePtt(); e.Handled = true; }
    }


    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        Task.Run(() => _vm.Shutdown());
    }
}
