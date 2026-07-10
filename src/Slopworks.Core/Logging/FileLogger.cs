using Microsoft.Extensions.Logging;

namespace Slopworks.Core.Logging;

/// <summary>
/// Minimal daily-rolling file logger writing to logs/app-yyyyMMdd.log. Hand-written to keep
/// dependencies at zero and guarantee logs land inside the Slopworks root.
/// </summary>
public sealed class FileLoggerProvider(string logsDir) : ILoggerProvider
{
    private readonly Lock _writeLock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Abbrev(level)}] {categoryName}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        lock (_writeLock)
        {
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(Path.Combine(logsDir, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
