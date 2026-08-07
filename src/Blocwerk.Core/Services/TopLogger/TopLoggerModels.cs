using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>Result of a TopLogger sign-in attempt.</summary>
public record TopLoggerAuthResult(
    bool Success,
    string? Token,
    string? UserUid,
    TopLoggerBackend Backend,
    string? Error);

/// <summary>The stored credentials used to call the API on a user's behalf.</summary>
public record TopLoggerCredentials(
    string Email,
    string Token,
    string? UserUid,
    TopLoggerBackend Backend);

/// <summary>
/// One ascent as returned by TopLogger, before grade mapping. <see cref="GradeRaw"/> /
/// <see cref="GradeSystem"/> are handed to <see cref="Helpers.TopLoggerGradeMapper"/> to produce a
/// Font/V label. Only sends/flashes are surfaced (projects are dropped).
/// </summary>
public record TopLoggerAscentDto(
    string ExternalId,
    string? ClimbName,
    string? GymName,
    string? GradeRaw,
    string? GradeSystem,
    AttemptType Type,
    DateTimeOffset LoggedAt);
