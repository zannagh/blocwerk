// <copyright file="WallRefreshLocks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// One lock per wall, shared by the background steps (<see cref="WallRefreshProcessor"/>) and the user's Apply
/// (<see cref="WallRefreshService.ApplyAsync"/>): a wall has one open run, so this serialises everything that moves that
/// run. Apply therefore never lands in the middle of a 3D re-check that is rewriting the decisions and the summary, and
/// a re-check never starts once an apply was accepted (the worker then sees Applying and applies).
/// </summary>
public sealed class WallRefreshLocks(TimeSpan? applyWait = null)
{
    /// <summary>How long Apply waits for a background step of the same wall before it asks the user to try again.</summary>
    public static readonly TimeSpan DefaultApplyWait = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    /// <summary>Waits for the wall's lock; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(Guid wallId, CancellationToken ct)
    {
        var gate = Gate(wallId);
        await gate.WaitAsync(ct);
        return new WallRefreshLockRelease(gate);
    }

    /// <summary>The wall's lock for the user's Apply: refused with the "being checked" message when a step holds it too long.</summary>
    public async Task<IDisposable> AcquireForApplyAsync(Guid wallId)
    {
        var gate = Gate(wallId);
        if (!await gate.WaitAsync(applyWait ?? DefaultApplyWait))
        {
            throw new UserFacingException(WallRefreshService.BeingChecked);
        }

        return new WallRefreshLockRelease(gate);
    }

    private SemaphoreSlim Gate(Guid wallId) => locks.GetOrAdd(wallId, _ => new SemaphoreSlim(1, 1));
}
