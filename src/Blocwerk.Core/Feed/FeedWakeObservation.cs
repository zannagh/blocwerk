// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>One registered wake observer; dispose it to stop observing.</summary>
internal sealed class FeedWakeObservation(Action remove) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            remove();
        }
    }
}
