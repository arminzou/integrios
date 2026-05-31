using Microsoft.Extensions.Logging;

namespace Integrios.Worker.Tests;

// Records the active scope stack at each log call so tests can assert which scope keys a
// handler's log entries carry. Register via AddSingleton<ILoggerProvider>(provider).
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturingLogger _logger = new();

    public List<IReadOnlyList<object?>> Entries => _logger.Entries;

    public ILogger CreateLogger(string categoryName) => _logger;

    public bool AnyEntryHasScopeKeys(params string[] keys) =>
        Entries.Any(scopes => scopes.Any(scope => ScopeHasKeys(scope, keys)));

    private static bool ScopeHasKeys(object? scope, string[] keys)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object>> pairs)
            return false;

        var present = pairs.Select(p => p.Key).ToHashSet();
        return keys.All(present.Contains);
    }

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<object?> _scopes = [];

        public List<IReadOnlyList<object?>> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Add(state);
            return new Scope(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(_scopes.ToList());

        private sealed class Scope(List<object?> scopes) : IDisposable
        {
            public void Dispose() => scopes.RemoveAt(scopes.Count - 1);
        }
    }
}
