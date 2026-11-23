using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GrayMint.Webhooks.Test.Helper;

// Keeps what a host logs, for a test of what goes to the log and nowhere else.
public sealed class TestLog : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    public IReadOnlyList<TestLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName)
    {
        return this;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new TestLogEntry { Level = logLevel, Message = formatter(state, exception), Exception = exception });
    }

    public void Dispose()
    {
    }
}
