using System.Data;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IAccountDeletionService"/> over a single <see cref="BlocwerkDbContext"/> and a single
/// transaction, mirroring <see cref="AccountMergeService"/>: everything commits at once or not at all.
/// </summary>
public partial class AccountDeletionService : IAccountDeletionService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IBetaVideoStorage betaVideoStorage;
    private readonly ICurrentUserService currentUserService;
    private readonly IKioskContext? kioskContext;
    private readonly ILogger<AccountDeletionService> logger;

    public AccountDeletionService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IBetaVideoStorage betaVideoStorage,
        ICurrentUserService currentUserService,
        ILogger<AccountDeletionService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.betaVideoStorage = betaVideoStorage;
        this.currentUserService = currentUserService;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    public async Task<AccountDeletionPreview> PreviewAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        KioskGuard.EnsureNotKiosk(kioskContext, db, "Previewing an account deletion");
        await EnsureSelfAsync(userId);

        // A fresh context leaves CurrentUserId at Guid.Empty, which short-circuits the Wall
        // membership query filter; IgnoreQueryFilters is still spelled out on every wall read so a
        // kiosk-scoped factory could never narrow the preview to one wall and under-report.
        db.CurrentUserId = Guid.Empty;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDeleted || GhostUser.Is(userId))
        {
            return new AccountDeletionPreview();
        }

        var (transfers, blocking) = await ResolveWallOwnershipAsync(db, userId, ct);

        return new AccountDeletionPreview
        {
            BlockingWallNames = blocking,
            WallTransfers = transfers,
            BouldersKept = await db.Boulders.CountAsync(b => b.CreatedByUserId == userId, ct),
            CommentsKept = await db.BoulderComments.CountAsync(c => c.UserId == userId, ct),
            AttemptsKept = await db.Attempts.CountAsync(a => a.UserId == userId, ct),
            MembershipsRemoved = await db.WallMembers.CountAsync(m => m.UserId == userId, ct),
            TrainingSessionsRemoved =
                await db.HangboardSessions.CountAsync(s => s.UserId == userId, ct)
                + await db.PullupSessions.CountAsync(s => s.UserId == userId, ct)
                + await db.ClimbingSessions.CountAsync(s => s.UserId == userId, ct),
            BetaVideosRemoved = await db.BetaVideos.CountAsync(v => v.UploadedByUserId == userId, ct),
        };
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        // A tablet bolted to a gym wall acts AS a consenting member for thirty minutes. Erasing that
        // member's account is permanent, reaches every wall they belong to, and is exactly the kind
        // of blast radius the kiosk axis exists to contain — so it is refused on the SERVICE, not
        // merely kept off the tablet's route list, because the page is an interactive Blazor
        // component that calls straight in here inside the circuit where no middleware runs.
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Deleting the account");
        await EnsureSelfAsync(userId);

        db.CurrentUserId = Guid.Empty;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDeleted || GhostUser.Is(userId))
        {
            // Idempotent on purpose: a double submit, a replayed request or an id that never existed
            // must not throw at somebody who is trying to leave. The reserved Ghost row is refused
            // outright — it is a system row nobody can sign in as, and scrubbing it would break
            // every boulder attributed to it.
            return false;
        }

        // A provisional read so an obviously-blocked deletion fails before anything is touched. It is
        // NOT the decision: that is re-taken inside the transaction below.
        var (_, provisionalBlocking) = await ResolveWallOwnershipAsync(db, userId, ct);
        if (provisionalBlocking.Count > 0)
        {
            throw new AccountDeletionBlockedException(provisionalBlocking);
        }

        await OnOwnershipResolvedAsync(ct);

        // Read the file names BEFORE the rows go, but delete the files only after the transaction
        // commits — an aborted transaction must not leave clips missing from disk.
        var betaVideoFiles = await db.BetaVideos
            .Where(v => v.UploadedByUserId == userId && v.StoragePath != null)
            .Select(v => v.StoragePath!)
            .ToListAsync(ct);

        // RefreshToken.UserId is the OAuth subject string, not a User FK, so the subjects have to be
        // captured before the identities are deleted.
        var tokenOwnership = await ResolveRefreshTokenOwnershipAsync(db, user, ct);

        // Serializable, because the ownership decision below is a read the writes depend on: two
        // co-admins deleting at the same moment would otherwise each see the other as a live
        // successor and hand each other the wall. Under PostgreSQL one of the two transactions is
        // then refused with a serialization failure and nothing of it stands.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // The authoritative decision, re-taken under the transaction against the same rows the
        // transfer is about to write.
        var (transfers, blocking) = await ResolveWallOwnershipAsync(db, userId, ct);
        if (blocking.Count > 0)
        {
            throw new AccountDeletionBlockedException(blocking);
        }

        await TransferWallOwnershipAsync(db, userId, transfers, ct);
        await ErasePersonalRowsAsync(db, userId, tokenOwnership, ct);
        await ScrubUserRowAsync(db, user, ct);
        await AssertNoWallLeftToADeletedOwnerAsync(db, transfers, ct);

        await transaction.CommitAsync(ct);

        DeleteBetaVideoFiles(userId, betaVideoFiles);

        // The audit record: that a deletion happened, when, and how much moved — and nothing about
        // who the person was. User.DeletedAt is the durable half of the same record.
        logger.LogInformation(
            "Account {UserId} erased at {DeletedAt}; {WallCount} wall(s) transferred, {ClipCount} beta clip(s) removed.",
            userId,
            user.DeletedAt,
            transfers.Count,
            betaVideoFiles.Count);

        return true;
    }

    /// <summary>
    /// A test seam, and nothing else: awaited once between the provisional ownership read and the
    /// transaction that re-takes the decision, so a test can commit a competing change in exactly the
    /// gap the TOCTOU lives in. Does nothing in production.
    /// </summary>
    protected virtual Task OnOwnershipResolvedAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Refuses to erase anybody but the caller.
    /// </summary>
    /// <remarks>
    /// The page that calls this passes the signed-in user's own id, but nothing about the service
    /// made that true: <see cref="PreviewAsync"/> would happily enumerate a stranger's wall names and
    /// content counts for any id that was guessed or leaked, and <see cref="DeleteAsync"/> would
    /// irreversibly erase them. Deletion is self-service by design — there is deliberately no admin
    /// path, because nothing in the app needs one and adding one would make this guard conditional.
    /// </remarks>
    private async Task EnsureSelfAsync(Guid userId)
    {
        var current = await currentUserService.GetCurrentUserAsync();
        if (current.Id != userId)
        {
            throw new UnauthorizedAccessException(
                "An account can only be previewed or erased by the person signed in to it.");
        }
    }

    /// <summary>
    /// Unlinks the beta clips whose rows the (already committed) transaction removed.
    /// </summary>
    /// <remarks>
    /// The commit is the point of no return: the account IS erased by the time this runs. A storage
    /// failure here therefore may not be reported as "nothing was deleted" and must not stop the
    /// caller signing the person out — it leaves orphaned files, which is a cleanup job, not a failed
    /// deletion. The paths are logged at warning level so they can be swept up by hand.
    /// </remarks>
    private void DeleteBetaVideoFiles(Guid userId, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                betaVideoStorage.Delete(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Account {UserId} was erased, but the beta clip file {StoragePath} could not be removed; it is now orphaned and needs deleting by hand.",
                    userId,
                    file);
            }
        }
    }
}
