// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/markers-request</c>: <see cref="MarkerDetectionOptions"/>, absent fields at their defaults.</summary>
public sealed record MarkersRequest(MarkersRequestOptions? Options)
{
    public MarkerDetectionOptions ToOptions()
    {
        var d = MarkerDetectionOptions.Default;
        return Options is not { } o
            ? d
            : new MarkerDetectionOptions
            {
                AllowedIds = o.AllowedIds is { } ids ? ids.ToHashSet() : d.AllowedIds,
                MinSidePx = o.MinSidePx ?? d.MinSidePx,
                MaxEdgeRatio = o.MaxEdgeRatio ?? d.MaxEdgeRatio,
                RefineCorners = o.RefineCorners ?? d.RefineCorners,
                MinQuietZoneContrast = o.MinQuietZoneContrast ?? d.MinQuietZoneContrast,
                BrightenedPass = o.BrightenedPass ?? d.BrightenedPass,
            };
    }
}
