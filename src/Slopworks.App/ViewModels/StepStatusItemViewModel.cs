using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class StepStatusItemViewModel(string id, string title, Action<string>? onBypass = null) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBypass))]
    private string? _bypassKey;

    public bool CanBypass => BypassKey is not null && onBypass is not null;

    [RelayCommand]
    private void Bypass()
    {
        if (BypassKey is { } key)
            onBypass?.Invoke(key);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    private string _state = "…";

    [ObservableProperty]
    private string _summary = "Detecting…";

    [ObservableProperty]
    private string _evidence = "";

    public IBrush StateBrush => State switch
    {
        "Ok" => Brushes.SeaGreen,
        "Partial" => Brushes.DarkOrange,
        "Broken" or "Failed" or "Denied" => Brushes.Firebrick,
        "Missing" => Brushes.SlateGray,
        "Skipped" => Brushes.DimGray,
        "Reboot" => Brushes.SteelBlue,
        _ => Brushes.DimGray,
    };

    public void Update(StepDetection detection)
    {
        State = detection.State.ToString();
        Summary = detection.Summary;
        Evidence = string.Join(Environment.NewLine, detection.Evidence);
        BypassKey = detection.BypassKey;
    }

    public void MarkOutcome(StepOutcome outcome, string? detail)
    {
        switch (outcome)
        {
            case StepOutcome.Skipped:
                State = "Skipped";
                Summary = detail ?? "Not applicable";
                break;
            case StepOutcome.Failed:
                State = "Failed";
                Summary = detail ?? Summary;
                break;
            case StepOutcome.Denied:
                State = "Denied";
                Summary = detail ?? Summary;
                break;
            case StepOutcome.RebootRequired:
                State = "Reboot";
                Summary = detail ?? "Reboot required to continue";
                break;
        }
    }
}
