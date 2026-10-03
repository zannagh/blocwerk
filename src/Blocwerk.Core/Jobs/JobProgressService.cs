// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Jobs;

/// <summary>
/// <see cref="IJobProgressService"/>: decides the wall scope from the acting user exactly as the administration area and the
/// wall-admin guards do (<see cref="AppAdminGuard"/>, wall owner or admin member), then reads through
/// <see cref="IJobProgressReader"/>. A kiosk session is refused; an API-key principal is its owner.
/// </summary>
public sealed class JobProgressService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUser,
    IJobProgressReader reader,
    IKioskContext? kioskContext = null) : IJobProgressService
{
    /// <summary>How far back ended jobs are listed by default.</summary>
    public static readonly TimeSpan DefaultRecent = TimeSpan.FromHours(24);

    /// <summary>The longest window a caller may ask for.</summary>
    public static readonly TimeSpan MaxRecent = TimeSpan.FromDays(7);

    private const string Action = "Watching background jobs";

    public async Task<JobProgressSnapshot> ListAsync(Guid? wallId, TimeSpan? recent, CancellationToken ct)
    {
        var walls = await WallScopeAsync(wallId, ct);
        var window = recent is { } r ? (r < TimeSpan.Zero ? TimeSpan.Zero : r > MaxRecent ? MaxRecent : r) : DefaultRecent;
        return await reader.ReadAsync(new JobProgressScope(walls, window), ct);
    }

    public async Task<bool> CanWatchAsync(Guid? wallId, CancellationToken ct)
    {
        try
        {
            var walls = await WallScopeAsync(wallId, ct);
            return walls is null || walls.Count > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The walls the acting user may watch: null (every wall) for an app administrator, else the walls they own or
    /// administer; narrowed to <paramref name="wallId"/> when given (refused when it is not one of them).
    /// </summary>
    internal async Task<IReadOnlySet<Guid>?> WallScopeAsync(Guid? wallId, CancellationToken ct)
    {
        var user = await currentUser.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
        if (await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct))
        {
            return wallId is { } only ? new HashSet<Guid> { only } : null;
        }

        var walls = await AdministeredWallsAsync(db, user.Id, ct);
        if (wallId is not { } id)
        {
            return walls;
        }

        return walls.Contains(id)
            ? new HashSet<Guid> { id }
            : throw new UnauthorizedAccessException($"User {user.Id} is not an admin of wall {id}.");
    }

    /// <summary>The walls the user owns or is an admin member of (the two branches of <see cref="WallAdminGuard"/>).</summary>
    internal static async Task<HashSet<Guid>> AdministeredWallsAsync(BlocwerkDbContext db, Guid userId, CancellationToken ct)
    {
        var member = await db.WallMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Role == WallRole.Admin)
            .Select(m => m.WallId)
            .ToListAsync(ct);
        var owned = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OwnerId == userId)
            .Select(w => w.Id)
            .ToListAsync(ct);
        return [.. member, .. owned];
    }
}
