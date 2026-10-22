// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// Counts open feed streams so no one caller can hold every connection.
/// <list type="bullet">
/// <item><description>The process: <see cref="FeedStreamOptions.MaxStreams"/>.</description></item>
/// <item><description>A signed-in user, a non-member on a share link included: <see cref="FeedStreamOptions.MaxStreamsPerUser"/>.</description></item>
/// <item><description>An anonymous stream (share link, kiosk) counts per address partition
/// (<see cref="FeedStreamOptions.MaxAnonymousPerAddress"/>; IPv6 per /64) and per wall
/// (<see cref="FeedStreamOptions.MaxAnonymousPerWall"/>), so a free share token cannot exhaust the process cap.
/// Signed-in streams skip the address cap: a gym's members share one NAT address.</description></item>
/// </list>
/// </summary>
public sealed class FeedStreamLimits(FeedStreamOptions options)
{
    private readonly Dictionary<string, int> counts = [];
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
    public IDisposable? TryAcquire(FeedStreamClaim claim)
    {
        var keys = KeysOf(claim);
        lock (gate)
        {
            if (total >= options.MaxStreams || keys.Any(k => counts.GetValueOrDefault(k.Key) >= k.Cap))
            {
                return null;
            }

            total++;
            foreach (var (key, _) in keys)
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            return new FeedStreamLease(() => Release(keys));
        }
    }

    private (string Key, int Cap)[] KeysOf(FeedStreamClaim claim) => claim.UserId is { } user
        ? [("user:" + user, options.MaxStreamsPerUser)]
        : [("addr:" + claim.Address, options.MaxAnonymousPerAddress), ("wall:" + claim.WallId, options.MaxAnonymousPerWall)];

    private void Release((string Key, int Cap)[] keys)
    {
        lock (gate)
        {
            total--;
            foreach (var (key, _) in keys)
            {
                if (--counts[key] == 0)
                {
                    counts.Remove(key);
                }
            }
        }
    }
}
