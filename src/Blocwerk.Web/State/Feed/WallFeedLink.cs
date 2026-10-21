// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// A circuit's subscriptions to the live feed (design §5.5), one per wall it has cached, as the viewer it is on that wall
/// (<see cref="ICircuitFeedViewer"/>). Scoped, so it lives and dies with the circuit.
/// <para>
/// <b>Delivery.</b> The hub calls in synchronously on its loop under the wall's gate. <see cref="Delivered"/> is raised
/// right there and must only evict and hand off (a component's <c>InvokeAsync</c>, never awaited). Anything that waits,
/// a resubscribe or leaving the wall, runs on the thread pool: subscribing takes the very gate the hub holds.
/// </para>
/// <para>
/// <b>Access.</b> <c>access.revoked</c> with a role is a demotion: resubscribe with that role's audiences. Without one it
/// is a removal: leave the wall (<see cref="AccessLost"/>). Promotions and ownership transfers write no access event, so
/// a <c>member.changed</c> of the circuit's own user, or a <c>wall.updated</c> that moved the owner, re-reads the viewer
/// from the database and resubscribes when its audiences changed (<see cref="ViewerChanged"/>). The last
/// access-relevant event of a batch wins.
/// </para>
/// </summary>
public sealed partial class WallFeedLink(IWallFeedHub hub, IWallFeed feed, ICircuitFeedViewer viewers, ILogger<WallFeedLink> logger)
    : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Task<WallFeedSlot?>> slots = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private int disposed;
    private int deniedEpoch;
    private Guid? pinned;

    /// <summary>Raised on the hub's loop for every delivery; a resync, or a delivery of another epoch, has Resync set.</summary>
    public event Action<FeedDelivery>? Delivered;

    /// <summary>Raised (on the thread pool) when the circuit lost its access to a wall and left its feed.</summary>
    public event Action<Guid>? AccessLost;

    /// <summary>Raised (on the thread pool) after a resubscribe gave the circuit other audiences on a wall.</summary>
    public event Action<Guid>? ViewerChanged;

    private bool IsDisposed => Volatile.Read(ref disposed) == 1;

    /// <summary>The <c>CursorAtLoad</c>, taken before a query; null when the feed cannot be read.</summary>
    public async Task<FeedCursor?> CursorAtLoadAsync()
    {
        try
        {
            return await feed.CurrentAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the feed cursor before a load; the load is evicted by any event");
            return null;
        }
    }

    /// <summary>
    /// The circuit's subscription to the wall, opened on first use; null when it has none. A "no access" answer is kept, so
    /// a signed-in non-member does not read their role again on every cache miss (<see cref="ForgetDenied"/> clears it); a
    /// failure is not, and the next load tries again.
    /// </summary>
    public async Task<WallFeedSlot?> EnsureAsync(Guid wallId)
    {
        if (IsDisposed || wallId == Guid.Empty)
        {
            return null;
        }

        // Twice at most: a slot that Release retired between the lookup and here is replaced once.
        for (var attempt = 0; ; attempt++)
        {
            var task = slots.GetOrAdd(wallId, id => OpenAsync(id, null));
            WallFeedSlot? slot;
            try
            {
                slot = await task;
            }
            catch (Exception ex)
            {
                slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task));
                logger.LogWarning(ex, "Could not subscribe to the feed of wall {WallId}; its cache relies on this process's own commits only", wallId);
                return null;
            }

            if (slot is not { IsRetired: true } || IsDisposed || attempt == 1)
            {
                return slot is { IsRetired: true } ? null : slot;
            }

            slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task));
        }
    }

    /// <summary>
    /// Keeps <paramref name="wallId"/> (the wall of the page on screen) subscribed whatever <see cref="Release"/> is asked,
    /// so a removal reaches every page of it; null unpins.
    /// </summary>
    public void Pin(Guid? wallId) => pinned = wallId;

    /// <summary>
    /// False when <paramref name="load"/> may miss events: it was read before the wall's subscription started, so the
    /// events between its cursor and the start reach nobody. The caller reloads.
    /// </summary>
    /// <typeparam name="T">The cached value.</typeparam>
    public async Task<bool> CoversAsync<T>(Guid wallId, CacheLoad<T> load)
    {
        var slot = await EnsureAsync(wallId);
        return slot?.Start is not { } start || load.Covers(start);
    }

    /// <summary>The viewer the circuit is subscribed to the wall as, or null.</summary>
    public FeedViewer? ViewerOf(Guid wallId) =>
        slots.TryGetValue(wallId, out var task) && task.IsCompletedSuccessfully ? task.Result?.Viewer : null;

    /// <summary>
    /// The viewer of a live subscription: null as well once the slot is revoked or retired, so a demotion stops what the
    /// old audiences received at once, before the resubscribe lands (job pulses read it at every delivery).
    /// </summary>
    public FeedViewer? ActiveViewerOf(Guid wallId) =>
        slots.TryGetValue(wallId, out var task) && task.IsCompletedSuccessfully && task.Result is { IsRevoked: false, IsRetired: false } slot
            ? slot.Viewer
            : null;

    /// <summary>
    /// Forgets the kept "no access" answers (the user's set of walls changed). An answer still being read when this runs
    /// may predate the change, so that read is repeated before it is kept (<see cref="ResolveAsync"/>).
    /// </summary>
    public void ForgetDenied()
    {
        Interlocked.Increment(ref deniedEpoch);
        foreach (var (wallId, task) in slots)
        {
            if (task.IsCompletedSuccessfully && task.Result is null)
            {
                slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task));
            }
        }
    }

    /// <summary>
    /// Unsubscribes from every wall that <paramref name="keep"/> declines (the circuit caches nothing of it), except the
    /// pinned one. A concurrent <see cref="EnsureAsync"/> that got a slot retired here subscribes again.
    /// </summary>
    public void Release(Func<Guid, bool> keep)
    {
        foreach (var (wallId, task) in slots)
        {
            if (wallId != pinned && task.IsCompletedSuccessfully && task.Result is { } slot && !keep(wallId)
                && slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task)))
            {
                slot.Retire();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        foreach (var task in slots.Values)
        {
            // A subscribe still in flight is retired when it lands, so it cannot outlive the circuit.
            _ = task.ContinueWith(t => RetireIfOpened(t), TaskScheduler.Default);
        }

        slots.Clear();
    }

    private static void RetireIfOpened(Task<WallFeedSlot?> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            task.Result?.Retire();
        }
    }

    // Throws on a failed resolve or subscribe; null means "no access".
    private async Task<WallFeedSlot?> OpenAsync(Guid wallId, FeedViewer? viewer)
    {
        viewer ??= await ResolveAsync(wallId);
        if (viewer is null)
        {
            return null;
        }

        var slot = new WallFeedSlot(wallId, viewer, Receive);
        slot.Attach(await hub.SubscribeAsync(wallId, viewer, slot));
        if (IsDisposed)
        {
            slot.Retire(); // the circuit went while the subscribe ran
            return null;
        }

        return slot;
    }

    // A "no access" answer read while ForgetDenied ran may be from before a join: read once more. Bounded, because every
    // repeat needs another ForgetDenied during the read.
    private async Task<FeedViewer?> ResolveAsync(Guid wallId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var epoch = Volatile.Read(ref deniedEpoch);
            var viewer = await viewers.ResolveAsync(wallId);
            if (viewer is not null || epoch == Volatile.Read(ref deniedEpoch) || attempt == 3)
            {
                return viewer;
            }
        }
    }

    // On the hub's loop, under the wall's gate: no waiting here, and no call back into the hub. A batch is cut after its
    // first access.revoked and the slot is revoked on the spot: the rest was narrowed to audiences the viewer lost.
    private void Receive(WallFeedSlot slot, FeedDelivery delivery)
    {
        if (IsDisposed)
        {
            return; // the one late delivery after Dispose
        }

        if (slot.Epoch is { } epoch && epoch != delivery.Cursor.Epoch)
        {
            delivery = delivery with { Resync = true };
        }

        slot.Epoch = delivery.Cursor.Epoch;
        var events = delivery.Events;
        var cut = slot.Viewer.UserId is null ? -1 : events.ToList().FindIndex(e => e.Kind == FeedKinds.AccessRevoked);
        if (cut >= 0)
        {
            slot.Revoke();
            delivery = delivery with { Events = events.Take(cut + 1).ToList() };
        }

        Delivered?.Invoke(delivery);
        if (slot.Viewer.UserId is { } user)
        {
            Schedule(slot.WallId, user, WallFeedAccess.Decide(events, user));
        }
    }

    private void Schedule(Guid wallId, Guid user, WallFeedAccessAction action)
    {
        switch (action.Kind)
        {
            case WallFeedAccessKind.Leave:
                Background(() => LeaveAsync(wallId));
                break;
            case WallFeedAccessKind.Demote:
                Background(() => ChangeAsync(wallId, FeedViewers.ForRoleName(action.Role, user)));
                break;
            case WallFeedAccessKind.Reread:
                Background(() => ChangeAsync(wallId, null));
                break;
        }
    }

    private void Background(Func<Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A feed resubscribe failed; the circuit keeps its current subscription");
        }
    });
}
