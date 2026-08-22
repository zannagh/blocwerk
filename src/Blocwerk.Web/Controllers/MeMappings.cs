using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>Projects the domain types onto the user API's wire shapes.</summary>
internal static class MeMappings
{
    public static SessionResponse ToResponse(this ClimbingSession session)
    {
        return new SessionResponse(session.Id, session.WallId, session.StartedAt, session.EndedAt);
    }

    public static AttemptResponse ToResponse(this Attempt attempt)
    {
        return new AttemptResponse(
            attempt.Id,
            attempt.BoulderId,
            attempt.Type.ToString(),
            attempt.Timestamp,
            attempt.Notes,
            attempt.ClientRequestId,
            attempt.ActivityId);
    }

    public static HangboardSessionResponse ToResponse(this HangboardSession session)
    {
        return new HangboardSessionResponse(
            session.Id,
            session.EdgeSizeMm,
            session.AdditionalWeightKg,
            session.Duration.TotalSeconds,
            session.Sets,
            session.Timestamp,
            session.Notes);
    }

    public static PullupSessionResponse ToResponse(this PullupSession session)
    {
        return new PullupSessionResponse(
            session.Id,
            session.Repetitions,
            session.AdditionalWeightKg,
            session.Sets,
            session.Timestamp,
            session.Notes);
    }

    public static BoulderAttemptSummaryResponse ToResponse(this BoulderAttemptSummary summary)
    {
        return new BoulderAttemptSummaryResponse(
            summary.BoulderName,
            summary.Grade,
            summary.BestResult.ToString(),
            summary.AttemptCount);
    }

    public static ActivitySummaryResponse ToResponse(this ActivitySummary summary)
    {
        return new ActivitySummaryResponse(
            summary.Id,
            summary.Date,
            summary.StartedAt,
            summary.DurationMinutes,
            summary.BoulderCount,
            summary.HangboardCount,
            summary.PullupCount,
            summary.WallName);
    }

    public static ActivityDetailResponse ToResponse(this ActivityDetail detail)
    {
        return new ActivityDetailResponse(
            detail.Id,
            detail.StartedAt,
            detail.DurationMinutes,
            detail.DurationIsManual,
            detail.Boulders.Select(b => b.ToResponse()).ToList(),
            detail.Hangboard.Select(h => h.ToResponse()).ToList(),
            detail.Pullups.Select(p => p.ToResponse()).ToList(),
            detail.WallName);
    }

    public static ProgressionResponse ToResponse(this UserProgression progression)
    {
        return new ProgressionResponse(
            progression.BoulderScore,
            progression.BoulderGrade,
            progression.TrainingScore,
            progression.WindowDays,
            progression.GroupBy.ToString(),
            progression.Buckets.Select(b => b.ToResponse()).ToList());
    }

    public static ProgressionBucketResponse ToResponse(this ProgressionBucket bucket)
    {
        return new ProgressionBucketResponse(
            bucket.Start,
            bucket.End,
            bucket.Label,
            bucket.BoulderScore,
            bucket.BoulderGrade,
            bucket.TrainingScore,
            bucket.VolumeMinutes);
    }

    public static DayActivityResponse ToResponse(this DayActivity activity)
    {
        return new DayActivityResponse(activity.Date, activity.Intensity);
    }
}
