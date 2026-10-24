// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The signed-in viewer's own rows across walls (<c>GET /api/me/changes</c>, plan §1 step 5), polled when the tab
/// becomes visible and when the browser comes back online; <c>/api/me</c> has no stream. A <c>u-</c> device only: a
/// share link has no own rows, and a kiosk polls nothing.
/// <list type="bullet">
/// <item><c>access.revoked</c> drops that wall (the identity matrix's 403);</item>
/// <item><c>walls.changed</c> re-reads the device's wall list;</item>
/// <item><c>ticks.resync</c>, <c>user.resync</c> and a resync page refetch the marks of every cached wall (no wall
/// stream carries them); <c>tick.mine</c> and <c>favorite.changed</c> refetch that wall's;</item>
/// <item>own ratings are not offline: <c>rating.mine</c> is ignored, as is every other kind.</item>
/// </list>
/// It starts from the oldest cursor of the stored marks, so a revocation while the device was away still arrives.
/// </summary>
internal sealed class MeChangesPoll
{
    // A full page is followed at once; this many pages per poll at most, so a long backlog cannot spin.
    private const int MaxPages = 8;

    private readonly ClientStore store;
    private readonly ISyncTransport transport;
    private readonly ISyncClock clock;
    private readonly ISyncRefusals refusals;
    private string? cursor;
    private string? cursorPartition;
    private Task? running;

    public MeChangesPoll(ClientStore store, ISyncTransport transport, ISyncClock clock, ISyncRefusals refusals)
    {
        this.store = store;
        this.transport = transport;
        this.clock = clock;
        this.refusals = refusals;
    }

    /// <summary>Polls once, unless a poll runs (then that one is the answer).</summary>
    /// <returns>A task that completes when the poll ended.</returns>
    public Task PollAsync()
    {
        if (running is { IsCompleted: false })
        {
            return running;
        }

        running = RunAsync();
        return running;
    }

    private async Task RunAsync()
    {
        var partition = store.Partition;
        if (!SyncLabels.IsUser(partition))
        {
            return;
        }

        if (cursorPartition != partition)
        {
            cursorPartition = partition;
            cursor = SyncCursor.Earliest(store.Walls.Count == 0 ? [] : MineCursors());
        }

        for (var page = 0; page < MaxPages; page++)
        {
            var answer = await transport.MyChangesAsync(cursor);
            if (answer.Status == 429 && page == 0)
            {
                await clock.Delay(SyncDelays.RetryAfter(answer.RetryAfter, clock.NextJitter()), CancellationToken.None);
                continue;
            }

            if (SyncLabels.EndsSession(answer.Status))
            {
                await refusals.RefusedAsync(partition!, Guid.Empty, answer.Status, answer.Reason, mine: false);
                return;
            }

            if (answer.Status != 200 || SyncJson.Read(answer.Body, SyncJsonContext.Default.FeedChanges) is not { } changes
                || store.Partition != partition)
            {
                return;
            }

            if (!await PageAsync(partition!, changes))
            {
                return;
            }
        }
    }

    // One page; true when another follows.
    private async Task<bool> PageAsync(string partition, FeedChanges changes)
    {
        if (changes.Resync)
        {
            await AllMarksAsync(partition);
            cursor = changes.Next;
            return false;
        }

        var marks = new HashSet<Guid>();
        var all = false;
        foreach (var envelope in changes.Events)
        {
            switch (envelope.Kind)
            {
                case FeedKinds.AccessRevoked when (envelope.EntityId ?? envelope.WallId) is { } wallId:
                    await refusals.RefusedAsync(partition, wallId, 403, null, mine: false);
                    break;
                case FeedKinds.WallsChanged:
                    await store.RefreshAsync();
                    break;
                case FeedKinds.TicksResync or FeedKinds.UserResync:
                    all = true;
                    break;
                case FeedKinds.TickMine or FeedKinds.FavoriteChanged when envelope.WallId is { } wallId:
                    marks.Add(wallId);
                    break;
            }
        }

        if (all)
        {
            await AllMarksAsync(partition);
        }
        else
        {
            foreach (var wallId in marks.Where(w => store.Walls.Any(row => row.WallId == w)))
            {
                await MineRefresh.RunAsync(store, transport, refusals, partition, wallId);
            }

            if (marks.Count > 0)
            {
                await store.RefreshAsync();
            }
        }

        cursor = changes.Next;
        return changes.More;
    }

    private async Task AllMarksAsync(string partition)
    {
        foreach (var row in store.Walls.ToList())
        {
            await MineRefresh.RunAsync(store, transport, refusals, partition, row.WallId);
        }

        await store.RefreshAsync();
    }

    // Every cached wall's stored marks; a wall without any has no cursor, so the poll starts with a resync.
    private IEnumerable<string?> MineCursors() =>
        store.Walls.Select(w => store.MineRows.FirstOrDefault(m => m.WallId == w.WallId)?.Cursor);
}
