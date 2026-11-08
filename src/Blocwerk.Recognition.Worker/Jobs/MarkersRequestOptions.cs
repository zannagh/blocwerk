// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>The optional <c>options</c> object of a <see cref="MarkersRequest"/>.</summary>
public sealed record MarkersRequestOptions(
    IReadOnlyList<int>? AllowedIds,
    double? MinSidePx,
    double? MaxEdgeRatio,
    bool? RefineCorners,
    double? MinQuietZoneContrast,
    bool? BrightenedPass);
