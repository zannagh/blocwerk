// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>How a bulk write site (<see cref="FeedBulkSites"/>) reaches the feed.</summary>
internal enum FeedBulkHandling
{
    /// <summary>Appends fine-grained rows (<c>video.status</c>, <c>hold.changed</c>, …) in the write's transaction.</summary>
    Rows,

    /// <summary>Appends a coarse resync (<c>wall.resync</c> per wall, <c>ticks.resync</c>) in the write's transaction.</summary>
    Resync,

    /// <summary>Reviewed: nothing a viewer sees changes, so no row is written.</summary>
    Excluded,
}
