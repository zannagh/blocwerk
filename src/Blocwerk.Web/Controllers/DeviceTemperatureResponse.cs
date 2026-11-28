// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>The web wall page's temperature card: the latest reading and one window's bucket averages.</summary>
/// <param name="LatestCelsius">The most recent reading, or null when the wall has none.</param>
/// <param name="LatestAt">When it was taken.</param>
/// <param name="Range">The window: 24h, 7d, 30d, 1y or all.</param>
/// <param name="From">The window's start, or null when "all" finds no readings.</param>
/// <param name="To">The window's end (now).</param>
/// <param name="BucketSeconds">Each bucket's width.</param>
/// <param name="Buckets">The buckets that have readings, oldest first; a missing bucket is a gap in the chart.</param>
public sealed record DeviceTemperatureResponse(
    double? LatestCelsius,
    DateTimeOffset? LatestAt,
    string Range,
    DateTimeOffset? From,
    DateTimeOffset To,
    double BucketSeconds,
    IReadOnlyList<WallTemperatureBucket> Buckets);
