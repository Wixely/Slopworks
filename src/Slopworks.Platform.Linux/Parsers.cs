namespace Slopworks.Platform.Linux;

/// <summary>Parses the aggregate "cpu" line of /proc/stat into idle and total jiffies.</summary>
public static class ProcStatParser
{
    /// <summary>Fields: user nice system idle iowait irq softirq steal [guest guest_nice]. Idle = idle + iowait.</summary>
    public static (ulong Idle, ulong Total)? ParseCpuLine(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
                continue;

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
                return null;

            ulong total = 0, idle = 0;
            for (var i = 1; i < fields.Length && i <= 8; i++)
            {
                if (!ulong.TryParse(fields[i], out var value))
                    return null;
                total += value;
                if (i is 4 or 5) // idle + iowait
                    idle += value;
            }

            return (idle, total);
        }

        return null;
    }
}

/// <summary>Parses /proc/meminfo for total and available memory.</summary>
public static class MemInfoParser
{
    /// <summary>Returns bytes (meminfo reports kB).</summary>
    public static (long TotalBytes, long AvailableBytes)? Parse(string memInfo)
    {
        long? total = null, available = null;
        foreach (var line in memInfo.Split('\n'))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = ParseKb(line);

            if (total is not null && available is not null)
                return (total.Value, available.Value);
        }

        return null;
    }

    private static long? ParseKb(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && long.TryParse(fields[1], out var kb) ? kb * 1024 : null;
    }
}

/// <summary>Detects NVIDIA PCI devices in "lspci -nn" output (vendor id 10de), driver or not.</summary>
public static class LspciParser
{
    public static bool ContainsNvidiaDevice(string lspciOutput)
        => lspciOutput.Contains("[10de:", StringComparison.OrdinalIgnoreCase)
        || lspciOutput.Contains("10de:", StringComparison.OrdinalIgnoreCase)
           && lspciOutput.Contains("VGA", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parses /etc/os-release for a display name and Ubuntu version comparison.</summary>
public static class OsReleaseParser
{
    public static string GetPrettyName(string osRelease)
    {
        foreach (var line in osRelease.Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                return line["PRETTY_NAME=".Length..].Trim().Trim('"');
        }

        return "Linux";
    }

    /// <summary>Ubuntu VERSION_ID like "24.04"; null for non-Ubuntu or missing.</summary>
    public static Version? GetUbuntuVersion(string osRelease)
    {
        var isUbuntu = false;
        Version? version = null;
        foreach (var line in osRelease.Split('\n'))
        {
            if (line.StartsWith("ID=", StringComparison.Ordinal))
                isUbuntu = line["ID=".Length..].Trim().Trim('"').Equals("ubuntu", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("VERSION_ID=", StringComparison.Ordinal))
                Version.TryParse(line["VERSION_ID=".Length..].Trim().Trim('"'), out version);
        }

        return isUbuntu ? version : null;
    }
}

/// <summary>Interprets "ufw status" output.</summary>
public static class UfwParser
{
    public static bool IsActive(string ufwStatus)
        => ufwStatus.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
}
