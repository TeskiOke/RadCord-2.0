using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RadCord;
using RadCord.Core;
using RadCord.ViewModels;

namespace RadCord.Views;

public partial class SettingsDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _meterTimer = new();
    private Border? _meterBar;

    private string _selectedAvatarColor;
    private string _selectedAccentColor;
    private string _selectedTheme;
    private bool   _audioLoaded;
    private bool   _capturingPtt;
    private bool   _capturingMicMute;
    private bool   _capturingMute;

    public SettingsDialog(MainViewModel vm)
    {
        _vm                  = vm;
        _selectedAvatarColor = vm.Config.AvatarColor;
        _selectedAccentColor = vm.Config.Profile.AppColor ?? "#5865f2";
        _selectedTheme       = vm.Config.Profile.Theme    ?? "dark";
        InitializeComponent();
        LoadAudioDevices();
        LoadCurrentSettings();
        InitColorSliders();
        InitBindLabels();
        InitVoiceMode();
        _audioLoaded = true;

        _meterBar = MicMeter;
        _meterTimer.Interval = TimeSpan.FromMilliseconds(60);
        _meterTimer.Tick    += MeterTick;
    }


    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string page)
        {
            PageProfile.Visibility = page == "Profile" ? Visibility.Visible : Visibility.Collapsed;
            PageAudio.Visibility   = page == "Audio"   ? Visibility.Visible : Visibility.Collapsed;
            PageBinds.Visibility   = page == "Binds"   ? Visibility.Visible : Visibility.Collapsed;

            NavProfile.Style = FindResource(page == "Profile" ? "AccentButton" : "GhostButton") as Style;
            NavAudio.Style   = FindResource(page == "Audio"   ? "AccentButton" : "GhostButton") as Style;
            NavBinds.Style   = FindResource(page == "Binds"   ? "AccentButton" : "GhostButton") as Style;
        }
    }


    private void LoadAudioDevices()
    {
        InputDeviceCombo.Items.Clear();
        InputDeviceCombo.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = (string?)null });
        foreach (var (id, name) in AudioManager.GetInputDevices())
            InputDeviceCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });

        OutputDeviceCombo.Items.Clear();
        OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = (string?)null });
        foreach (var (id, name) in AudioManager.GetOutputDevices())
            OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
    }

    private void LoadCurrentSettings()
    {
        // Select current input device
        var inId = _vm.Config.Profile.AudioInputDevice;
        foreach (ComboBoxItem item in InputDeviceCombo.Items)
            if (item.Tag?.ToString() == inId) { InputDeviceCombo.SelectedItem = item; break; }
        if (InputDeviceCombo.SelectedIndex < 0) InputDeviceCombo.SelectedIndex = 0;

        var outId = _vm.Config.Profile.AudioOutputDevice;
        foreach (ComboBoxItem item in OutputDeviceCombo.Items)
            if (item.Tag?.ToString() == outId) { OutputDeviceCombo.SelectedItem = item; break; }
        if (OutputDeviceCombo.SelectedIndex < 0) OutputDeviceCombo.SelectedIndex = 0;

        var vadPct = _vm.Config.Profile.VadPct;
        VadSlider.Value     = vadPct;
        VadLabel.Text       = vadPct == 0 ? "ВЫКЛ" : $"{vadPct}%";
        VadDisableBtn.IsChecked = vadPct == 0;
        MasterVolSlider.Value = (int)(_vm.Audio.MasterVolume * 100);
        MasterVolLabel.Text   = $"{(int)MasterVolSlider.Value}%";
        PeersVolSlider.Value  = (int)(_vm.Audio.PeersVolume * 100);
        PeersVolLabel.Text    = $"{(int)PeersVolSlider.Value}%";
        StreamVolSlider.Value   = (int)(_vm.Audio.StreamVolume * 100);
        StreamVolLabel.Text     = $"{(int)StreamVolSlider.Value}%";
        NotifVolSlider.Value    = (int)(_vm.Sound.Volume * 100);
        NotifVolLabel.Text      = $"{(int)NotifVolSlider.Value}%";

        NickBox.Text = _vm.Config.Nickname;
    }


    private void VadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_audioLoaded) return;
        var v = (int)e.NewValue;
        VadLabel.Text = v == 0 ? "ВЫКЛ" : $"{v}%";
        _vm.Audio.VadThreshold = v * 20f;
        _vm.Config.Profile.VadPct = v;
        _vm.Config.Save();
    }

    private void VadDisable_Changed(object sender, RoutedEventArgs e)
    {
        if (!_audioLoaded) return;
        bool disable = VadDisableBtn.IsChecked == true;
        if (disable)
        {
            VadSlider.Value = 0;       // это также запустит VadSlider_ValueChanged
            VadSlider.IsEnabled = false;
        }
        else
        {
            VadSlider.IsEnabled = true;
            if ((int)VadSlider.Value == 0)
                VadSlider.Value = 15;  // вернуть дефолтное значение
        }
    }

    private void MasterVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MasterVolLabel.Text = $"{(int)e.NewValue}%";
        if (_audioLoaded) _vm.Audio.MasterVolume = (float)(e.NewValue / 100.0);
    }

    private void PeersVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PeersVolLabel.Text = $"{(int)e.NewValue}%";
        if (_audioLoaded) _vm.Audio.PeersVolume = (float)(e.NewValue / 100.0);
    }

    private void StreamVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        StreamVolLabel.Text = $"{(int)e.NewValue}%";
        if (_audioLoaded) _vm.Audio.StreamVolume = (float)(e.NewValue / 100.0);
    }

    private void NotifVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        NotifVolLabel.Text = $"{(int)e.NewValue}%";
        if (_audioLoaded) _vm.Sound.Volume = (float)(e.NewValue / 100.0);
    }

    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_audioLoaded) return;
        var inItem  = InputDeviceCombo.SelectedItem  as ComboBoxItem;
        var outItem = OutputDeviceCombo.SelectedItem as ComboBoxItem;
        var inId    = inItem?.Tag  as string;
        var outId   = outItem?.Tag as string;
        _vm.Config.Profile.AudioInputDevice  = inId;
        _vm.Config.Profile.AudioOutputDevice = outId;
        _vm.Config.Save();
        _vm.Audio.SetInputDevice(inId);
        _vm.Audio.SetOutputDevice(outId);
        _vm.Audio.StopStreams();
        _vm.Audio.StartStreams();
    }


    private void MicTest_Checked(object sender, RoutedEventArgs e)
    {
        _vm.Audio.EnsureCapture();
        _vm.Audio.MicTestActive = true;
        _vm.Audio.OnRmsLevel    = UpdateMeter;
        _meterTimer.Start();
        MicTestBtn.Content = "⏹  Стоп";
    }

    private void MicTest_Unchecked(object sender, RoutedEventArgs e)
    {
        _vm.Audio.MicTestActive = false;
        _meterTimer.Stop();
        _vm.Audio.OnRmsLevel = v => _vm.RmsLevel = v;
        _vm.Audio.StopTestCapture();
        if (_meterBar != null) _meterBar.Width = 0;
        MicTestBtn.Content = "▶  Тест";
    }

    private float _lastRms;
    private void UpdateMeter(float rms) => _lastRms = rms;

    private void MeterTick(object? sender, EventArgs e)
    {
        if (_meterBar == null) return;
        var parent = _meterBar.Parent as Border;
        if (parent == null) return;
        var maxW = parent.ActualWidth;
        var frac = Math.Min(1.0, _lastRms / 2000.0);
        _meterBar.Width = maxW * frac;
        _meterBar.Background = new SolidColorBrush(
            _lastRms > _vm.Audio.VadThreshold
                ? Color.FromRgb(67, 181, 129)
                : Color.FromRgb(250, 166, 26));
    }


    private void InitBindLabels()
    {
        PttKeyLabel.Text     = _vm.Config.Profile.PushToTalkKey ?? "Не назначено";
        MicMuteKeyLabel.Text = _vm.Config.Profile.MicMuteKey    ?? "Не назначено";
        MuteKeyLabel.Text    = _vm.Config.Profile.FullMuteKey   ?? "Не назначено";
    }

    private void ResetCaptureButtons()
    {
        CapturePttBtn.Content     = "Назначить";
        CaptureMicMuteBtn.Content = "Назначить";
        CaptureMuteBtn.Content    = "Назначить";
    }

    private void CapturePtt_Click(object sender, RoutedEventArgs e)
    {
        _capturingPtt = true; _capturingMicMute = _capturingMute = false;
        CapturePttBtn.Content = "[ Нажмите клавишу... ]";
        CaptureMicMuteBtn.Content = CaptureMuteBtn.Content = "Назначить";
    }

    private void CaptureMicMute_Click(object sender, RoutedEventArgs e)
    {
        _capturingMicMute = true; _capturingPtt = _capturingMute = false;
        CaptureMicMuteBtn.Content = "[ Нажмите клавишу... ]";
        CapturePttBtn.Content = CaptureMuteBtn.Content = "Назначить";
    }

    private void CaptureMute_Click(object sender, RoutedEventArgs e)
    {
        _capturingMute = true; _capturingPtt = _capturingMicMute = false;
        CaptureMuteBtn.Content = "[ Нажмите клавишу... ]";
        CapturePttBtn.Content = CaptureMicMuteBtn.Content = "Назначить";
    }

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); } catch { }
    }

    private void InitVoiceMode()
    {
        if (_vm.Config.Profile.VoiceMode == "ptt")
            RbPtt.IsChecked = true;
        else
            RbVad.IsChecked = true;
    }

    private void VoiceMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_audioLoaded) return;
        _vm.Config.Profile.VoiceMode = RbPtt.IsChecked == true ? "ptt" : "vad";
        _vm.Config.Save();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (!_capturingPtt && !_capturingMicMute && !_capturingMute) return;
        var mouseStr = e.ChangedButton switch
        {
            MouseButton.Left     => "Mouse1",
            MouseButton.Right    => "Mouse2",
            MouseButton.Middle   => "Mouse3",
            MouseButton.XButton1 => "Mouse4",
            MouseButton.XButton2 => "Mouse5",
            _                    => (string?)null
        };
        if (mouseStr == null) return;
        ApplyBind(mouseStr);
        _vm.Config.Save();
        e.Handled = true;
    }

    private void ApplyBind(string str)
    {
        if (_capturingPtt)
        {
            _vm.Config.Profile.PushToTalkKey = str;
            PttKeyLabel.Text = str;
        }
        else if (_capturingMicMute)
        {
            _vm.Config.Profile.MicMuteKey = str;
            MicMuteKeyLabel.Text = str;
        }
        else if (_capturingMute)
        {
            _vm.Config.Profile.FullMuteKey = str;
            MuteKeyLabel.Text = str;
        }
        _capturingPtt = _capturingMicMute = _capturingMute = false;
        ResetCaptureButtons();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_capturingPtt && !_capturingMicMute && !_capturingMute) return;
        if (e.Key == Key.Escape)
        {
            _capturingPtt = _capturingMicMute = _capturingMute = false;
            ResetCaptureButtons();
            e.Handled = true;
            return;
        }
        var keyStr = e.Key.ToString();
        ApplyBind(keyStr);
        _vm.Config.Save();
        e.Handled = true;
    }


    private void InitColorSliders()
    {
        // Avatar color
        try
        {
            var ac = (Color)ColorConverter.ConvertFromString(_selectedAvatarColor);
            AvatarRSlider.Value = ac.R;
            AvatarGSlider.Value = ac.G;
            AvatarBSlider.Value = ac.B;
        }
        catch { AvatarRSlider.Value = 114; AvatarGSlider.Value = 137; AvatarBSlider.Value = 218; }
        UpdateAvatarPreview();

        // Accent color
        try
        {
            var ac = (Color)ColorConverter.ConvertFromString(_selectedAccentColor);
            AccentRSlider.Value = ac.R;
            AccentGSlider.Value = ac.G;
            AccentBSlider.Value = ac.B;
        }
        catch { AccentRSlider.Value = 88; AccentGSlider.Value = 101; AccentBSlider.Value = 242; }
        UpdateAccentPreview();

        // Theme radio
        switch (_selectedTheme)
        {
            case "gray":  RbGray.IsChecked  = true; break;
            case "light": RbLight.IsChecked = true; break;
            default:      RbDark.IsChecked  = true; break;
        }
    }

    private void UpdateAvatarPreview()
    {
        var r = (byte)AvatarRSlider.Value;
        var g = (byte)AvatarGSlider.Value;
        var b = (byte)AvatarBSlider.Value;
        AvatarRLabel.Text = r.ToString();
        AvatarGLabel.Text = g.ToString();
        AvatarBLabel.Text = b.ToString();
        _selectedAvatarColor = $"#{r:X2}{g:X2}{b:X2}";
        AvatarColorPreview.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void UpdateAccentPreview()
    {
        var r = (byte)AccentRSlider.Value;
        var g = (byte)AccentGSlider.Value;
        var b = (byte)AccentBSlider.Value;
        AccentRLabel.Text = r.ToString();
        AccentGLabel.Text = g.ToString();
        AccentBLabel.Text = b.ToString();
        _selectedAccentColor = $"#{r:X2}{g:X2}{b:X2}";
        AccentColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void AvatarRGB_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateAvatarPreview();

    private void AccentRGB_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAccentPreview();
        if (_audioLoaded)
        {
            _vm.Config.Profile.AppColor = _selectedAccentColor;
            App.ApplyTheme(_vm.Config);
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if      (RbGray?.IsChecked  == true) _selectedTheme = "gray";
        else if (RbLight?.IsChecked == true) _selectedTheme = "light";
        else                                 _selectedTheme = "dark";
        if (_audioLoaded)
        {
            _vm.Config.Profile.Theme = _selectedTheme;
            App.ApplyTheme(_vm.Config);
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var nick = NickBox.Text.Trim();
        if (string.IsNullOrEmpty(nick)) { MessageBox.Show("Введите никнейм."); return; }

        _vm.Config.Profile.Nickname    = nick;
        _vm.Config.Profile.AvatarColor = _selectedAvatarColor;
        _vm.Config.Profile.AppColor    = _selectedAccentColor;
        _vm.Config.Profile.Theme       = _selectedTheme;
        _vm.Config.Save();
        _vm.RefreshSelfInfo();
        App.ApplyTheme(_vm.Config);
        MessageBox.Show("Профиль сохранён.", "RadCord", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm.Audio.MicTestActive)
        {
            _vm.Audio.MicTestActive = false;
            _meterTimer.Stop();
            _vm.Audio.StopTestCapture();
        }
    }
}
