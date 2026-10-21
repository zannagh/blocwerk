// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>One list's hold on a wall's subscription (<see cref="WallFeedWatches"/>); disposing releases it once.</summary>
public sealed class WallFeedWatch(Action release) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            release();
        }
    }
}
