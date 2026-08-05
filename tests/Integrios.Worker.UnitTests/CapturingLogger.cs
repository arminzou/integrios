using Microsoft.Extensions.Logging;

namespace Integrios.Worker.UnitTests;

// Keeps a per-log-call snapshot of the active scope stack so tests can assert which scope keys entries carry.
// Register via AddSingleton<ILoggerProvider>(provider).
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturingLogger logger = new();

    public List<IReadOnlyList<object?>> Entries => logger.Entries;
    public List<string> Messages => logger.Messages;

    public ILogger CreateLogger(string categoryName) => logger;

    public bool AnyEntryHasScopeKeys(params string[] keys) =>
        Entries.Any(scopes => scopes.Any(scope => ScopeHasKeys(scope, keys)));

    public bool AnyMessageContains(string text) =>
        Messages.Any(message => message.Contains(text, StringComparison.Ordinal));

    private static bool ScopeHasKeys(object? scope, string[] keys)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object>> pairs)
        {
            return false;
        }

        var present = pairs.Select(p => p.Key).ToHashSet();
        return keys.All(present.Contains);
    }

    public void Dispose()
    {
    }
}

internal sealed class CapturingLogger : ILogger
{
    private readonly List<object?> scopes = [];

    public List<IReadOnlyList<object?>> Entries { get; } = [];
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        scopes.Add(state);
        return new Scope(scopes);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(scopes.ToList());
        Messages.Add(formatter(state, exception));
    }

    private sealed class Scope(List<object?> scopes) : IDisposable
    {
        public void Dispose() => scopes.RemoveAt(scopes.Count - 1);
    }
}
