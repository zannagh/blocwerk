// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>One open stream's place in <see cref="FeedStreamLimits"/>; released once.</summary>
internal sealed class FeedStreamLease(Action release) : IDisposable
{
    private int released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            release();
        }
    }
}
