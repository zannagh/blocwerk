using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IAttemptService
{
    Task<Attempt> LogAttemptAsync(Guid boulderId, AttemptType type, string? notes = null);

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

    public AttemptService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
    }

    public async Task<Attempt> LogAttemptAsync(Guid boulderId, AttemptType type, string? notes = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        var attempt = new Attempt
        {
            BoulderId = boulderId,
            UserId = user.Id,
            Type = type,
            Notes = notes,
        };

        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
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
