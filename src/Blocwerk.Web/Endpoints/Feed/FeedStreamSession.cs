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
/// The stream ends with a <c>bye</c> when its lifetime is up, when the client falls behind the queue, or when its access
/// changed: an <c>access.revoked</c> of its own ends it at once, and a role, owner, share-token or wall change is
/// decided again and ends it when the grant differs. The app stopping ends it like its lifetime. The browser then
/// reconnects with its last id and is gated afresh.
/// </remarks>
internal sealed class FeedStreamSession(FeedStreamHost host, FeedStreamWriter writer)
{
    private FeedCursor last;

    public async Task RunAsync(FeedStreamRequest request, CancellationToken ct)
    {
        await writer.StartAsync(host.Options.Retry, ct);
        var queue = new FeedStreamQueue(host.Options.QueueCapacity);
        using var subscription = await host.Hub.SubscribeAsync(request.WallId, request.Viewer, queue, ct);
        await CatchUpAsync(request, ct);
        await writer.ByeAsync(await PumpAsync(request, queue.Reader, ct), ct);
    }

    private async Task CatchUpAsync(FeedStreamRequest request, CancellationToken ct)
    {
        var after = request.After;
        for (var page = 0; page < host.Options.MaxCatchUpPages; page++)
        {
            var changes = await host.CatchUp.ReadWallAsync(request.WallId, request.Viewer, after, host.Options.PageSize, ct);
            last = FeedCatchUp.Parse(changes.Next);
            if (changes.Resync)
            {
                await writer.ResyncAsync(last, ct);
                await writer.FlushAsync(ct);
                return;
            }

            foreach (var envelope in changes.Events)
            {
                await writer.EventAsync(envelope, ct);
            }

            await writer.FlushAsync(ct);
            if (!changes.More)
            {
                return;
            }

            after = changes.Next;
        }

        // A backlog this long is cheaper to reload than to replay: the client reloads and continues from the mark.
        last = await host.Feed.CurrentAsync(ct);
        await writer.ResyncAsync(last, ct);
        await writer.FlushAsync(ct);
    }

    private async Task<string> PumpAsync(FeedStreamRequest request, ChannelReader<FeedDelivery> reader, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var left = host.Options.Lifetime - clock.Elapsed;
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

            var check = await DrainAsync(reader, request.Viewer, ct);
            if (check == FeedStreamCheck.Revoked || (check == FeedStreamCheck.Recheck && !await StillGrantedAsync(request, ct)))
            {
                return FeedStreamEvents.ByeAccess;
            }
        }
    }

    private static async Task<bool> StillGrantedAsync(FeedStreamRequest request, CancellationToken ct) =>
        await request.Recheck(ct) is { } now && now.SameAs(request.Viewer);

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

    private async Task<FeedStreamCheck> DrainAsync(ChannelReader<FeedDelivery> reader, FeedViewer viewer, CancellationToken ct)
    {
        var check = FeedStreamCheck.None;
        while (check != FeedStreamCheck.Revoked && reader.TryRead(out var delivery))
        {
            check = FeedStreamChecks.Max(check, await WriteAsync(delivery, viewer, ct));
        }

        await writer.FlushAsync(ct);
        return check;
    }

    private async Task<FeedStreamCheck> WriteAsync(FeedDelivery delivery, FeedViewer viewer, CancellationToken ct)
    {
        if (delivery.Resync || delivery.Cursor.Epoch != last.Epoch)
        {
            last = delivery.Cursor;
            await writer.ResyncAsync(last, ct);
            return FeedStreamCheck.None;
        }

        var check = FeedStreamCheck.None;
        foreach (var envelope in delivery.Events)
        {
            if (!FeedCursor.TryParse(envelope.Cursor, out var at) || at.Position <= last.Position)
            {
                continue; // already written by the catch-up
            }

            await writer.EventAsync(envelope, ct);
            last = at;
            check = FeedStreamChecks.Max(check, FeedStreamChecks.Of(envelope, viewer));
            if (check == FeedStreamCheck.Revoked)
            {
                break; // nothing after the caller's own revocation reaches them under the old grant
            }
        }

        return check;
    }
}
