using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Reads and writes the resumable state of an in-flight big-wall update: the session header (status,
/// phase cursor, neighbour index, who) here, the decision rows in the Decisions partial. Every method is
/// gated by <see cref="WallAdminGuard"/>, and a session belongs to the WALL — any admin may continue it.
/// </summary>
public partial class WallUpdateSessionService : IWallUpdateSessionService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly ILogger<WallUpdateSessionService> logger;

    public WallUpdateSessionService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<WallUpdateSessionService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<WallUpdateSessionInfo?> GetOpenSessionAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session is null)
        {
            return null;
        }

        return await WallUpdateSessionDescriptor.DescribeAsync(db, session);
    }

    /// <inheritdoc/>
    public async Task<WallUpdateSessionInfo> SetPhaseAsync(
        Guid wallId, WallUpdatePhase phase, int neighbourIndex = 0)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await RequireOpenAsync(db, wallId);
        session.Phase = phase;
        session.NeighbourIndex = Math.Max(0, neighbourIndex);
        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();

        logger.LogDebug(
            "Wall update session {SessionId} on wall {WallId} moved to {Phase} (neighbour {Index}) by {UserId}",
            session.Id, wallId, phase, session.NeighbourIndex, user.Id);
        return await WallUpdateSessionDescriptor.DescribeAsync(db, session);
    }

    /// <summary>
    /// The wall's open session, or a throw. Every decision write needs one: a decision without a session
    /// has nothing to hang off, and silently creating one here would hide a wizard that lost its footing.
    /// </summary>
    private static async Task<WallUpdateSession> RequireOpenAsync(BlocwerkDbContext db, Guid wallId)
    {
        return await WallUpdateSessions.FindOpenAsync(db, wallId)
            ?? throw new InvalidOperationException("No in-flight big update on this wall.");
    }
}
