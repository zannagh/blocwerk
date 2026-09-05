namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A lightweight summary of a single TopLogger climb-day ("session") from the paginated feed, without
/// drilling into its per-day logs. Used by the re-sync pre-check to decide whether a full pull is
/// needed and, when reconciling, to fetch just that one day's ticks.
/// </summary>
/// <param name="Date">The climb-day's <c>statsAtDate</c> (day-anchored).</param>
/// <param name="DateKey">
/// The <c>yyyy-MM-dd</c> key for the day, as the per-day logs query expects it (derived from the raw
/// <c>statsAtDate</c> text so it is not skewed by the instant's local offset).
/// </param>
public sealed record TopLoggerSessionSummary(DateTimeOffset Date, string DateKey);
