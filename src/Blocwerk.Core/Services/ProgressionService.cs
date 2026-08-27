using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IProgressionService
{
    Task<UserProgression> GetProgressionAsync();

    /// <summary>
    /// Progression for another member, using an explicit window and grouping. The current viewer must
    /// share at least one wall with <paramref name="userId"/>; otherwise an empty progression is
    /// returned (defence-in-depth — the page also gates this). Passing the viewer's own id is allowed.
    /// </summary>
    Task<UserProgression> GetProgressionForUserAsync(Guid userId, int windowDays, ProgressionGroupBy groupBy);

    Task<List<DayActivity>> GetActivityGridAsync(int weeks = 20);

    Task<DaySummary> GetDaySummaryAsync(DateOnly date);

    Task UpdateProgressionWindowAsync(int days);

    Task UpdateProgressionGroupingAsync(ProgressionGroupBy groupBy);

    /// <summary>Activities (gap-clustered sessions) within the user's progression window, newest first.</summary>
    Task<List<ActivitySummary>> GetActivitiesAsync();

    Task<ActivityDetail?> GetActivityAsync(Guid activityId);

    Task UpdateActivityDurationAsync(Guid activityId, int minutes);

    /// <summary>
    /// Leaderboard for every member of <paramref name="wallId"/>: their hardest send (all-time, this
    /// wall), their wall-scoped rolling boulder score, and their all-time training volume on the wall.
    /// Members with no data appear with a null grade and zero score/volume. Ordered by score descending.
    /// </summary>
    Task<IReadOnlyList<WallLeaderboardEntry>> GetWallLeaderboardAsync(Guid wallId);
}

public record UserProgression(
    double BoulderScore,
    string? BoulderGrade,
    double TrainingScore,
    int WindowDays,
    ProgressionGroupBy GroupBy,
    List<ProgressionBucket> Buckets);

/// <summary>
/// One group-by bucket (a day, ISO week, or calendar month). Boulder/Training scores are null when
/// the bucket has no qualifying activity (rendered as a gap); Volume is always present (0 = no activity).
/// </summary>
public record ProgressionBucket(
    DateOnly Start,
    DateOnly End,
    string Label,
    double? BoulderScore,
    string? BoulderGrade,
    double? TrainingScore,
    double VolumeMinutes);

public record DayActivity(DateOnly Date, int Intensity);

public record DaySummary(
    DateOnly Date,
    List<BoulderAttemptSummary> Boulders,
    List<HangboardSession> Hangboard,
    List<PullupSession> Pullups,
    TimeSpan? SessionDuration);

public record BoulderAttemptSummary(string BoulderName, string? Grade, AttemptType BestResult, int AttemptCount);

public record ActivitySummary(
    Guid Id,
    DateOnly Date,
    DateTimeOffset StartedAt,
    int DurationMinutes,
    int BoulderCount,
    int HangboardCount,
    int PullupCount,
    string? WallName);

public record ActivityDetail(
    Guid Id,
    DateTimeOffset StartedAt,
    int DurationMinutes,
    bool DurationIsManual,
    List<BoulderAttemptSummary> Boulders,
    List<HangboardSession> Hangboard,
    List<PullupSession> Pullups,
    string? WallName);

