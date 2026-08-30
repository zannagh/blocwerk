using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Merges a source user into a target user (see <see cref="IAccountMergeService"/>). The whole merge
/// runs in one transaction on a single <see cref="BlocwerkDbContext"/> and commits exactly once, so a
/// failure at any step leaves the two accounts untouched.
/// </summary>
public partial class AccountMergeService : IAccountMergeService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<AccountMergeService> logger;

    public AccountMergeService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ILogger<AccountMergeService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    public async Task MergeUsersAsync(Guid sourceUserId, Guid targetUserId)
    {
        if (sourceUserId == targetUserId)
        {
            throw new InvalidOperationException("Cannot merge a user into itself.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();

        // CurrentUserId stays Guid.Empty on this fresh context, which disables the Wall membership
        // query filter (it short-circuits to "true"). Wall updates below still call IgnoreQueryFilters
        // defensively so the merge can never be silently scoped away.
        var source = await db.Users.FirstOrDefaultAsync(u => u.Id == sourceUserId)
                     ?? throw new InvalidOperationException($"Source user {sourceUserId} does not exist.");
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId)
                     ?? throw new InvalidOperationException($"Target user {targetUserId} does not exist.");

        // Refresh tokens are keyed by the provider subject (nameid), not by User.Id, so capture the
        // source's provider subjects up front — before its identities are re-pointed at the target —
        // to know which tokens to drop.
        var sourceProviderSubjects = await db.UserIdentities
            .Where(i => i.UserId == sourceUserId)
            .Select(i => i.ProviderUserId)
            .ToListAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();

        await RepointSimpleForeignKeysAsync(db, sourceUserId, targetUserId);
        await RepointRestrictForeignKeysAsync(db, sourceUserId, targetUserId);

        await DedupWallMembersAsync(db, sourceUserId, targetUserId);
        await DedupBoulderRatingsAsync(db, sourceUserId, targetUserId);
        await DedupBoulderFavoritesAsync(db, sourceUserId, targetUserId);

        // Move the source's provider identities onto the target so every provider that used to resolve
        // to the source now resolves to the target.
        await db.UserIdentities
            .Where(i => i.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UserId, targetUserId));

        // RefreshToken.UserId is the JWT subject string, not a User FK — just drop the source's.
        if (sourceProviderSubjects.Count > 0)
        {
            await db.RefreshTokens
                .Where(t => sourceProviderSubjects.Contains(t.UserId))
                .ExecuteDeleteAsync();
        }

        // Keep the target's profile, but never lose an Admin role in the merge.
        if (source.Role > target.Role)
        {
            target.Role = source.Role;
            await db.SaveChangesAsync();
        }

        db.Users.Remove(source);
        await db.SaveChangesAsync();

        await transaction.CommitAsync();

        logger.LogInformation(
            "Merged user {SourceUserId} into {TargetUserId} (final role {Role}).",
            sourceUserId,
            targetUserId,
            target.Role);
    }

    /// <summary>
    /// Re-points the FKs that can be blindly updated (Cascade / nullable / no-FK staging columns): the
    /// source's rows simply become the target's.
    /// </summary>
    private static async Task RepointSimpleForeignKeysAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId)
    {
        await db.Attempts.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.Activities.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.ActivityLog.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.BoulderComments.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.HangboardSessions.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.PullupSessions.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.ClimbingSessions.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));
        await db.ApiKeys.Where(x => x.UserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetUserId));

        // Nullable creator column.
        await db.HoldLinks.Where(x => x.CreatedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, (Guid?)targetUserId));

        // Nullable staging columns (no FK constraint, but still user-referencing).
        await db.Walls.IgnoreQueryFilters().Where(x => x.StagedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StagedByUserId, (Guid?)targetUserId));
        await db.WallPanels.Where(x => x.StagedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StagedByUserId, (Guid?)targetUserId));
    }

    /// <summary>
    /// Re-points the Restrict FKs. These would block the final source delete if left dangling, so they
    /// must move to the target before the user row is removed.
    /// </summary>
    private static async Task RepointRestrictForeignKeysAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId)
    {
        await db.Walls.IgnoreQueryFilters().Where(x => x.OwnerId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerId, targetUserId));
        await db.Boulders.Where(x => x.CreatedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, targetUserId));
        await db.GradeProposals.Where(x => x.ProposedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProposedByUserId, targetUserId));
        await db.BetaVideos.Where(x => x.UploadedByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UploadedByUserId, targetUserId));
        await db.WallResets.Where(x => x.ResetByUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResetByUserId, targetUserId));
    }
}
