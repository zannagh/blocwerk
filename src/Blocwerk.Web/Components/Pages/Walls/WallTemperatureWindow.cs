// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The temperature chart's windows and buckets, shared by the web wall page (<c>WallTemperaturePanel</c>) and the
/// companion app's read: 24 hours, 7 days, 30 days, a year, or everything since the wall's oldest reading, averaged by
/// the database into <see cref="DayBuckets"/> (the day) or <see cref="WideBuckets"/> (wider windows) buckets.
/// </summary>
public static class WallTemperatureWindow
{
    /// <summary>Buckets of the 24-hour window.</summary>
    public const int DayBuckets = 120;

    /// <summary>Buckets of every wider window.</summary>
    public const int WideBuckets = 96;

    /// <summary>The narrowest bucket.</summary>
    public static readonly TimeSpan MinBucketWidth = TimeSpan.FromSeconds(1);

    /// <summary>The window's length, or null for "All".</summary>
    /// <param name="key">24h, 7d, 30d, 1y or all.</param>
    /// <param name="span">The length, null for all.</param>
    /// <returns>False for an unknown key.</returns>
    public static bool TryParse(string? key, out TimeSpan? span)
    {
        span = key switch
        {
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            "1y" => TimeSpan.FromDays(365),
            _ => null,
        };
        return span is not null || key == "all";
    }

    /// <summary>
    /// The window ending at <paramref name="to"/>: its start, bucket width and bucket count; null when "All" finds no
    /// readings at all.
    /// </summary>
    public static async Task<(DateTimeOffset From, TimeSpan Width, int Slots)?> ResolveAsync(
        IWallTemperatureService temperatures,
        Guid wallId,
        TimeSpan? span,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var from = span is { } length ? to - length : await AllTimeStartAsync(temperatures, wallId, to, ct);
        if (from is null)
        {
            return null;
        }

        var buckets = span == TimeSpan.FromHours(24) ? DayBuckets : WideBuckets;
        var total = to - from.Value;
        var width = TimeSpan.FromTicks(Math.Max(MinBucketWidth.Ticks, total.Ticks / buckets));
        var slots = Math.Max(1, (int)Math.Ceiling(total.Ticks / (double)width.Ticks));
        return (from.Value, width, slots);
    }

    private static async Task<DateTimeOffset?> AllTimeStartAsync(
        IWallTemperatureService temperatures,
        Guid wallId,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var earliest = await temperatures.GetEarliestReadingAtAsync(wallId, ct);
        if (earliest is null)
        {
            return null;
        }

        // A wall that started reporting minutes ago still needs a window wide enough to draw.
        return earliest.Value < to - MinBucketWidth ? earliest : to - TimeSpan.FromHours(24);
    }
}
