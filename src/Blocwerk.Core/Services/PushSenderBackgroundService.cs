using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebPush;
using WebPushSubscription = WebPush.PushSubscription;

namespace Blocwerk.Core.Services;

/// <summary>
/// Drains the <see cref="PushSendQueue"/> and performs the actual Web Push HTTP sends, keeping the
/// outbound network off the request/circuit thread. A dead endpoint (HTTP 404/410 Gone) prunes its
/// subscription row; any other failure is logged and swallowed so one bad endpoint never stalls the
/// queue. No-ops entirely when VAPID is unconfigured.
/// </summary>
public sealed class PushSenderBackgroundService : BackgroundService
{
    // A single hung push endpoint must never stall delivery to everyone else: each send gets its own
    // short timeout, and the queue is drained with bounded concurrency so one dead host cannot block
    // the head of the line. Values are deliberately modest — a push is best-effort.
    private const int MaxConcurrentSends = 6;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    // RootDbContextFactory rather than the scoped IDbContextFactory: a hosted service is a singleton
    // and these pruning deletes are outside any user session (matches TelemetryStatsCollector). Held
    // as the interface so the async create is available.
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly PushSendQueue queue;
    private readonly VapidOptions vapid;
    private readonly ILogger<PushSenderBackgroundService> logger;
    private readonly WebPushClient client = new();

    public PushSenderBackgroundService(
        RootDbContextFactory dbContextFactory,
        PushSendQueue queue,
        VapidOptions vapid,
        ILogger<PushSenderBackgroundService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.queue = queue;
        this.vapid = vapid;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!vapid.IsConfigured)
        {
            // The single startup warning for a missing VAPID keypair. Nothing is ever enqueued in this
            // case (every notify method guards on the same flag), so the feature simply no-ops.
            logger.LogWarning(
                "Web Push is disabled: VAPID keys are not configured (set VAPID__SUBJECT, VAPID__PUBLICKEY, VAPID__PRIVATEKEY). Push notifications will not be sent.");
            return;
        }

        var details = new VapidDetails(vapid.Subject, vapid.PublicKey, vapid.PrivateKey);

        // Bounded-concurrency drain: read serially (SingleReader channel) but run up to
        // MaxConcurrentSends in flight so one stalled endpoint cannot block the queue head. Each send
        // isolates its own failure, so faulted tasks are never awaited for their result.
        // Not disposed: it lives for the service lifetime and background send tasks call Release() in
        // their finally, so disposing it on shutdown would race those into ObjectDisposedException.
        // (No wait handle is ever allocated — AvailableWaitHandle is untouched — so there is nothing
        // to leak.)
        var concurrency = new SemaphoreSlim(MaxConcurrentSends);
        var inflight = new List<Task>();

        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            await concurrency.WaitAsync(stoppingToken);
            inflight.Add(Task.Run(async () =>
            {
                try
                {
                    await SendOneAsync(job, details, stoppingToken);
                }
                finally
                {
                    concurrency.Release();
                }
            }, stoppingToken));

            inflight.RemoveAll(t => t.IsCompleted);
        }

        try
        {
            await Task.WhenAll(inflight);
        }
        catch (Exception ex)
        {
            // Shutdown drain: individual sends already swallow their own failures, so anything here is
            // a cancellation as the token trips. Never let it fault the host stop.
            logger.LogDebug(ex, "Push sender drain completed with in-flight cancellations during shutdown.");
        }
    }

    private async Task SendOneAsync(PushSendJob job, VapidDetails details, CancellationToken stoppingToken)
    {
        // Per-send timeout linked to the shutdown token: a hung endpoint is abandoned after
        // SendTimeout instead of holding a concurrency slot for the HttpClient's full ~100s default.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(SendTimeout);

        try
        {
            var subscription = new WebPushSubscription(job.Endpoint, job.P256dh, job.Auth);
            await client.SendNotificationAsync(subscription, job.PayloadJson, details, cts.Token);
        }
        catch (WebPushException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Gone)
        {
            // Prune with the shutdown token, not the (possibly already-cancelled) per-send token.
            await PruneAsync(job.SubscriptionId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Genuine host shutdown — let the drain loop end.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The per-send timeout tripped (stoppingToken is NOT cancelled): drop just this send.
            logger.LogWarning("Web Push send to subscription {SubscriptionId} timed out after {Timeout}s; dropping.",
                job.SubscriptionId, SendTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web Push send failed for subscription {SubscriptionId}; dropping this send.",
                job.SubscriptionId);
        }
    }

    private async Task PruneAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await db.PushSubscriptions
                .Where(s => s.Id == subscriptionId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to prune dead push subscription {SubscriptionId}.", subscriptionId);
        }
    }

    public override void Dispose()
    {
        client.Dispose();
        base.Dispose();
    }
}
