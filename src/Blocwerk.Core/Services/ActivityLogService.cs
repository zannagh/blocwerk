using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IActivityLogService
{
    Task LogAsync(Guid wallId, Guid? boulderId, ActivityType type, string? details = null);

    Task<(List<ActivityLogEntry> Items, int TotalCount)> GetWallActivityAsync(Guid wallId, int page = 0, int pageSize = 20);

    Task<(List<ActivityLogEntry> Items, int TotalCount)> GetBoulderActivityAsync(Guid boulderId, int page = 0, int pageSize = 5);
}

public class ActivityLogService : IActivityLogService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;

    public ActivityLogService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
    }

    public async Task LogAsync(Guid wallId, Guid? boulderId, ActivityType type, string? details = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        db.ActivityLog.Add(new ActivityLogEntry
        {
            WallId = wallId,
            BoulderId = boulderId,
            UserId = user.Id,
            Type = type,
            Details = details,
        });

        await db.SaveChangesAsync();
    }

    public async Task<(List<ActivityLogEntry> Items, int TotalCount)> GetWallActivityAsync(Guid wallId, int page = 0, int pageSize = 20)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var query = db.ActivityLog
            .AsNoTracking()
            .Include(a => a.User)
            .Where(a => a.WallId == wallId)
            .OrderByDescending(a => a.Timestamp);

        var total = await query.CountAsync();
        var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync();

        return (items, total);
    }

    public async Task<(List<ActivityLogEntry> Items, int TotalCount)> GetBoulderActivityAsync(Guid boulderId, int page = 0, int pageSize = 5)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var query = db.ActivityLog
            .AsNoTracking()
            .Include(a => a.User)
            .Where(a => a.BoulderId == boulderId)
            .OrderByDescending(a => a.Timestamp);

        var total = await query.CountAsync();
        var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync();

        return (items, total);
    }
}
