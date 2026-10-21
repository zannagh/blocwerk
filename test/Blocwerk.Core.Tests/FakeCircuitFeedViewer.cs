// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>A circuit viewer the test sets, counting how often the circuit asked.</summary>
internal sealed class FakeCircuitFeedViewer : ICircuitFeedViewer
{
    private int resolved;

    public FeedViewer? Viewer { get; set; }

    public int Resolved => Volatile.Read(ref resolved);

    public Task<FeedViewer?> ResolveAsync(Guid wallId)
    {
        Interlocked.Increment(ref resolved);
        return Task.FromResult(Viewer);
    }
}
