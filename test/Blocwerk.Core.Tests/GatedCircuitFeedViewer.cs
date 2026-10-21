// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A circuit viewer whose first answer is "no access", read before <see cref="Release"/> is called: a role read that was
/// in flight while the user joined. Later reads answer <see cref="Viewer"/>.
/// </summary>
internal sealed class GatedCircuitFeedViewer : ICircuitFeedViewer
{
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;

    public FeedViewer? Viewer { get; set; }

    public int Calls => Volatile.Read(ref calls);

    /// <summary>Gets a task that completes once the first read has started.</summary>
    public Task Entered => entered.Task;

    public void Release() => gate.TrySetResult();

    public async Task<FeedViewer?> ResolveAsync(Guid wallId)
    {
        if (Interlocked.Increment(ref calls) > 1)
        {
            return Viewer;
        }

        entered.TrySetResult();
        await gate.Task;
        return null;
    }
}
