using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using RadCord.Core;

namespace RadCord.Views;

public partial class NetworkDialog : Window
{
    private readonly AppConfig _config;
    private bool        _isCreateMode = true;
    private JObject?    _selectedScanResult;

    public NetworkData? ResultNetwork { get; private set; }

    public NetworkDialog(AppConfig config)
    {
        _config = config;
        InitializeComponent();
    }


    private void TabCreate_Click(object sender, RoutedEventArgs e)
    {
        _isCreateMode          = true;
        ChoosePanel.Visibility = Visibility.Collapsed;
        CreatePanel.Visibility = Visibility.Visible;
        JoinPanel.Visibility   = Visibility.Collapsed;
        NameBox.Focus();
    }

    private void TabJoin_Click(object sender, RoutedEventArgs e)
    {
        _isCreateMode          = false;
        ChoosePanel.Visibility = Visibility.Collapsed;
        CreatePanel.Visibility = Visibility.Collapsed;
        JoinPanel.Visibility   = Visibility.Visible;
    }

    private void BackToChoose_Click(object sender, RoutedEventArgs e)
    {
        ChoosePanel.Visibility = Visibility.Visible;
        CreatePanel.Visibility = Visibility.Collapsed;
        JoinPanel.Visibility   = Visibility.Collapsed;
    }


    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanStatus.Text       = "Сканирование… (4 сек)";
        ScanStatus.Visibility = Visibility.Visible;
        ScanList.Items.Clear();

        var results = await PeerDiscovery.ScanAsync(4.0);

        ScanStatus.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanStatus.Text       = "Сетей не найдено.";

        foreach (var net in results)
        {
            var name  = net["name"]?.ToString() ?? "?";
            var users = (net["users"] as JArray)?.Count ?? 0;
            var item  = new ListBoxItem
            {
                Content = $"{name}  ({users} чел.)",
                Tag     = net,
            };
            ScanList.Items.Add(item);
        }
    }

    private void ScanList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScanList.SelectedItem is ListBoxItem item && item.Tag is JObject net)
            _selectedScanResult = net;
    }


    private void Add_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility    = Visibility.Collapsed;
        JoinErrorText.Visibility = Visibility.Collapsed;

        if (_isCreateMode)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowError(ErrorText, "Введите название сервера.");
                return;
            }
            ResultNetwork = _config.AddNetwork(name, PasswordBox.Password);
        }
        else
        {
            if (_selectedScanResult == null)
            {
                ShowError(JoinErrorText, "Выберите сеть из списка.");
                return;
            }
            var name = _selectedScanResult["name"]?.ToString() ?? "";
            ResultNetwork = _config.AddNetwork(name, JoinPasswordBox.Password);
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void ShowError(System.Windows.Controls.TextBlock tb, string msg)
    {
        tb.Text       = msg;
        tb.Visibility = Visibility.Visible;
    }
}
