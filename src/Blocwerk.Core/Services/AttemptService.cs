using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// True when someone other than the boulder's owner has sent or flashed it. Used to gate
    /// full edits: once another climber has completed a boulder, only its name and grade may
    /// change, so a revise cannot pull the holds out from under an existing ascent.
    /// </summary>
    Task<bool> HasBeenSentByOthersAsync(Guid boulderId, Guid ownerId);
}

public record AttemptSummary(int TotalAttempts, bool HasSent, bool HasFlashed);

public class AttemptService : IAttemptService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<AttemptService> _logger;

    public AttemptService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IActivityLogService activityLogService,
        ILogger<AttemptService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<Attempt> LogAttemptAsync(
        Guid boulderId,
        AttemptType type,
        string? notes = null,
        Guid? clientRequestId = null,
        DateTimeOffset? timestamp = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.Log");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId);
            if (boulder is null)
            {
                _logger.LogWarning("Cannot log attempt: boulder {BoulderId} not found for {UserId}", boulderId, user.Id);
                throw new InvalidOperationException("Boulder not found");
            }

            if (clientRequestId.HasValue)
            {
                var replayed = await db.Attempts
                    .FirstOrDefaultAsync(a => a.ClientRequestId == clientRequestId.Value);

                if (replayed != null)
                {
                    return replayed;
                }
            }

            // Debounce accidental double-logs: the same user logging the same action (Type) on the
            // same boulder within a 60s window is treated as a duplicate and silently ignored. Keyed
            // on user+boulder+type, so an Attempt followed by a Send is NOT debounced against each
            // other. The window is measured in REAL time: it anchors on the caller-supplied
            // timestamp (the moment of the tap, threaded through even from the offline queue) and
            // only falls back to "now" when none is given. That is what lets an offline batch of
            // same-type attempts logged minutes apart all persist on reconnect — each carries its
            // own real tap time, so only taps genuinely within 60s of each other collapse.
            var effectiveTimestamp = timestamp ?? DateTimeOffset.UtcNow;
            var duplicate = await db.Attempts
                .FirstOrDefaultAsync(a =>
                    a.UserId == user.Id
                    && a.BoulderId == boulderId
                    && a.Type == type
                    && a.Timestamp >= effectiveTimestamp.AddSeconds(-60)
                    && a.Timestamp <= effectiveTimestamp.AddSeconds(60));

            if (duplicate != null)
            {
                return duplicate;
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

            // Group the attempt into a training activity (created/extended in this same context, so
            // it commits atomically with the attempt). The boulder's wall becomes the activity's wall.
            attempt.ActivityId = await ActivityGrouping.ResolveActivityIdAsync(db, user.Id, attempt.Timestamp, boulder.WallId);

            db.Attempts.Add(attempt);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordAttemptLogged(boulder.WallId);

            _logger.LogInformation("Attempt logged on boulder {BoulderId} of type {AttemptType} by {UserId}", boulder.Id, type, user.Id);

            await _activityLogService.LogAsync(boulder.WallId, boulderId, ActivityType.AttemptLogged, type.ToString());
            return attempt;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Attempt>> GetAttemptsForBoulderAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.GetForBoulder");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            return await db.Attempts
                .AsNoTracking()
                .Include(a => a.User)
                .Where(a => a.BoulderId == boulderId)
                .OrderByDescending(a => a.Timestamp)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Attempt>> GetMyAttemptsAsync(Guid? wallId = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.GetMine", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var query = db.Attempts
                .AsNoTracking()
                .Include(a => a.Boulder)
                .Where(a => a.UserId == user.Id);

            if (wallId.HasValue)
            {
                query = query.Where(a => a.Boulder.WallId == wallId.Value);
            }

            return await query.OrderByDescending(a => a.Timestamp).ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteAttemptAsync(Guid attemptId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.Delete");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == user.Id);
            if (attempt is null)
            {
                _logger.LogWarning("Cannot delete attempt {AttemptId}: not found for {UserId}", attemptId, user.Id);
                throw new InvalidOperationException("Attempt not found");
            }

            db.Attempts.Remove(attempt);
            await db.SaveChangesAsync();

            _logger.LogInformation("Attempt {AttemptId} deleted by {UserId}", attemptId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> HasBeenSentByOthersAsync(Guid boulderId, Guid ownerId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.HasBeenSentByOthers");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            return await db.Attempts
                .AsNoTracking()
                .AnyAsync(a => a.BoulderId == boulderId
                    && a.UserId != ownerId
                    && (a.Type == AttemptType.Send || a.Type == AttemptType.Flash));
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<AttemptSummary> GetBoulderSummaryForUserAsync(Guid boulderId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Attempt.GetBoulderSummary");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Only the attempt Type is needed to compute the summary — pull that single column
            // rather than materialising whole Attempt rows (notes, timestamps, fks) just to count.
            var types = await db.Attempts
                .AsNoTracking()
                .Where(a => a.BoulderId == boulderId && a.UserId == user.Id)
                .Select(a => a.Type)
                .ToListAsync();

            return new AttemptSummary(
                TotalAttempts: types.Count,
                HasSent: types.Contains(AttemptType.Send),
                HasFlashed: types.Contains(AttemptType.Flash));
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
