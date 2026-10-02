using System.Windows;
using System.Windows.Controls;
using RadCord.Core;

namespace RadCord.Views;

public partial class ScreenPickerDialog : Window
{
    public ScreenCaptureSource? SelectedSource { get; private set; }

    public ScreenPickerDialog()
    {
        InitializeComponent();
        LoadQualityOptions();
        LoadMonitors();
    }

    private void LoadQualityOptions()
    {
        QualityCombo.Items.Clear();
        foreach (var (label, _, _, _) in Protocol.QualityPresets)
            QualityCombo.Items.Add(label);
        QualityCombo.SelectedIndex = 2; // 720p

        FpsCombo.Items.Clear();
        foreach (var fps in Protocol.FpsOptions)
            FpsCombo.Items.Add($"{fps} fps");
        FpsCombo.SelectedIndex = 2; // 24fps
    }

    private void LoadMonitors()
    {
        SourceList.Items.Clear();
        foreach (var src in ScreenCapture.GetMonitors())
        {
            var item = new ListBoxItem { Content = src.Label, Tag = src };
            SourceList.Items.Add(item);
        }
        if (SourceList.Items.Count > 0)
            SourceList.SelectedIndex = 0;
    }

    private void LoadWindows()
    {
        SourceList.Items.Clear();
        foreach (var src in ScreenCapture.GetWindows())
        {
            var item = new ListBoxItem { Content = src.Label, Tag = src };
            SourceList.Items.Add(item);
        }
        if (SourceList.Items.Count > 0)
            SourceList.SelectedIndex = 0;
    }

    private void TabMonitor_Click(object sender, RoutedEventArgs e)
    {
        TabMonitor.Style = FindResource("AccentButton") as Style;
        TabWindow.Style  = FindResource("GhostButton")  as Style;
        LoadMonitors();
    }

    private void TabWindow_Click(object sender, RoutedEventArgs e)
    {
        TabMonitor.Style = FindResource("GhostButton")  as Style;
        TabWindow.Style  = FindResource("AccentButton") as Style;
        LoadWindows();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not ListBoxItem item || item.Tag is not ScreenCaptureSource src)
        {
            MessageBox.Show("Выберите источник.", "RadCord");
            return;
        }

        // Apply quality / fps
        var qIdx = QualityCombo.SelectedIndex;
        var fIdx = FpsCombo.SelectedIndex;
        if (qIdx >= 0)
        {
            var (_, w, h, q) = Protocol.QualityPresets[qIdx];
            src.Width       = w;
            src.Height      = h;
            src.JpegQuality = q;
        }
        if (fIdx >= 0)
            src.Fps = Protocol.FpsOptions[fIdx];

        src.CaptureSystemAudio = IncludeAudio.IsChecked == true;
        SelectedSource = src;
        DialogResult   = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
