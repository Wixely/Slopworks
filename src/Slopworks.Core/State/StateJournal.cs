using System.Text.Json;
using Slopworks.Core.Serialization;

namespace Slopworks.Core.State;

public sealed class JournalData
{
    public int SchemaVersion { get; set; } = 1;
    public string? RunId { get; set; }
    public string Mode { get; set; } = "safe";
    public PendingReboot? PendingReboot { get; set; }
    public Dictionary<string, StepJournalEntry> Steps { get; set; } = [];
    public Dictionary<string, ResolvedArtifactEntry> ResolvedArtifacts { get; set; } = [];
}

public sealed class PendingReboot
{
    public string AfterStep { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed class StepJournalEntry
{
    public string LastState { get; set; } = "Unknown";
    public DateTimeOffset LastVerifiedAt { get; set; }
}

public sealed class ResolvedArtifactEntry
{
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public string FileName { get; set; } = "";
    public DateTimeOffset ResolvedAt { get; set; }
}

/// <summary>
/// Advisory persistence for resume points and resolved artifacts. Detection is always the
/// source of truth — a stale journal can never cause a wrong action.
/// </summary>
public interface IStateJournal
{
    JournalData Data { get; }
    Task SaveAsync(CancellationToken ct = default);
}

public sealed class FileStateJournal(string path) : IStateJournal
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JournalData Data { get; private set; } = new();

    public static FileStateJournal Load(string path)
    {
        var journal = new FileStateJournal(path);
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), SlopworksJsonContext.Default.JournalData);
                if (loaded is not null)
                    journal.Data = loaded;
            }
            catch (JsonException)
            {
                // Corrupt journal: start fresh. Detection re-establishes truth on the next run.
            }
        }

        return journal;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(Data, SlopworksJsonContext.Default.JournalData), ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
