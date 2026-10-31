using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MediaLens.Tests;

internal sealed class CaptureLogs : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedLog> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CaptureLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var ids = new List<string>();
            owner._scopes.ForEachScope((scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object>> fields)
                {
                    foreach (var (key, value) in fields)
                    {
                        if (key == "requestId" && value is string id)
                        {
                            values.Add(id);
                        }
                    }
                }
            }, ids);
            owner.Entries.Enqueue(new CapturedLog(formatter(state, exception), exception?.Message, ids.ToArray()));
        }
    }
}

internal sealed record CapturedLog(string Message, string? ExceptionMessage, string[] RequestIds);
