// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Views;

/// <summary>What the boulder list shows: the Server wall view's filters that the cached data can answer.</summary>
public sealed record BoulderFilter
{
    /// <summary>Gets the name search.</summary>
    public string? Search { get; init; }

    /// <summary>Gets the order.</summary>
    public BoulderSort Sort { get; init; } = BoulderSort.Newest;

    /// <summary>Gets the easiest grade shown, or null.</summary>
    public string? GradeMin { get; init; }

    /// <summary>Gets the hardest grade shown, or null.</summary>
    public string? GradeMax { get; init; }

    /// <summary>Gets a value indicating whether only the boulders the viewer tried are shown.</summary>
    public bool Attempted { get; init; }

    /// <summary>Gets a value indicating whether only the boulders the viewer sent (a flash too) are shown.</summary>
    public bool Done { get; init; }

    /// <summary>Gets a value indicating whether only the boulders the viewer has not sent are shown.</summary>
    public bool NotDone { get; init; }

    /// <summary>Gets a value indicating whether only the viewer's favourites are shown.</summary>
    public bool Favourites { get; init; }

    /// <summary>Gets how many filters narrow the list (the chip's count), as the Server counts them.</summary>
    public int ActiveCount =>
        (string.IsNullOrWhiteSpace(Search) ? 0 : 1)
        + (string.IsNullOrEmpty(GradeMin) && string.IsNullOrEmpty(GradeMax) ? 0 : 1)
        + (Attempted ? 1 : 0) + (Done ? 1 : 0) + (NotDone ? 1 : 0) + (Favourites ? 1 : 0);
}
