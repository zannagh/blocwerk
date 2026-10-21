// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>A hub whose subscribes, once closed, complete only when the test opens the gate (after the real subscribe).</summary>
internal sealed class GatedWallFeedHub(IWallFeedHub inner) : IWallFeedHub
{
    private volatile TaskCompletionSource? gate;
    private int waiting;

    public int Waiting => Volatile.Read(ref waiting);

    public void Close() => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => gate?.TrySetResult();

    public async Task<FeedSubscription> SubscribeAsync(
        Guid wallId, FeedViewer viewer, IFeedSubscriber subscriber, CancellationToken cancellationToken = default)
    {
        var subscription = await inner.SubscribeAsync(wallId, viewer, subscriber, cancellationToken);
        if (gate is { } closed)
        {
            Interlocked.Increment(ref waiting);
            await closed.Task;
        }

        return subscription;
    }

    public IDisposable ObserveWakes(Action<Guid> observer) => inner.ObserveWakes(observer);
}
