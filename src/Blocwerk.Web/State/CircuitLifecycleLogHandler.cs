// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Blocwerk.Web.State;

/// <summary>
/// Logs a circuit's life at Information, a handful of lines per circuit: opened, connection up, connection down,
/// closed, each with a short circuit id, the client class (phone, tablet, desktop, kiosk) and how long it lasted.
/// This is what shows phone reconnect churn (a tab going down and up again) in the server log; the framework's own
/// evictions are logged by the Circuits categories (see <see cref="LoggingLevels"/>). Registered per circuit.
/// </summary>
public sealed class CircuitLifecycleLogHandler : CircuitHandler
{
    private readonly ILogger<CircuitLifecycleLogHandler> logger;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IKioskContext kioskContext;
    private readonly TimeProvider time;

    private string client = "unknown";
    private DateTimeOffset openedAt;
    private DateTimeOffset? downSince;
    private DateTimeOffset upSince;
    private int reconnects;

    public CircuitLifecycleLogHandler(
        ILogger<CircuitLifecycleLogHandler> logger,
        IHttpContextAccessor httpContextAccessor,
        IKioskContext kioskContext)
        : this(logger, httpContextAccessor, kioskContext, TimeProvider.System)
    {
    }

    internal CircuitLifecycleLogHandler(
        ILogger<CircuitLifecycleLogHandler> logger,
        IHttpContextAccessor httpContextAccessor,
        IKioskContext kioskContext,
        TimeProvider time)
    {
        this.logger = logger;
        this.httpContextAccessor = httpContextAccessor;
        this.kioskContext = kioskContext;
        this.time = time;
    }

    /// <summary>The first characters of a circuit id: enough to follow one circuit through the log.</summary>
    public static string ShortId(string? circuitId) =>
        string.IsNullOrEmpty(circuitId) ? "?" : circuitId[..Math.Min(8, circuitId.Length)];

    /// <summary>phone, tablet or desktop from a user agent; a registered kiosk is always "kiosk".</summary>
    public static string Classify(string? userAgent, bool kiosk)
    {
        if (kiosk)
        {
            return "kiosk";
        }

        if (string.IsNullOrEmpty(userAgent))
        {
            return "unknown";
        }

        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
            (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) && !userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)))
        {
            return "tablet";
        }

        return userAgent.Contains("Mobi", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            ? "phone"
            : "desktop";
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await kioskContext.InitializeAsync();
        client = Classify(httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString(), kioskContext.IsKiosk);
        Opened(circuit.Id);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ConnectionUp(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ConnectionDown(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Closed(circuit.Id);
        return Task.CompletedTask;
    }

    internal void SetClient(string value) => client = value;

    internal void Opened(string circuitId)
    {
        openedAt = time.GetUtcNow();
        upSince = openedAt;
        logger.LogInformation("Circuit {CircuitId} opened ({Client})", ShortId(circuitId), client);
    }

    internal void ConnectionUp(string circuitId)
    {
        var now = time.GetUtcNow();
        if (downSince is { } down)
        {
            reconnects++;
            downSince = null;
            logger.LogInformation(
                "Circuit {CircuitId} reconnected ({Client}) after {DownMs} ms down, reconnect {Reconnects}",
                ShortId(circuitId),
                client,
                (long)(now - down).TotalMilliseconds,
                reconnects);
        }
        else
        {
            logger.LogInformation("Circuit {CircuitId} connected ({Client})", ShortId(circuitId), client);
        }

        upSince = now;
    }

    internal void ConnectionDown(string circuitId)
    {
        var now = time.GetUtcNow();
        downSince = now;
        logger.LogInformation(
            "Circuit {CircuitId} connection down ({Client}) after {UpSeconds} s up",
            ShortId(circuitId),
            client,
            (long)(now - upSince).TotalSeconds);
    }

    internal void Closed(string circuitId)
    {
        var now = time.GetUtcNow();
        logger.LogInformation(
            "Circuit {CircuitId} closed ({Client}) after {LifetimeSeconds} s, {Reconnects} reconnects, {State}",
            ShortId(circuitId),
            client,
            (long)(now - openedAt).TotalSeconds,
            reconnects,
            downSince is null ? "closed while connected" : "closed while disconnected");
    }
}
