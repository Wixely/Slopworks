namespace Slopworks.Core.Engine;

/// <summary>
/// IProgress that invokes the callback synchronously on the reporting thread. Unlike
/// Progress&lt;T&gt; there is no sync-context marshaling, so event order is deterministic —
/// consumers (the UI) marshal to their own thread instead.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
