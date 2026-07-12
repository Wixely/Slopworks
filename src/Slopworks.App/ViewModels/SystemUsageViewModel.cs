using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Slopworks.App.ViewModels;

/// <summary>Live whole-machine CPU/RAM bars, sampled once a second.</summary>
public partial class SystemUsageViewModel : ObservableObject
{
    private readonly SlopworksHost _host;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _ramPercent;

    [ObservableProperty]
    private string _cpuLabel = "CPU —";

    [ObservableProperty]
    private string _ramLabel = "RAM —";

    public SystemUsageViewModel(SlopworksHost host)
    {
        _host = host;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh(); // establishes the CPU baseline; real values from the next tick
    }

    private void Refresh()
    {
        var usage = _host.Metrics.Sample();

        CpuPercent = usage.CpuPercent;
        CpuLabel = $"CPU {usage.CpuPercent:0}%";

        RamPercent = usage.RamPercent;
        RamLabel = usage.TotalRamBytes > 0
            ? $"RAM {usage.RamPercent:0}% — {usage.UsedRamBytes / 1024.0 / 1024 / 1024:0.0} / {usage.TotalRamBytes / 1024.0 / 1024 / 1024:0.0} GB"
            : "RAM —";
    }
}
