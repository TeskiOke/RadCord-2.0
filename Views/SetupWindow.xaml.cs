using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RadCord.Core;

namespace RadCord.Views;

public partial class SetupWindow : Window
{
    private string _selectedColor = "#5865F2";

    private static readonly string[] Palette =
    {
        "#5865F2", "#57F287", "#FEE75C", "#ED4245",
        "#EB459E", "#3BA55D", "#FAA61A", "#5DADE2",
        "#A569BD", "#1ABC9C", "#E67E22", "#99AAB5",
    };

    public SetupWindow()
    {
        InitializeComponent();
        BuildColorPicker();
        UpdateAvatarPreview();
        NicknameBox.Focus();
    }


    private void BuildColorPicker()
    {
        ColorPanel.Children.Clear();
        foreach (var hex in Palette)
        {
            var outer = new Border
            {
                Width         = 44,
                Height        = 44,
                CornerRadius  = new CornerRadius(22),
                Margin        = new Thickness(4),
                Cursor        = System.Windows.Input.Cursors.Hand,
                Tag           = hex,
                Background    = Brushes.Transparent,
                BorderThickness = new Thickness(3),
                BorderBrush   = Brushes.Transparent,
            };

            var circle = new Ellipse
            {
                Width  = 34,
                Height = 34,
                Fill   = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            var check = new TextBlock
            {
                Text                = "✓",
                FontSize            = 16,
                FontWeight          = FontWeights.Bold,
                Foreground          = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Visibility          = Visibility.Collapsed,
            };

            var grid = new Grid();
            grid.Children.Add(circle);
            grid.Children.Add(check);
            outer.Child = grid;

            outer.MouseLeftButtonUp += (_, _) =>
            {
                _selectedColor = hex;
                RefreshColorSelection();
                UpdateAvatarPreview();
            };
            ColorPanel.Children.Add(outer);
        }
        RefreshColorSelection();
    }

    private void RefreshColorSelection()
    {
        foreach (Border outer in ColorPanel.Children)
        {
            bool sel = outer.Tag?.ToString() == _selectedColor;
            outer.BorderBrush = sel
                ? new SolidColorBrush(System.Windows.Media.Colors.White)
                : Brushes.Transparent;
            if (outer.Child is Grid g)
            {
                ((TextBlock)g.Children[1]).Visibility =
                    sel ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }


    private void NicknameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAvatarPreview();
        ValidateForm();
    }

    private void UpdateAvatarPreview()
    {
        var nick = NicknameBox?.Text.Trim() ?? "";
        AvatarInitial.Text = nick.Length > 0
            ? nick[0].ToString().ToUpper()
            : "?";
        AvatarFill.Color = (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(_selectedColor);
    }

    private void ValidateForm()
    {
        var nick = NicknameBox.Text.Trim();
        bool ok = nick.Length >= 2;
        StartBtn.IsEnabled = ok;
        NickHint.Foreground = ok || nick.Length == 0
            ? (Brush)FindResource("TextMutedBrush")
            : (Brush)FindResource("RedBrush");
    }


    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var nick = NicknameBox.Text.Trim();
        if (nick.Length < 2)
        {
            ErrorText.Text       = "Никнейм должен содержать минимум 2 символа.";
            ErrorText.Visibility = Visibility.Visible;
            NicknameBox.Focus();
            return;
        }
        AppConfig.Create(nick, _selectedColor);
        DialogResult = true;
    }
}
