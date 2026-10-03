using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>An <see cref="ILogger"/> that writes each message (information and up) to the test output.</summary>
internal sealed class TestOutputLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            output.WriteLine("  " + formatter(state, exception) + (exception is null ? string.Empty : $" ({exception.Message})"));
        }
    }
}
