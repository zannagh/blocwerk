using System.Linq.Expressions;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IActivityLogService
{
    Task LogAsync(Guid wallId, Guid? boulderId, ActivityType type, string? details = null);

    /// <summary>
    /// Logs an entry attributed to the Ghost system row instead of to a session user.
    /// </summary>
    /// <remarks>
    /// Exists for the one caller that has no session user to resolve: a boulder set at an unattended
    /// kiosk tablet. It takes NO user id — an earlier draft did, which put "attribute this entry to
    /// any user id you like, no authorisation performed" on an interface injected into a dozen
    /// components for the sake of a single call site. The attribution is baked in instead, so the
    /// primitive that exists is exactly the one that is needed and there is nothing here to misuse.
    /// It performs no authorisation of its own — the caller must already have proven it may write to
    /// the wall (see <c>BoulderService.EnsureAnonymousKioskCreateAllowedAsync</c>).
    /// </remarks>
    Task LogAsGhostAsync(Guid wallId, Guid? boulderId, ActivityType type, string? details = null);

    /// <summary>
    /// A wall's activity, newest first. The caller must be a member of the wall, or pass the
    /// wall's <paramref name="shareToken"/> for the anonymous share-link view.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller may not read this wall.</exception>
    Task<(List<ActivityLogEntry> Items, int TotalCount)> GetWallActivityAsync(
        Guid wallId,
        int page = 0,
        int pageSize = 20,
        string? shareToken = null);

    /// <summary>
    /// One boulder's activity, newest first. The caller must be a member of the boulder's wall, or
    /// pass that wall's <paramref name="shareToken"/> for the anonymous share-link view.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller may not read this boulder.</exception>
    Task<(List<ActivityLogEntry> Items, int TotalCount)> GetBoulderActivityAsync(
        Guid boulderId,
        int page = 0,
        int pageSize = 5,
        string? shareToken = null);
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
        await WriteAsync(wallId, boulderId, type, user.Id, details);
    }

    public Task LogAsGhostAsync(Guid wallId, Guid? boulderId, ActivityType type, string? details = null)
    {
        return WriteAsync(wallId, boulderId, type, GhostUser.Id, details);
    }

    /// <summary>
    /// The single insert both public entry points funnel through. Private on purpose: the arbitrary
    /// user id lives here, where nothing outside this class can reach it.
    /// </summary>
    private async Task WriteAsync(Guid wallId, Guid? boulderId, ActivityType type, Guid userId, string? details)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        db.ActivityLog.Add(new ActivityLogEntry
        {
            WallId = wallId,
            BoulderId = boulderId,
            UserId = userId,
            Type = type,
            Details = details,
        });

        await db.SaveChangesAsync();
    }

    public async Task<(List<ActivityLogEntry> Items, int TotalCount)> GetWallActivityAsync(
        Guid wallId,
        int page = 0,
        int pageSize = 20,
        string? shareToken = null)
    {
        await using var db = await OpenReadableAsync(shareToken);

        if (!await CanReadWallAsync(db, wallId, shareToken))
        {
            throw new UnauthorizedAccessException($"Wall {wallId} is not readable by this caller.");
        }

        return await PageAsync(db, a => a.WallId == wallId, page, pageSize);
    }

    public async Task<(List<ActivityLogEntry> Items, int TotalCount)> GetBoulderActivityAsync(
        Guid boulderId,
        int page = 0,
        int pageSize = 5,
        string? shareToken = null)
    {
        await using var db = await OpenReadableAsync(shareToken);

        // The log carries usernames, so it must not be readable for a boulder the caller cannot
        // see. Resolve the boulder's wall under the same gate the boulder itself uses, rather than
        // trusting the caller-supplied id: the entries themselves are not filtered by anything.
        var boulder = await db.Boulders
            .AsNoTracking()
            .Where(b => b.Id == boulderId)
            .Select(b => new { b.WallId, b.IsDraft })
            .FirstOrDefaultAsync();

        // A share viewer never sees a draft boulder, so it must not see a draft's log either.
        if (boulder is null
            || (!string.IsNullOrEmpty(shareToken) && boulder.IsDraft)
            || !await CanReadWallAsync(db, boulder.WallId, shareToken))
        {
            throw new UnauthorizedAccessException($"Boulder {boulderId} is not readable by this caller.");
        }

        return await PageAsync(db, a => a.BoulderId == boulderId, page, pageSize);
    }

    /// <summary>
    /// A share token takes the anonymous path (the wall filter must stay open for it to resolve
    /// the wall at all); without one the caller has to be signed in, and the context carries their
    /// id so the wall query filter becomes the membership check.
    /// </summary>
    private async Task<BlocwerkDbContext> OpenReadableAsync(string? shareToken)
    {
        var currentUserId = Guid.Empty;
        if (string.IsNullOrEmpty(shareToken))
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            currentUserId = user.Id;
        }

        var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = currentUserId;
        return db;
    }

    private static async Task<bool> CanReadWallAsync(BlocwerkDbContext db, Guid wallId, string? shareToken)
    {
        return string.IsNullOrEmpty(shareToken)

            // CurrentUserId is the signed-in caller here, so the wall query filter is the check.
            ? await db.Walls.AnyAsync(w => w.Id == wallId)
            : await db.Walls.AnyAsync(w => w.Id == wallId && w.ShareToken == shareToken);
    }

    private static async Task<(List<ActivityLogEntry> Items, int TotalCount)> PageAsync(
        BlocwerkDbContext db,
        Expression<Func<ActivityLogEntry, bool>> predicate,
        int page,
        int pageSize)
    {
        var query = db.ActivityLog
            .AsNoTracking()
            .Include(a => a.User)
            .Where(predicate)
            .OrderByDescending(a => a.Timestamp);

        var total = await query.CountAsync();
        var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync();

        return (items, total);
    }
}
