// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// Counts open feed streams, in the process and per signed-in user, so a runaway client (or many tabs) cannot hold every
/// connection. Anonymous viewers (share link, kiosk) count only toward the process cap: a gym's phones share one address.
/// </summary>
public sealed class FeedStreamLimits(FeedStreamOptions options)
{
    private readonly Dictionary<Guid, int> perUser = [];
    private readonly Lock gate = new();
    private int total;

    /// <summary>Gets the open streams.</summary>
    public int Open
    {
        get
        {
            lock (gate)
            {
                return total;
            }
        }
    }

    /// <summary>A lease to dispose when the stream ends, or null when a cap is reached.</summary>
    public IDisposable? TryAcquire(Guid? userId)
    {
        lock (gate)
        {
            if (total >= options.MaxStreams
                || (userId is { } user && perUser.GetValueOrDefault(user) >= options.MaxStreamsPerUser))
            {
                return null;
            }

            total++;
            if (userId is { } id)
            {
                perUser[id] = perUser.GetValueOrDefault(id) + 1;
            }

            return new FeedStreamLease(() => Release(userId));
        }
    }

    private void Release(Guid? userId)
    {
        lock (gate)
        {
            total--;
            if (userId is { } id && --perUser[id] == 0)
            {
                perUser.Remove(id);
            }
        }
    }
}