public class ProgressionService : IProgressionService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IWallService _wallService;
    private readonly ILogger<ProgressionService> _logger;

    public ProgressionService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService, IWallService wallService, ILogger<ProgressionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _wallService = wallService;
        _logger = logger;
    }

    /// <summary>Days a send counts toward the boulder rating before it decays out (TopLogger-style).</summary>
    private const int RatingWindowDays = 60;

    public async Task<UserProgression> GetProgressionAsync()
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        return await ComputeProgressionAsync(user.Id, user.ProgressionWindowDays, user.ProgressionGroupBy);
    }

    public async Task<UserProgression> GetProgressionForUserAsync(Guid userId, int windowDays, ProgressionGroupBy groupBy)
    {
        var viewer = await _currentUserService.GetCurrentUserAsync();

        // Defence-in-depth: only surface another member's progression when the viewer shares a wall
        // with them. Viewing one's own progression is always allowed.
        if (viewer.Id != userId && !await _wallService.UsersShareAWallAsync(viewer.Id, userId))
        {
            _logger.LogWarning("User {ViewerId} denied progression for {TargetId} (no shared wall)", viewer.Id, userId);
            return new UserProgression(0, null, 0, windowDays, groupBy, []);
        }

        return await ComputeProgressionAsync(userId, Math.Clamp(windowDays, 7, 365), groupBy);
    }

    private async Task<UserProgression> ComputeProgressionAsync(Guid userId, int windowDays, ProgressionGroupBy groupBy)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.Get");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-windowDays);

            // Boulder rating is a rolling window: a send counts for RatingWindowDays, then decays out
            // (TopLogger-style). So the curve needs attempts from one rating-window BEFORE the view
            // starts, for the earliest bucket's trailing window.
            var attempts = await db.Attempts
                .Include(a => a.Boulder)
                .Where(a => a.UserId == userId && a.Timestamp >= cutoff.AddDays(-RatingWindowDays))
                .ToListAsync();

            // Current rating = the rolling score as of now, independent of the view window.
            var ratingAttempts = attempts.Where(a => a.Timestamp >= now.AddDays(-RatingWindowDays)).ToList();
            var boulderScore = CalculateBoulderScore(ratingAttempts);
            var boulderGrade = GradeScoring.ScoreToGrade(boulderScore);

            var hangboard = await db.HangboardSessions
                .Where(h => h.UserId == userId && h.Timestamp >= cutoff)
                .ToListAsync();
            var pullups = await db.PullupSessions
                .Where(p => p.UserId == userId && p.Timestamp >= cutoff)
                .ToListAsync();
            var trainingScore = CalculateTrainingScore(hangboard, pullups);

            var activities = await db.Activities
                .Where(a => a.UserId == userId && a.StartedAt >= cutoff)
                .ToListAsync();

            // Boulder is a rolling rating per bucket (smooth, continuous); training and volume are
            // per-period sums. Reuses the rows already loaded above.
            var buckets = BuildBuckets(groupBy, cutoff, attempts, hangboard, pullups, activities);

            return new UserProgression(boulderScore, boulderGrade, trainingScore, windowDays, groupBy, buckets);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<DayActivity>> GetActivityGridAsync(int weeks = 20)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.GetActivityGrid");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var days = weeks * 7;
            var since = DateTimeOffset.UtcNow.AddDays(-days);

            var attempts = await db.Attempts
                .Where(a => a.UserId == user.Id && a.Timestamp >= since)
                .Select(a => a.Timestamp)
                .ToListAsync();

            var hangboard = await db.HangboardSessions
                .Where(h => h.UserId == user.Id && h.Timestamp >= since)
                .Select(h => h.Timestamp)
                .ToListAsync();

            var pullups = await db.PullupSessions
                .Where(p => p.UserId == user.Id && p.Timestamp >= since)
                .Select(p => p.Timestamp)
                .ToListAsync();

            var allTimestamps = attempts.Concat(hangboard).Concat(pullups).ToList();

            var result = new List<DayActivity>();
            var startDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddDays(-days).Date);

            for (int i = 0; i <= days; i++)
            {
                var date = startDate.AddDays(i);
                var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                var dayEnd = dayStart.AddDays(1);

                var dayStamps = allTimestamps.Where(t => t >= dayStart && t < dayEnd).ToList();
                int intensity = 0;

                if (dayStamps.Count > 0)
                {
                    var span = dayStamps.Max() - dayStamps.Min();
                    var count = dayStamps.Count;

                    if (span.TotalMinutes >= 90 || count >= 15)
                    {
                        intensity = 4;
                    }
                    else if (span.TotalMinutes >= 60 || count >= 10)
                    {
                        intensity = 3;
                    }
                    else if (span.TotalMinutes >= 30 || count >= 5)
                    {
                        intensity = 2;
                    }
                    else
                    {
                        intensity = 1;
                    }
                }

                result.Add(new DayActivity(date, intensity));
            }

            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<DaySummary> GetDaySummaryAsync(DateOnly date)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.GetDaySummary");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1);

            var attempts = await db.Attempts
                .Include(a => a.Boulder)
                .Where(a => a.UserId == user.Id && a.Timestamp >= dayStart && a.Timestamp < dayEnd)
                .ToListAsync();

            var boulders = attempts
                .GroupBy(a => a.BoulderId)
                .Select(g =>
                {
                    var first = g.First();
                    var best = g.Max(a => a.Type);
                    return new BoulderAttemptSummary(first.Boulder.Name, first.Boulder.Grade, best, g.Count());
                })
                .ToList();

            var hangboard = await db.HangboardSessions
                .Where(h => h.UserId == user.Id && h.Timestamp >= dayStart && h.Timestamp < dayEnd)
                .ToListAsync();

            var pullups = await db.PullupSessions
                .Where(p => p.UserId == user.Id && p.Timestamp >= dayStart && p.Timestamp < dayEnd)
                .ToListAsync();

            TimeSpan? sessionDuration = null;
            var allTimestamps = attempts.Select(a => a.Timestamp)
                .Concat(hangboard.Select(h => h.Timestamp))
                .Concat(pullups.Select(p => p.Timestamp))
                .ToList();

            if (allTimestamps.Count >= 2)
            {
                sessionDuration = allTimestamps.Max() - allTimestamps.Min();
            }

            return new DaySummary(date, boulders, hangboard, pullups, sessionDuration);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task UpdateProgressionWindowAsync(int days)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.UpdateWindow");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var dbUser = await db.Users.FirstAsync(u => u.Id == user.Id);
            dbUser.ProgressionWindowDays = Math.Clamp(days, 7, 365);
            await db.SaveChangesAsync();

            // The current-user service caches the User for the scope; drop it so the re-render that
            // recomputes progression reads the new window rather than the stale cached one.
            _currentUserService.InvalidateCache();
            _logger.LogInformation("Progression window updated to {WindowDays} days for {UserId}", dbUser.ProgressionWindowDays, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task UpdateProgressionGroupingAsync(ProgressionGroupBy groupBy)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.UpdateGrouping");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var dbUser = await db.Users.FirstAsync(u => u.Id == user.Id);
            dbUser.ProgressionGroupBy = groupBy;
            await db.SaveChangesAsync();

            _currentUserService.InvalidateCache();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<ActivitySummary>> GetActivitiesAsync()
    {
        using var op = BlocwerkMetrics.TimeOperation("Activity.List");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-user.ProgressionWindowDays);

            var activities = await db.Activities
                .Include(a => a.Wall)
                .Where(a => a.UserId == user.Id && a.StartedAt >= cutoff)
                .OrderByDescending(a => a.StartedAt)
                .ToListAsync();

            if (activities.Count == 0)
            {
                return [];
            }

            var ids = activities.Select(a => a.Id).ToList();

            var boulderCounts = await db.Attempts
                .Where(a => a.ActivityId != null && ids.Contains(a.ActivityId.Value))
                .GroupBy(a => a.ActivityId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Select(x => x.BoulderId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);

            var hangboardCounts = await db.HangboardSessions
                .Where(h => h.ActivityId != null && ids.Contains(h.ActivityId.Value))
                .GroupBy(h => h.ActivityId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);

            var pullupCounts = await db.PullupSessions
                .Where(p => p.ActivityId != null && ids.Contains(p.ActivityId.Value))
                .GroupBy(p => p.ActivityId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count);

            return activities.Select(a => new ActivitySummary(
                a.Id,
                DateOnly.FromDateTime(a.StartedAt.UtcDateTime.Date),
                a.StartedAt,
                ActivityMinutes(a),
                boulderCounts.GetValueOrDefault(a.Id),
                hangboardCounts.GetValueOrDefault(a.Id),
                pullupCounts.GetValueOrDefault(a.Id),
                a.Wall?.Name)).ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<ActivityDetail?> GetActivityAsync(Guid activityId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Activity.Get");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var activity = await db.Activities
                .Include(a => a.Wall)
                .FirstOrDefaultAsync(a => a.Id == activityId && a.UserId == user.Id);

            if (activity == null)
            {
                return null;
            }

            var attempts = await db.Attempts
                .Include(a => a.Boulder)
                .Where(a => a.ActivityId == activityId)
                .ToListAsync();

            var boulders = attempts
                .GroupBy(a => a.BoulderId)
                .Select(g =>
                {
                    var first = g.First();
                    return new BoulderAttemptSummary(first.Boulder.Name, first.Boulder.Grade, g.Max(a => a.Type), g.Count());
                })
                .ToList();

            var hangboard = await db.HangboardSessions.Where(h => h.ActivityId == activityId).ToListAsync();
            var pullups = await db.PullupSessions.Where(p => p.ActivityId == activityId).ToListAsync();

            return new ActivityDetail(
                activity.Id,
                activity.StartedAt,
                ActivityMinutes(activity),
                activity.DurationMinutes.HasValue,
                boulders,
                hangboard,
                pullups,
                activity.Wall?.Name);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task UpdateActivityDurationAsync(Guid activityId, int minutes)
    {
        using var op = BlocwerkMetrics.TimeOperation("Activity.UpdateDuration");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId && a.UserId == user.Id);
            if (activity == null)
            {
                throw new InvalidOperationException("Activity not found");
            }

            activity.DurationMinutes = Math.Clamp(minutes, 0, 24 * 60);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<WallLeaderboardEntry>> GetWallLeaderboardAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Progression.GetWallLeaderboard", wallId);
        try
        {
            var members = await _wallService.GetMembersAsync(wallId);
            if (members.Count == 0)
            {
                return [];
            }

            // Defence-in-depth: the leaderboard is UI-gated to members, but the service must not
            // hand a wall's board to a non-member. Reuse the members already loaded — no extra query.
            var currentUser = await _currentUserService.GetCurrentUserAsync();
            if (currentUser is null || !members.Any(m => m.UserId == currentUser.Id))
            {
                return [];
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var scoreCutoff = DateTimeOffset.UtcNow.AddDays(-RatingWindowDays);

            // ONE query for the whole wall: every attempt on a boulder that belongs to this wall,
            // materialised with its boulder so the scoring helpers can read the grade. Grouped per
            // user in memory below — no per-member round-trips.
            var wallAttempts = await db.Attempts
                .Include(a => a.Boulder)
                .Where(a => a.Boulder.WallId == wallId)
                .ToListAsync();

            // ONE query for the wall's activities; volume is summed per user in memory.
            var wallActivities = await db.Activities
                .Where(a => a.WallId == wallId)
                .ToListAsync();

            var attemptsByUser = wallAttempts
                .GroupBy(a => a.UserId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var volumeByUser = wallActivities
                .GroupBy(a => a.UserId)
                .ToDictionary(g => g.Key, g => g.Sum(ActivityMinutes));

            var entries = new List<WallLeaderboardEntry>(members.Count);
            foreach (var member in members)
            {
                var attempts = attemptsByUser.GetValueOrDefault(member.UserId) ?? [];

                var (hardestGrade, hardestScore) = HardestSend(attempts);
                var score = (int)Math.Round(CalculateBoulderScore(
                    attempts.Where(a => a.Timestamp >= scoreCutoff).ToList()));
                var volume = volumeByUser.GetValueOrDefault(member.UserId);

                entries.Add(new WallLeaderboardEntry(
                    member.UserId,
                    member.User?.DisplayName ?? "Unknown",
                    hardestGrade,
                    hardestScore,
                    score,
                    volume));
            }

            return entries
                .OrderByDescending(e => e.Score)
                .ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// The member's hardest send over the given (already wall-scoped) attempts: the best type per
    /// boulder decides whether it counts, and the flash bonus is folded into the numeric score used
    /// for sorting. Returns (null, 0) when there is no send.
    /// </summary>
    private static (string? Grade, int Score) HardestSend(List<Attempt> attempts)
    {
        string? grade = null;
        var score = 0;
        foreach (var boulderGroup in attempts.GroupBy(a => a.BoulderId))
        {
            var bestType = boulderGroup.Max(a => a.Type);
            if (bestType < AttemptType.Send)
            {
                continue;
            }

            var boulder = boulderGroup.First().Boulder;
            var candidate = GradeScoring.GetScore(boulder.Grade, bestType == AttemptType.Flash);
            if (candidate > score)
            {
                score = candidate;
                grade = boulder.Grade;
            }
        }

        return (grade, score);
    }

    private static double CalculateBoulderScore(List<Attempt> attempts)
    {
        var bestPerBoulder = attempts
            .GroupBy(a => a.BoulderId)
            .Select(g =>
            {
                var boulder = g.First().Boulder;
                var bestType = g.Max(a => a.Type);
                if (bestType < AttemptType.Send)
                {
                    return 0;
                }

                return GradeScoring.GetScore(boulder.Grade, bestType == AttemptType.Flash);
            })
            .Where(s => s > 0)
            .OrderByDescending(s => s)
            .Take(10)
            .ToList();

        if (bestPerBoulder.Count == 0)
        {
            return 0;
        }

        return bestPerBoulder.Average();
    }

    private static double CalculateTrainingScore(List<HangboardSession> hangboard, List<PullupSession> pullups)
    {
        double score = 0;
        foreach (var h in hangboard)
        {
            score += h.Sets * h.Duration.TotalSeconds * (1 + (h.AdditionalWeightKg / 20.0)) / (h.EdgeSizeMm / 10.0);
        }

        foreach (var p in pullups)
        {
            score += p.Sets * p.Repetitions * (1 + (p.AdditionalWeightKg / 10.0));
        }

        return score;
    }

    /// <summary>Effective duration in minutes: the user's override, else the event span.</summary>
    private static int ActivityMinutes(Activity a) =>
        a.DurationMinutes ?? Math.Max(0, (int)Math.Round((a.LastEventAt - a.StartedAt).TotalMinutes));

    private static List<ProgressionBucket> BuildBuckets(
        ProgressionGroupBy groupBy,
        DateTimeOffset cutoff,
        List<Attempt> attempts,
        List<HangboardSession> hangboard,
        List<PullupSession> pullups,
        List<Activity> activities)
    {
        var start = DateOnly.FromDateTime(cutoff.UtcDateTime.Date);
        var end = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.Date);

        var buckets = new List<ProgressionBucket>();
        foreach (var (bucketStart, bucketEnd, label) in EnumerateBuckets(groupBy, start, end))
        {
            var from = new DateTimeOffset(bucketStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var to = new DateTimeOffset(bucketEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            // Boulder: rolling rating as of the end of the bucket — every send in the trailing
            // rating window counts, so the curve is continuous and smooth rather than spiky.
            var ratingAttempts = attempts.Where(a => a.Timestamp >= to.AddDays(-RatingWindowDays) && a.Timestamp < to).ToList();
            // Training/volume: only this period's own items.
            var bucketHang = hangboard.Where(h => h.Timestamp >= from && h.Timestamp < to).ToList();
            var bucketPull = pullups.Where(p => p.Timestamp >= from && p.Timestamp < to).ToList();
            var bucketVolume = activities.Where(a => a.StartedAt >= from && a.StartedAt < to).Sum(ActivityMinutes);

            var bScore = CalculateBoulderScore(ratingAttempts);
            var tScore = CalculateTrainingScore(bucketHang, bucketPull);

            buckets.Add(new ProgressionBucket(
                bucketStart,
                bucketEnd,
                label,
                bScore > 0 ? bScore : null,
                GradeScoring.ScoreToGrade(bScore),
                bucketHang.Count + bucketPull.Count > 0 ? tScore : null,
                bucketVolume));
        }

        return buckets;
    }

    private static IEnumerable<(DateOnly Start, DateOnly End, string Label)> EnumerateBuckets(
        ProgressionGroupBy groupBy, DateOnly start, DateOnly end)
    {
        switch (groupBy)
        {
            case ProgressionGroupBy.Day:
                for (var d = start; d <= end; d = d.AddDays(1))
                {
                    yield return (d, d, d.ToString("dd.MM"));
                }

                break;

            case ProgressionGroupBy.Month:
                var month = new DateOnly(start.Year, start.Month, 1);
                var lastMonth = new DateOnly(end.Year, end.Month, 1);
                while (month <= lastMonth)
                {
                    yield return (month, month.AddMonths(1).AddDays(-1), month.ToString("MMM yy"));
                    month = month.AddMonths(1);
                }

                break;

            default: // Week: ISO weeks anchored on Monday.
                var weekStart = start.AddDays(-((7 + (int)start.DayOfWeek - (int)DayOfWeek.Monday) % 7));
                for (var w = weekStart; w <= end; w = w.AddDays(7))
                {
                    yield return (w, w.AddDays(6), w.ToString("dd.MM"));
                }

                break;
        }
    }
}
