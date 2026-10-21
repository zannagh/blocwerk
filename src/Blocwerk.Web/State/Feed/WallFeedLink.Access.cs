// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>The access side of <see cref="WallFeedLink"/>: resubscribing as another viewer, and leaving a wall.</summary>
public sealed partial class WallFeedLink
{
    // Resubscribes as viewer (null: read it from the database again) when its audiences differ, or the slot was revoked.
    private async Task ChangeAsync(Guid wallId, FeedViewer? viewer)
    {
        await changes.WaitAsync();
        try
        {
            // Awaited, not peeked: a revoke can arrive in the first batch, while the subscribe that delivered it is still
            // returning. Safe here, off the hub's gate (Background runs on the thread pool).
            if (IsDisposed || !slots.TryGetValue(wallId, out var task) || await OpenedOrNullAsync(task) is not { } current)
            {
                return;
            }

            await SwapAsync(wallId, task, current, viewer);
        }
        finally
        {
            changes.Release();
        }
    }

    private static async Task<WallFeedSlot?> OpenedOrNullAsync(Task<WallFeedSlot?> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return null; // EnsureAsync logs and drops a failed subscribe
        }
    }

    private async Task SwapAsync(Guid wallId, Task<WallFeedSlot?> task, WallFeedSlot current, FeedViewer? viewer)
    {
        WallFeedSlot? fresh = null;
        try
        {
            viewer ??= await viewers.ResolveAsync(wallId);
            if (viewer is null)
            {
                LeaveCore(wallId);
                return;
            }

            if (!current.IsRevoked && viewer.SameAs(current.Viewer))
            {
                return;
            }

            fresh = await OpenAsync(wallId, viewer);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A feed resubscribe of wall {WallId} failed", wallId);
        }

        if (fresh is null || !slots.TryUpdate(wallId, Task.FromResult<WallFeedSlot?>(fresh), task))
        {
            fresh?.Retire(); // released or left meanwhile
            if (current.IsRevoked && slots.TryRemove(new KeyValuePair<Guid, Task<WallFeedSlot?>>(wallId, task)))
            {
                current.Retire(); // a revoked slot never stays: the next load subscribes again
                ViewerChanged?.Invoke(wallId);
            }

            return;
        }

        current.Retire();
        if (IsDisposed)
        {
            fresh.Retire(); // Dispose ran between the subscribe and the swap
            return;
        }

        ViewerChanged?.Invoke(wallId);
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
        if (slots.TryRemove(wallId, out var task))
        {
            RetireIfOpened(task);
        }

        if (!IsDisposed)
        {
            AccessLost?.Invoke(wallId);
        }
    }
}
