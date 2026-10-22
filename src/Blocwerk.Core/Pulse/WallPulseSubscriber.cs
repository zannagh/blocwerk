// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Pulse;

/// <summary>One subscription to a wall's pulses; disposing it removes it and drops any delivery still under way.</summary>
internal sealed class WallPulseSubscriber(Guid wallId, Func<FeedViewer?> viewer, Action<WallPulse> onPulse, Action<WallPulseSubscriber> remove)
    : IDisposable
{
    private int disposed;

    public Guid WallId { get; } = wallId;

    public bool IsDisposed => Volatile.Read(ref disposed) == 1;

    /// <summary>Hands the pulse over when the viewer, read now, may receive it.</summary>
    public void Deliver(WallPulse pulse)
    {
        if (IsDisposed || viewer() is not { } current || !current.Audiences.Contains(pulse.Audience))
        {
            return;
        }

        onPulse(pulse);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            remove(this);
        }
    }
}
