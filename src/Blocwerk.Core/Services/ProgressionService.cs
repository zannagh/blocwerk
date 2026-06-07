using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IProgressionService
{
    Task<UserProgression> GetProgressionAsync();

    Task<List<DayActivity>> GetActivityGridAsync(int weeks = 20);

    Task<DaySummary> GetDaySummaryAsync(DateOnly date);

    Task UpdateProgressionWindowAsync(int days);
}

public record UserProgression(
    double BoulderScore,
    string? BoulderGrade,
    double TrainingScore,
    List<ProgressionPoint> BoulderCurve,
    List<ProgressionPoint> TrainingCurve,
    int WindowDays);

public record ProgressionPoint(DateOnly Date, double Score);

public record DayActivity(DateOnly Date, int Intensity);

public record DaySummary(
    DateOnly Date,
    List<BoulderAttemptSummary> Boulders,
    List<HangboardSession> Hangboard,
    List<PullupSession> Pullups,
    TimeSpan? SessionDuration);

public record BoulderAttemptSummary(string BoulderName, string? Grade, AttemptType BestResult, int AttemptCount);

public class ProgressionService : IProgressionService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;

    public ProgressionService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
    }

    public async Task<UserProgression> GetProgressionAsync()
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var windowDays = user.ProgressionWindowDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);

        var attempts = await db.Attempts
            .Include(a => a.Boulder)
            .Where(a => a.UserId == user.Id && a.Timestamp >= cutoff)
            .ToListAsync();

        var boulderScore = CalculateBoulderScore(attempts);
        var boulderGrade = GradeScoring.ScoreToGrade(boulderScore);

        var hangboard = await db.HangboardSessions
            .Where(h => h.UserId == user.Id && h.Timestamp >= cutoff)
            .ToListAsync();
        var pullups = await db.PullupSessions
            .Where(p => p.UserId == user.Id && p.Timestamp >= cutoff)
            .ToListAsync();
        var trainingScore = CalculateTrainingScore(hangboard, pullups);

        var boulderCurve = await BuildBoulderCurveAsync(db, user.Id, windowDays);
        var trainingCurve = await BuildTrainingCurveAsync(db, user.Id, windowDays);

        return new UserProgression(boulderScore, boulderGrade, trainingScore, boulderCurve, trainingCurve, windowDays);
    }

    public async Task<List<DayActivity>> GetActivityGridAsync(int weeks = 20)
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

    public async Task<DaySummary> GetDaySummaryAsync(DateOnly date)
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

    public async Task UpdateProgressionWindowAsync(int days)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var dbUser = await db.Users.FirstAsync(u => u.Id == user.Id);
        dbUser.ProgressionWindowDays = Math.Clamp(days, 7, 365);
        await db.SaveChangesAsync();
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

    private async Task<List<ProgressionPoint>> BuildBoulderCurveAsync(BlocwerkDbContext db, Guid userId, int windowDays)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-windowDays);
        var attempts = await db.Attempts
            .Include(a => a.Boulder)
            .Where(a => a.UserId == userId && a.Timestamp >= since)
            .OrderBy(a => a.Timestamp)
            .ToListAsync();

        var curve = new List<ProgressionPoint>();
        var startDate = DateOnly.FromDateTime(since.Date);
        var endDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        for (var date = startDate; date <= endDate; date = date.AddDays(7))
        {
            var windowEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var windowStart = windowEnd.AddDays(-windowDays);
            var windowAttempts = attempts.Where(a => a.Timestamp >= windowStart && a.Timestamp < windowEnd).ToList();
            var score = CalculateBoulderScore(windowAttempts);
            curve.Add(new ProgressionPoint(date, score));
        }

        return curve;
    }

    private async Task<List<ProgressionPoint>> BuildTrainingCurveAsync(BlocwerkDbContext db, Guid userId, int windowDays)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-windowDays);

        var hangboard = await db.HangboardSessions
            .Where(h => h.UserId == userId && h.Timestamp >= since)
            .ToListAsync();
        var pullups = await db.PullupSessions
            .Where(p => p.UserId == userId && p.Timestamp >= since)
            .ToListAsync();

        var curve = new List<ProgressionPoint>();
        var startDate = DateOnly.FromDateTime(since.Date);
        var endDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        for (var date = startDate; date <= endDate; date = date.AddDays(7))
        {
            var windowEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var windowStart = windowEnd.AddDays(-windowDays);
            var h = hangboard.Where(x => x.Timestamp >= windowStart && x.Timestamp < windowEnd).ToList();
            var p = pullups.Where(x => x.Timestamp >= windowStart && x.Timestamp < windowEnd).ToList();
            curve.Add(new ProgressionPoint(date, CalculateTrainingScore(h, p)));
        }

        return curve;
    }
}
