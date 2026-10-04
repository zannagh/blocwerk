// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog.Events;

namespace Blocwerk.Core.Tests;

public class CircuitLifecycleLogHandlerTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605 Mobile/15E148 Safari/604.1", false, "phone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537 Chrome/126 Mobile Safari/537", false, "phone")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537 Chrome/126 Safari/537", false, "tablet")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605 Mobile/15E148", false, "tablet")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605 Safari/605", false, "desktop")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)", true, "kiosk")]
    [InlineData(null, false, "unknown")]
    public void Classify_NamesTheClient(string? userAgent, bool kiosk, string expected) =>
        Assert.Equal(expected, CircuitLifecycleLogHandler.Classify(userAgent, kiosk));

    [Fact]
    public void ShortId_KeepsTheFirstEightCharacters()
    {
        Assert.Equal("abcdefgh", CircuitLifecycleLogHandler.ShortId("abcdefghijkl"));
        Assert.Equal("abc", CircuitLifecycleLogHandler.ShortId("abc"));
        Assert.Equal("?", CircuitLifecycleLogHandler.ShortId(null));
    }

    [Fact]
    public void ALifeWithOneReconnect_LogsOpenDownUpAndClose_WithDurations()
    {
        var time = new StepTime();
        var logger = new ListLogger<CircuitLifecycleLogHandler>();
        var handler = new CircuitLifecycleLogHandler(logger, Substitute.For<IHttpContextAccessor>(), Substitute.For<IKioskContext>(), time);
        handler.SetClient("phone");

        handler.Opened("abcdefghij");
        handler.ConnectionUp("abcdefghij");
        time.Advance(TimeSpan.FromSeconds(30));
        handler.ConnectionDown("abcdefghij");
        time.Advance(TimeSpan.FromMilliseconds(1500));
        handler.ConnectionUp("abcdefghij");
        time.Advance(TimeSpan.FromSeconds(10));
        handler.Closed("abcdefghij");

        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Collection(
            logger.Entries,
            e => Assert.Equal("Circuit abcdefgh opened (phone)", e.Message),
            e => Assert.Equal("Circuit abcdefgh connected (phone)", e.Message),
            e => Assert.Equal("Circuit abcdefgh connection down (phone) after 30 s up", e.Message),
            e => Assert.Equal("Circuit abcdefgh reconnected (phone) after 1500 ms down, reconnect 1", e.Message),
            e => Assert.Equal("Circuit abcdefgh closed (phone) after 41 s, 1 reconnects, closed while connected", e.Message));
    }

    [Fact]
    public void ClosingWhileDown_IsSaid()
    {
        var time = new StepTime();
        var logger = new ListLogger<CircuitLifecycleLogHandler>();
        var handler = new CircuitLifecycleLogHandler(logger, Substitute.For<IHttpContextAccessor>(), Substitute.For<IKioskContext>(), time);

        handler.Opened("id");
        handler.ConnectionDown("id");
        handler.Closed("id");

        Assert.EndsWith("closed while disconnected", logger.Entries[^1].Message);
    }

    [Fact]
    public void LoggingLevels_ComeFromTheEnvironment_OverTheDefaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Microsoft.AspNetCore.Components.Server.Circuits.CircuitRegistry"] = "Warning",
            ["Logging:LogLevel:Blocwerk.Web"] = "Debug",
            ["Logging:LogLevel:Bad"] = "loud",
        }).Build();

        var levels = LoggingLevels.Resolve(configuration);

        Assert.Equal(LogEventLevel.Warning, levels[LoggingLevels.Circuits + ".CircuitRegistry"]);
        Assert.Equal(LogEventLevel.Debug, levels["Blocwerk.Web"]);
        Assert.False(levels.ContainsKey("Bad"));
        Assert.Equal(LogEventLevel.Warning, levels[LoggingLevels.Circuits]);
        Assert.Equal(LogEventLevel.Information, levels["Default"]);
    }

    private sealed class StepTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
