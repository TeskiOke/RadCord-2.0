using CommunityToolkit.Mvvm.ComponentModel;
using RadCord.Core;

namespace RadCord.Models;

public partial class NetworkInfo : ObservableObject
{
    [ObservableProperty] private string _id          = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownName))]
    private string _name        = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownName))]
    private string _displayName = "";

    [ObservableProperty] private int _onlineCount;

    public string ShownName => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;

    public static NetworkInfo FromConfig(NetworkData data) => new()
    {
        Id          = data.Id,
        Name        = data.Name,
        DisplayName = data.DisplayName ?? "",
    };
}
