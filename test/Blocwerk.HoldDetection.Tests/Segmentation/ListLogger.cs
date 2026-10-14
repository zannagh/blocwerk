// <copyright file="ListLogger.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.Logging;

namespace Blocwerk.HoldDetection.Tests.Segmentation;

/// <summary>A logger that keeps its warnings.</summary>
internal sealed class ListLogger : ILogger
{
    /// <summary>Gets the warnings logged so far.</summary>
    public List<string> Warnings { get; } = [];

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Warnings.Add(formatter(state, exception));
        }
    }
}
