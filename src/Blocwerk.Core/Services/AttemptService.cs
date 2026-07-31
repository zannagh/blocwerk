using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IAttemptService
{
    /// <summary>
    /// Logs an attempt. Pass a stable <paramref name="clientRequestId"/> when the call may
    /// be replayed from an offline queue: a second call with the same id returns the row
    /// already stored instead of logging the attempt twice. Pass <paramref name="timestamp"/>
    /// to backdate the attempt to a chosen time instead of "now".
    /// </summary>
    Task<Attempt> LogAttemptAsync(
        Guid boulderId,
        AttemptType type,
        string? notes = null,
        Guid? clientRequestId = null,
        DateTimeOffset? timestamp = null);

    Task<List<Attempt>> GetAttemptsForBoulderAsync(Guid boulderId);

    Task<List<Attempt>> GetMyAttemptsAsync(Guid? wallId = null);

    Task DeleteAttemptAsync(Guid attemptId);

    Task<AttemptSummary> GetBoulderSummaryForUserAsync(Guid boulderId);
}

public record AttemptSummary(int TotalAttempts, bool HasSent, bool HasFlashed);

public class AttemptService : IAttemptService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;

    public AttemptService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
    }

    public async Task<Attempt> LogAttemptAsync(
        Guid boulderId,
        AttemptType type,
        string? notes = null,
        Guid? clientRequestId = null,
        DateTimeOffset? timestamp = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        if (clientRequestId.HasValue)
        {
            var replayed = await db.Attempts
                .FirstOrDefaultAsync(a => a.ClientRequestId == clientRequestId.Value);

            if (replayed != null)
            {
                return replayed;
            }
        }

        var attempt = new Attempt
        {
            BoulderId = boulderId,
            UserId = user.Id,
            Type = type,
            Notes = notes,
            ClientRequestId = clientRequestId,
        };

        if (timestamp.HasValue)
        {
            attempt.Timestamp = timestamp.Value;
        }

        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();

        await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.AttemptLogged, type.ToString());
        return attempt;
    }

    public async Task<List<Attempt>> GetAttemptsForBoulderAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        return await db.Attempts
            .Include(a => a.User)
            .Where(a => a.BoulderId == boulderId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();
    }

    public async Task<List<Attempt>> GetMyAttemptsAsync(Guid? wallId = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var query = db.Attempts
            .Include(a => a.Boulder)
            .Where(a => a.UserId == user.Id);

        if (wallId.HasValue)
        {
            query = query.Where(a => a.Boulder.WallId == wallId.Value);
        }

        return await query.OrderByDescending(a => a.Timestamp).ToListAsync();
    }

    public async Task DeleteAttemptAsync(Guid attemptId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == user.Id)
                      ?? throw new InvalidOperationException("Attempt not found");

        db.Attempts.Remove(attempt);
        await db.SaveChangesAsync();
    }

    public async Task<AttemptSummary> GetBoulderSummaryForUserAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var attempts = await db.Attempts
            .Where(a => a.BoulderId == boulderId && a.UserId == user.Id)
            .ToListAsync();

        return new AttemptSummary(
            TotalAttempts: attempts.Count,
            HasSent: attempts.Any(a => a.Type == AttemptType.Send),
            HasFlashed: attempts.Any(a => a.Type == AttemptType.Flash));
    }
}
