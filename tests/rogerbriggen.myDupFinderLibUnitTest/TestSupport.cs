// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RogerBriggen.MyDupFinderLibUnitTest;

/// <summary>
/// A clock that only moves when the test says so.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan time) => _timestamp += time.Ticks;
}

internal sealed record LogEntry(LogLevel Level, string Message);

/// <summary>
/// Keeps all log messages in memory so tests can look at them.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = new List<LogEntry>();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    public void Dispose()
    {
    }

    private sealed class ListLogger : ILogger
    {
        private readonly ListLoggerProvider _provider;

        public ListLogger(ListLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_provider._entries)
            {
                _provider._entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
