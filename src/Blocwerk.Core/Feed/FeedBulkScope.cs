// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// A <see cref="FeedScope"/> for a background hold writer that runs over the whole wall or over a set of holds (texture
/// placement, outline upgrade, footprint and protrusion refinement, volume placement). A whole-wall run writes in batches
/// that each stay under <see cref="FeedCollapse.MaxRowsPerWall"/>, so without a scope a big wall would write a row per hold;
/// with one it writes a resync per batch. A run over a few edited holds keeps its fine-grained rows.
/// </summary>
internal static class FeedBulkScope
{
    /// <summary>The most holds a run may name and still write a row per hold.</summary>
    public const int MaxFineGrainedHolds = 20;

    /// <summary>Opens the scope for a whole-wall run (<paramref name="holdCount"/> null) or a large one, else returns null.</summary>
    public static IDisposable? Begin(Guid wallId, string label, int? holdCount = null) =>
        holdCount is { } count && count <= MaxFineGrainedHolds ? null : FeedScope.Begin(wallId, label);
}
