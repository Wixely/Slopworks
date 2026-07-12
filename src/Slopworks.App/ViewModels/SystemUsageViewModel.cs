using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Slopworks.Core.Platform;

namespace Slopworks.App.ViewModels;

public partial class GpuUsageItemViewModel(GpuUsage initial) : ObservableObject
{
    public int Index { get; } = initial.Index;
    public string Header { get; } = $"GPU {initial.Index} — {initial.Name}";

    [ObservableProperty]
    private double _utilizationPercent;

    [ObservableProperty]
    private string _utilizationLabel = "Processing —";

    [ObservableProperty]
    private double _vramPercent;

    [ObservableProperty]
    private string _vramLabel = "VRAM —";

    public void Update(GpuUsage usage)
    {
        UtilizationPercent = usage.UtilizationPercent;
        UtilizationLabel = $"Processing {usage.UtilizationPercent:0}%";
        VramPercent = usage.VramPercent;
        VramLabel = $"VRAM {usage.VramPercent:0}% — {usage.VramUsedMiB / 1024.0:0.0} / {usage.VramTotalMiB / 1024.0:0.0} GiB";
    }
}

/// <summary>Live whole-machine CPU/RAM bars plus per-GPU processing/VRAM bars, sampled once a second.</summary>
public partial class SystemUsageViewModel : ObservableObject
{
    private readonly SlopworksHost _host;
    private bool _gpuSampleInFlight;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _ramPercent;

    [ObservableProperty]
    private string _cpuLabel = "CPU —";

    [ObservableProperty]
    private string _ramLabel = "RAM —";

    [ObservableProperty]
    private bool _hasGpus;

    public ObservableCollection<GpuUsageItemViewModel> Gpus { get; } = [];

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

        _ = RefreshGpusAsync();
    }

    private async Task RefreshGpusAsync()
    {
        if (_gpuSampleInFlight)
            return;

        _gpuSampleInFlight = true;
        try
        {
            var samples = await Task.Run(() => _host.GpuMetrics.SampleAsync(CancellationToken.None));

            if (samples.Count != Gpus.Count || samples.Select(s => s.Index).SequenceEqual(Gpus.Select(g => g.Index)) == false)
            {
                Gpus.Clear();
                foreach (var sample in samples)
                    Gpus.Add(new GpuUsageItemViewModel(sample));
            }

            foreach (var sample in samples)
                Gpus.FirstOrDefault(g => g.Index == sample.Index)?.Update(sample);

            HasGpus = Gpus.Count > 0;
        }
        finally
        {
            _gpuSampleInFlight = false;
        }
    }
}
