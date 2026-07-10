using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class StepStatusItemViewModel(string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;

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
        "Broken" => Brushes.Firebrick,
        "Missing" => Brushes.SlateGray,
        _ => Brushes.DimGray,
    };

    public void Update(StepDetection detection)
    {
        State = detection.State.ToString();
        Summary = detection.Summary;
        Evidence = string.Join(Environment.NewLine, detection.Evidence);
    }
}
