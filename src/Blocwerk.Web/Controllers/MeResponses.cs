namespace Blocwerk.Web.Controllers;

/// <summary>
/// Wire shapes of the user API. Every one of these exists because the matching domain type is an
/// EF entity carrying <c>User</c>/<c>Wall</c>/<c>Boulder</c> navigation properties: serializing
/// those directly would either cycle or hand the caller rows belonging to other users.
/// </summary>
public record SessionResponse(Guid Id, Guid WallId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public record AttemptResponse(
    Guid Id,
    Guid BoulderId,
    string Type,
    DateTimeOffset Timestamp,
    string? Notes,
    Guid? ClientRequestId,
    Guid? ActivityId);

public record HangboardSessionResponse(
    Guid Id,
    int EdgeSizeMm,
    double AdditionalWeightKg,
    double DurationSeconds,
    int Sets,
    DateTimeOffset Timestamp,
    string? Notes);

public record PullupSessionResponse(
    Guid Id,
    int Repetitions,
    double AdditionalWeightKg,
    int Sets,
    DateTimeOffset Timestamp,
    string? Notes);

/// <summary>Recent training work, flattened out of the caller's activities.</summary>
public record TrainingResponse(
    IReadOnlyList<HangboardSessionResponse> Hangboard,
    IReadOnlyList<PullupSessionResponse> Pullups);

public record BoulderAttemptSummaryResponse(string BoulderName, string? Grade, string BestResult, int AttemptCount);

public record ActivitySummaryResponse(
    Guid Id,
    DateOnly Date,
    DateTimeOffset StartedAt,
    int DurationMinutes,
    int BoulderCount,
    int HangboardCount,
    int PullupCount,
    string? WallName);

public record ActivityDetailResponse(
    Guid Id,
    DateTimeOffset StartedAt,
    int DurationMinutes,
    bool DurationIsManual,
    IReadOnlyList<BoulderAttemptSummaryResponse> Boulders,
    IReadOnlyList<HangboardSessionResponse> Hangboard,
    IReadOnlyList<PullupSessionResponse> Pullups,
    string? WallName);

public record ProgressionBucketResponse(
    DateOnly Start,
    DateOnly End,
    string Label,
    double? BoulderScore,
    string? BoulderGrade,
    double? TrainingScore,
    double VolumeMinutes);

public record ProgressionResponse(
    double BoulderScore,
    string? BoulderGrade,
    double TrainingScore,
    int WindowDays,
    string GroupBy,
    IReadOnlyList<ProgressionBucketResponse> Buckets);

public record DayActivityResponse(DateOnly Date, int Intensity);
