// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Client.Views;

/// <summary>
/// Times as the Server pages show them (TimeText, LocalTime): a "how long ago" label up to a week, else a date, in the
/// device's local time. The client has no time zone data, so the offset comes from the browser
/// (<see cref="ClientViewFacts.UtcOffset"/>).
/// </summary>
public static class ClientTime
{
    /// <summary>A short "how long ago" label, falling back to a local date beyond a week.</summary>
    /// <param name="timestamp">The moment.</param>
    /// <param name="now">Now.</param>
    /// <param name="offset">Local time minus UTC.</param>
    /// <returns>The label.</returns>
    public static string Relative(DateTimeOffset timestamp, DateTimeOffset now, TimeSpan offset)
    {
        var diff = now - timestamp;
        return diff.TotalMinutes < 1 ? "just now"
            : diff.TotalHours < 1 ? $"{(int)diff.TotalMinutes}m ago"
            : diff.TotalDays < 1 ? $"{(int)diff.TotalHours}h ago"
            : diff.TotalDays < 1.5 ? "yesterday"
            : diff.TotalDays < 7 ? $"{(int)diff.TotalDays}d ago"
            : timestamp.ToOffset(offset).ToString("MMM d", CultureInfo.InvariantCulture);
    }

    /// <summary>A local date, as the Server's <c>LocalTimeFormat.Date</c>.</summary>
    /// <param name="timestamp">The moment.</param>
    /// <param name="offset">Local time minus UTC.</param>
    /// <returns>The date.</returns>
    public static string Date(DateTimeOffset timestamp, TimeSpan offset) =>
        timestamp.ToOffset(offset).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
}
