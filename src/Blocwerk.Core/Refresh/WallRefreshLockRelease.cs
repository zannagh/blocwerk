// <copyright file="WallRefreshLockRelease.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Refresh;

/// <summary>Releases a <see cref="WallRefreshLocks"/> lock once, however often it is disposed.</summary>
internal sealed class WallRefreshLockRelease(SemaphoreSlim gate) : IDisposable
{
    private int released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            gate.Release();
        }
    }
}
