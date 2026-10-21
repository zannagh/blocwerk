// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;
using System.Threading.Channels;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// One open stream (design §7.2): subscribe to the hub first, then replay the client's gap with the catch-up read, then
/// relay the hub's deliveries. The subscription opens before the replay, so nothing falls between the two; an event both
/// deliver is written once, because the stream skips anything at or before the last cursor it wrote.
/// </summary>
/// <remarks>
/// Every envelope, replayed or live, written or skipped as a repeat, is classified for the stream's grant
/// (<see cref="FeedStreamChecks"/>) before anything after it is written: the caller's own <c>access.revoked</c> ends the
/// stream at once, and a role, owner, share-token or wall change is decided again before the next row goes out. The
/// stream also ends when its (jittered) lifetime is up, when the client falls behind the queue, or when the app stops.
/// The browser then reconnects with its last id and is gated afresh.
/// </remarks>
internal sealed class FeedStreamSession(FeedStreamHost host, FeedStreamWriter writer)
{
    private FeedCursor last;

    public async Task RunAsync(FeedStreamRequest request, CancellationToken ct)
    {
        await writer.StartAsync(host.Options.Spread(host.Options.Retry), ct);
        var queue = new FeedStreamQueue(host.Options.QueueCapacity);
        using var subscription = await host.Hub.SubscribeAsync(request.WallId, request.Viewer, queue, ct);
        var bye = await CatchUpAsync(request, ct) ?? await PumpAsync(request, queue.Reader, ct);
        await writer.ByeAsync(bye, ct);
    }

    // Null when the replay is done and the stream goes live; a goodbye reason when it must end.
    private async Task<string?> CatchUpAsync(FeedStreamRequest request, CancellationToken ct)
    {
        var after = request.After;
        last = FeedCatchUp.Parse(after);
        for (var page = 0; page < host.Options.MaxCatchUpPages; page++)
        {
            var changes = await host.CatchUp.ReadWallAsync(request.WallId, request.Viewer, after, host.Options.PageSize, ct);
            if (changes.Resync)
            {
                last = FeedCatchUp.Parse(changes.Next);
                await writer.ResyncAsync(last, ct);
                await writer.FlushAsync(ct);
                return null;
            }

            var bye = await EmitAsync(changes.Events, request, ct);
            await writer.FlushAsync(ct);
            if (bye is not null)
            {
                return bye;
            }

            last = FeedCatchUp.Parse(changes.Next);
            if (!changes.More)
            {
                return null;
            }

            after = changes.Next;
        }

        // A backlog this long is cheaper to reload than to replay: the client reloads and continues from the mark.
        last = await host.CatchUp.MarkAsync(request.WallId, request.Viewer, ct);
        await writer.ResyncAsync(last, ct);
        await writer.FlushAsync(ct);
        return null;
    }

    private async Task<string> PumpAsync(FeedStreamRequest request, ChannelReader<FeedDelivery> reader, CancellationToken ct)
    {
        var lifetime = host.Options.Spread(host.Options.Lifetime);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var left = lifetime - clock.Elapsed;
            if (left <= TimeSpan.Zero || host.Stopping.IsCancellationRequested)
            {
                return FeedStreamEvents.ByeLifetime;
            }

            var ready = await WaitAsync(reader, left < host.Options.Heartbeat ? left : host.Options.Heartbeat, host.Stopping, ct);
            if (ready is null)
            {
                if (!host.Stopping.IsCancellationRequested)
                {
                    await writer.PingAsync(ct);
                }

                continue;
            }

            if (ready == false)
            {
                return FeedStreamEvents.ByeOverflow; // only an overflow completes the queue
            }

            if (await DrainAsync(reader, request, ct) is { } bye)
            {
                return bye;
            }
        }
    }

    // True when a delivery is waiting, false when the queue was closed, null when the wait ran out (idle) or the app stops.
    private static async Task<bool?> WaitAsync(
        ChannelReader<FeedDelivery> reader, TimeSpan wait, CancellationToken stopping, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        timeout.CancelAfter(wait);
        try
        {
            return await reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string?> DrainAsync(ChannelReader<FeedDelivery> reader, FeedStreamRequest request, CancellationToken ct)
    {
        string? bye = null;
        while (bye is null && reader.TryRead(out var delivery))
        {
            if (delivery.Resync || delivery.Cursor.Epoch != last.Epoch)
            {
                last = delivery.Cursor;
                await writer.ResyncAsync(last, ct);
                continue;
            }

            bye = await EmitAsync(delivery.Events, request, ct);
        }

        await writer.FlushAsync(ct);
        return bye;
    }

    /// <summary>
    /// Writes the envelopes not yet written, in order. Each is classified first, repeats included: a revocation ends the
    /// stream right after it, and after a change that may move the grant, access is decided again before the next row is
    /// written (and once more at the end of the batch). Null to go on, else the goodbye reason.
    /// </summary>
    private async Task<string?> EmitAsync(IReadOnlyList<FeedEnvelope> envelopes, FeedStreamRequest request, CancellationToken ct)
    {
        var recheck = false;
        foreach (var envelope in envelopes)
        {
            var check = FeedStreamChecks.Of(envelope, request.Viewer);
            if (FeedCursor.TryParse(envelope.Cursor, out var at) && !(at.Epoch == last.Epoch && at.Position <= last.Position))
            {
                if (recheck && !await StillGrantedAsync(request, ct))
                {
                    return FeedStreamEvents.ByeAccess;
                }

                recheck = false;
                await writer.EventAsync(envelope, ct);
                last = at;
            }

            if (check == FeedStreamCheck.Revoked)
            {
                return FeedStreamEvents.ByeAccess; // nothing after the caller's own revocation reaches them under the old grant
            }

            recheck |= check == FeedStreamCheck.Recheck;
        }

        return recheck && !await StillGrantedAsync(request, ct) ? FeedStreamEvents.ByeAccess : null;
    }

    private static async Task<bool> StillGrantedAsync(FeedStreamRequest request, CancellationToken ct) =>
        await request.Recheck(ct) is { } now && now.SameAs(request.Viewer);
}
