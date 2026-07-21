using System.ComponentModel;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Platform;

/// <summary>A GPU as reported by nvidia-smi: its NVML index, name, PCI bus id and memory.</summary>
public sealed record GpuDevice(int Index, string Name, string PciBusId, int MemoryMiB)
{
    public bool HasPci => PciBusId.Length > 0 && !PciBusId.Equals("N/A", StringComparison.OrdinalIgnoreCase);

    /// <summary>e.g. "GPU 0 — NVIDIA GeForce RTX 5090 · 32 GB · PCI 00000000:01:00.0".</summary>
    public string Describe()
    {
        var mem = MemoryMiB > 0 ? $" · {MemoryMiB / 1024} GB" : "";
        var pci = HasPci ? $" · PCI {PciBusId}" : "";
        return $"GPU {Index} — {Name}{mem}{pci}";
    }
}

/// <summary>
/// Parses "nvidia-smi --query-gpu=index,name[,pci.bus_id],memory.total
/// --format=csv,noheader,nounits" — one GPU per line.
/// </summary>
public static class GpuInventoryParser
{
    public static IReadOnlyList<GpuDevice> Parse(string csv, bool hasPci = true)
    {
        var devices = new List<GpuDevice>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            var minColumns = hasPci ? 4 : 3;
            if (parts.Length < minColumns || !int.TryParse(parts[0], out var index))
                continue;

            var pci = hasPci ? parts[2] : "";
            var memoryField = hasPci ? parts[3] : parts[2];
            int.TryParse(memoryField, out var memory);

            devices.Add(new GpuDevice(index, parts[1], pci, memory));
        }

        return devices;
    }
}

/// <summary>
/// Runs nvidia-smi to enumerate GPUs, shared by the platform providers. Tries the full query
/// (with PCI bus id) and falls back to name-only if the driver is too old to report PCI.
/// </summary>
public static class NvidiaSmiInventory
{
    public static async Task<IReadOnlyList<GpuDevice>> EnumerateAsync(IProcessRunner probes, string exe, CancellationToken ct)
    {
        var withPci = await TryQueryAsync(probes, exe, "index,name,pci.bus_id,memory.total", hasPci: true, ct);
        return withPci.Count > 0
            ? withPci
            : await TryQueryAsync(probes, exe, "index,name,memory.total", hasPci: false, ct);
    }

    private static async Task<IReadOnlyList<GpuDevice>> TryQueryAsync(
        IProcessRunner probes, string exe, string query, bool hasPci, CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(
                new ProcessSpec(exe, [$"--query-gpu={query}", "--format=csv,noheader,nounits"]), null, ct);
            return result.Succeeded ? GpuInventoryParser.Parse(result.Stdout, hasPci) : [];
        }
        catch (Win32Exception)
        {
            return [];
        }
    }
}
