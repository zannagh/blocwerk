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
/// a <c>member.changed</c> of the circuit's own user, or any <c>wall.updated</c> for a signed-in viewer, re-reads the
/// viewer from the database and resubscribes when its audiences changed (<see cref="ViewerChanged"/>).
/// </para>
/// </summary>
public sealed class WallFeedLink(IWallFeedHub hub, IWallFeed feed, ICircuitFeedViewer viewers, ILogger<WallFeedLink> logger)
    : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Task<WallFeedSlot?>> slots = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private int disposed;

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

    /// <summary>The circuit's subscription to the wall, opened on first use; null when it has none (no access, failure).</summary>
    public async Task<WallFeedSlot?> EnsureAsync(Guid wallId)
    {
        if (IsDisposed || wallId == Guid.Empty)
        {
            return null;
        }

        var task = slots.GetOrAdd(wallId, id => OpenAsync(id, null));
        var slot = await task;
        if (slot is null)
        {
            slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task)); // the next load tries again
        }

        return slot;
    }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        foreach (var task in slots.Values)
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result?.Retire();
            }
        }

        slots.Clear();
    }

    private async Task<WallFeedSlot?> OpenAsync(Guid wallId, FeedViewer? viewer)
    {
        try
        {
            viewer ??= await viewers.ResolveAsync(wallId);
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not subscribe to the feed of wall {WallId}; its cache relies on the domain-change path", wallId);
            return null;
        }
    }

    // On the hub's loop, under the wall's gate: no waiting here.
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
        Delivered?.Invoke(delivery);
        React(slot, delivery.Events);
    }

    private void React(WallFeedSlot slot, IReadOnlyList<FeedEnvelope> events)
    {
        var me = slot.Viewer.UserId;
        var revoked = events.LastOrDefault(e => e.Kind == FeedKinds.AccessRevoked);
        if (revoked is not null && me is { } user)
        {
            if (WallFeedAccess.Role(revoked) is { } role)
            {
                Background(() => ChangeAsync(slot.WallId, FeedViewers.ForRoleName(role, user)));
            }
            else
            {
                Background(() => LeaveAsync(slot.WallId));
            }

            return;
        }

        if (me is not null && events.Any(e => WallFeedAccess.MayChangeViewer(e, me.Value)))
        {
            Background(() => ChangeAsync(slot.WallId, null));
        }
    }

    // Resubscribes as viewer (null: read it from the database again), when its audiences differ from the current ones.
    private async Task ChangeAsync(Guid wallId, FeedViewer? viewer)
    {
        await changes.WaitAsync();
        try
        {
            if (IsDisposed || !slots.TryGetValue(wallId, out var task) || await task is not { } current)
            {
                return;
            }

            viewer ??= await viewers.ResolveAsync(wallId);
            if (viewer is null)
            {
                LeaveCore(wallId);
                return;
            }

            if (viewer.SameAs(current.Viewer) || await OpenAsync(wallId, viewer) is not { } fresh)
            {
                return;
            }

            slots[wallId] = Task.FromResult<WallFeedSlot?>(fresh);
            current.Retire();
            ViewerChanged?.Invoke(wallId);
        }
        finally
        {
            changes.Release();
        }
    }

    private async Task LeaveAsync(Guid wallId)
    {
        await changes.WaitAsync();
        try
        {
            LeaveCore(wallId);
        }
        finally
        {
            changes.Release();
        }
    }

    private void LeaveCore(Guid wallId)
    {
        if (slots.TryRemove(wallId, out var task) && task.IsCompletedSuccessfully)
        {
            task.Result?.Retire();
        }

        if (!IsDisposed)
        {
            AccessLost?.Invoke(wallId);
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
