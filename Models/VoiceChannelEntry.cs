using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RadCord.Models;

public partial class VoiceChannelEntry : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private int _count;
    public ObservableCollection<Peer> Participants { get; } = new();

    public VoiceChannelEntry(string name) => Name = name;
}
