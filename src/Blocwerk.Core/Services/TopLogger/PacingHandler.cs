using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A <see cref="DelegatingHandler"/> that paces outgoing requests to respect a
/// minimum interval plus random jitter, and stamps a browser-like User-Agent.
/// Keeps the client from hammering the unofficial TopLogger API.
/// </summary>
public sealed class PacingHandler : DelegatingHandler
{
    private readonly TopLoggerSettings settings;
    private readonly ILogger<PacingHandler> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Random random = new();
    private long lastSendTicks;

    public PacingHandler(TopLoggerSettings settings, ILogger<PacingHandler> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.UserAgent.Count == 0
            && !string.IsNullOrWhiteSpace(settings.UserAgent)
            && ProductInfoHeaderValue.TryParse(settings.UserAgent, out ProductInfoHeaderValue? parsed)
            && parsed is not null)
        {
            request.Headers.UserAgent.Add(parsed);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan delay = ComputeDelay();
            if (delay > TimeSpan.Zero)
            {
                logger.LogDebug("Pacing outgoing request, delaying for {DelayMs} ms.", delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            lastSendTicks = DateTimeOffset.UtcNow.UtcTicks;
        }
        finally
        {
            gate.Release();
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private TimeSpan ComputeDelay()
    {
        TimeSpan jitter = TimeSpan.Zero;
        if (settings.MaxJitter > TimeSpan.Zero)
        {
            jitter = TimeSpan.FromMilliseconds(random.NextDouble() * settings.MaxJitter.TotalMilliseconds);
        }

        TimeSpan required = settings.MinRequestInterval + jitter;
        if (lastSendTicks == 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(lastSendTicks, TimeSpan.Zero);
        TimeSpan remaining = required - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
