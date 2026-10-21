// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// The walls a live list follows although the circuit may cache nothing of them (a gallery, a 3D page): their feed
/// subscription is kept when idle walls are released. Counted, because several lists can follow one wall.
/// </summary>
public sealed class WallFeedWatches
{
    private readonly ConcurrentDictionary<Guid, int> counts = new();

    public bool Has(Guid wallId) => counts.TryGetValue(wallId, out var n) && n > 0;

    /// <summary>Follows the wall until the returned handle is disposed (once).</summary>
    public IDisposable Watch(Guid wallId)
    {
        counts.AddOrUpdate(wallId, 1, (_, n) => n + 1);
        return new WallFeedWatch(() => counts.AddOrUpdate(wallId, 0, (_, n) => Math.Max(0, n - 1)));
    }
}
