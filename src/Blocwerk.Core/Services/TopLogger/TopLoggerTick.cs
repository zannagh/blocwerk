namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// A single logged tick / ascent from a user's TopLogger logbook, flattened for
/// import into Blocwerk. Fields the API does not populate are left null.
/// </summary>
/// <param name="ExternalId">The TopLogger climb-log id (stable per tick).</param>
/// <param name="ClimbId">The TopLogger climb id (stable per climb across ticks / sessions), when known.</param>
/// <param name="ClimbName">The climb / boulder name, when known.</param>
/// <param name="ClimbType">The climb type (e.g. <c>boulder</c>, <c>route</c>).</param>
/// <param name="GymId">The TopLogger gym id the climb belongs to.</param>
/// <param name="GymName">The gym's display name.</param>
/// <param name="GymSlug">The gym's URL slug.</param>
/// <param name="LoggedAt">When the tick was logged (from <c>climbedAtDate</c>).</param>
/// <param name="TickType">The tick type (e.g. <c>flash</c>, <c>redpoint</c>).</param>
/// <param name="TryIndex">The attempt index recorded on the tick.</param>
/// <param name="Ticked">Whether the climb counts as ticked.</param>
/// <param name="Topped">Whether the climb was topped, when reported.</param>
/// <param name="Points">The points awarded, when reported.</param>
/// <param name="RawGrade">The raw / scaled grade exactly as returned by the API.</param>
/// <param name="MappedFontGrade">
/// The best-effort Font grade derived from <paramref name="RawGrade"/>, or
/// <c>null</c> when it could not be mapped (caller may flag NeedsGradeMapping).
/// </param>
public sealed record TopLoggerTick(
    string ExternalId,
    string? ClimbId,
    string? ClimbName,
    string? ClimbType,
    string? GymId,
    string? GymName,
    string? GymSlug,
    DateTimeOffset? LoggedAt,
    string? TickType,
    int TryIndex,
    bool Ticked,
    bool? Topped,
    double? Points,
    string? RawGrade,
    string? MappedFontGrade);
