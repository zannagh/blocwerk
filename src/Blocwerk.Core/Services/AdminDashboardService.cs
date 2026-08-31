using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Builds the app-wide admin overview from a global view of the database. A fresh
/// <see cref="BlocwerkDbContext"/> with <c>CurrentUserId = Guid.Empty</c> bypasses the per-user wall
/// query filter, so every count spans all walls. The per-wall figures are gathered with grouped
/// queries keyed by wall id (one round trip each) and stitched together in memory to avoid an N+1.
/// </summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;

    public AdminDashboardService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
    }

    public async Task<AdminOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        // Defence-in-depth: this method sets CurrentUserId = Guid.Empty to read across every tenant,
        // so it fails closed on its own rather than trusting the caller to have gated it. This sits
        // behind the admin page's [Authorize(Policy = AppAdmin)]; a real admin still passes.
        var user = await currentUserService.GetCurrentUserAsync();
        if (user?.Role != IdentityRole.Admin)
        {
            throw new UnauthorizedAccessException("The admin overview is restricted to administrators.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = Guid.Empty;

        var totalWalls = await db.Walls.CountAsync(cancellationToken);
        var totalUsers = await db.Users.CountAsync(cancellationToken);
        var totalBoulders = await db.Boulders.CountAsync(b => !b.IsArchived, cancellationToken);

        var walls = await db.Walls
            .Select(w => new { w.Id, w.Name })
            .ToListAsync(cancellationToken);

        var memberCounts = await db.WallMembers
            .GroupBy(m => m.WallId)
            .Select(g => new { WallId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WallId, x => x.Count, cancellationToken);

        var boulderCounts = await db.Boulders
            .Where(b => !b.IsArchived)
            .GroupBy(b => b.WallId)
            .Select(g => new { WallId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WallId, x => x.Count, cancellationToken);

        var cutoff = DateTimeOffset.UtcNow - RecentWindow;
        var recentActivity = await db.ActivityLog
            .Where(a => a.Timestamp >= cutoff)
            .GroupBy(a => a.WallId)
            .Select(g => new { WallId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WallId, x => x.Count, cancellationToken);

        var lastActivity = await db.ActivityLog
            .GroupBy(a => a.WallId)
            .Select(g => new { WallId = g.Key, Last = g.Max(a => a.Timestamp) })
            .ToDictionaryAsync(x => x.WallId, x => x.Last, cancellationToken);

        var activeSessions = await db.ClimbingSessions
            .Where(s => s.EndedAt == null)
            .GroupBy(s => s.WallId)
            .Select(g => new { WallId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WallId, x => x.Count, cancellationToken);

        var wallStats = walls
            .Select(w => new AdminWallStat(
                w.Id,
                w.Name,
                memberCounts.GetValueOrDefault(w.Id),
                boulderCounts.GetValueOrDefault(w.Id),
                recentActivity.GetValueOrDefault(w.Id),
                activeSessions.GetValueOrDefault(w.Id),
                lastActivity.TryGetValue(w.Id, out var last) ? last : null))
            .OrderByDescending(s => s.RecentActivityCount)
            .ThenBy(s => s.WallName)
            .ToList();

        return new AdminOverview(totalWalls, totalUsers, totalBoulders, wallStats);
    }
}
