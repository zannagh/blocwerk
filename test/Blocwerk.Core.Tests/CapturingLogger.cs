// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Tests;

/// <summary>Keeps every formatted log line with its level, for tests that assert what was logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> lines = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (lines)
            {
                return lines.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (lines)
        {
            lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
