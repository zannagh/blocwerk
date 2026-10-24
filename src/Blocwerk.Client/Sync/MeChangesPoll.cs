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
/// It starts from its own stored cursor, else the oldest cursor of the stored marks, so a revocation while the device
/// was away still arrives; it checks that each answer is for this partition's user, and it ends on <see cref="Cancel"/>.
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
    private CancellationTokenSource? cancel;

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

        cancel = new CancellationTokenSource();
        running = RunAsync(cancel.Token);
        return running;
    }

    /// <summary>Ends a running poll (the tab hid, the label changed): its wait ends, and nothing more is asked or applied.</summary>
    public void Cancel()
    {
        cancel?.Cancel();
        running = null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var partition = store.Partition;
        if (!SyncLabels.IsUser(partition))
        {
            return;
        }

        if (cursorPartition != partition)
        {
            cursorPartition = partition;
            cursor = await transport.SavedMeCursorAsync(partition!) ?? SyncCursor.Earliest(MineCursors());
        }

        // The run reads and moves its own copy: a run cancelled mid-page (the label changed meanwhile) never writes the
        // shared cursor, so another label's poll can never resume from this one's position.
        var at = cursor;
        for (var page = 0; page < MaxPages && !stop.IsCancellationRequested; page++)
        {
            var answer = await transport.MyChangesAsync(at);
            if (answer.Status == 429 && page == 0)
            {
                try
                {
                    await clock.Delay(SyncDelays.RetryAfter(answer.RetryAfter, clock.NextJitter()), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (!Current(stop, partition))
            {
                return;
            }

            if (SyncLabels.EndsSession(answer.Status))
            {
                await refusals.RefusedAsync(partition!, Guid.Empty, answer.Status, answer.Reason, mine: false);
                return;
            }

            // Rows of whoever the cookie names now: another user signing in elsewhere is never this partition's.
            if (answer.Status != 200 || answer.Partition != partition
                || SyncJson.Read(answer.Body, SyncJsonContext.Default.FeedChanges) is not { } changes)
            {
                return;
            }

            var more = await PageAsync(partition!, changes);
            if (!Current(stop, partition))
            {
                return;
            }

            at = changes.Next;
            cursor = at;
            await transport.SaveMeCursorAsync(partition!, at);
            if (!more)
            {
                return;
            }
        }
    }

    // Still this run's to write: not cancelled, and the store and the cursor still name its label.
    private bool Current(CancellationToken stop, string? partition) =>
        !stop.IsCancellationRequested && store.Partition == partition && cursorPartition == partition;

    // One page; true when another follows.
    private async Task<bool> PageAsync(string partition, FeedChanges changes)
    {
        if (changes.Resync)
        {
            await AllMarksAsync(partition);
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

    // The cursors of the stored marks. A wall without a marks row (a share link's wall under u-, which /mine refuses)
    // has nothing to resume and does not hold the poll back; with no marks at all the first poll resyncs once, and its
    // cursor is stored for the next start.
    private IEnumerable<string?> MineCursors() =>
        store.MineRows.Where(m => store.Walls.Any(w => w.WallId == m.WallId)).Select(m => m.Cursor);
}
