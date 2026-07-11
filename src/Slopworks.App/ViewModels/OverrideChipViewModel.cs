using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Slopworks.App.ViewModels;

/// <summary>One active bypass/force override, removable with a click to restore the check.</summary>
public partial class OverrideChipViewModel(string kind, string key, Action<string, string> remove) : ObservableObject
{
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public string Label => $"{Kind}: {Key}";

    [RelayCommand]
    private void Remove() => remove(Kind, Key);
}
