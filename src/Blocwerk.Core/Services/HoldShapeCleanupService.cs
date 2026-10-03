using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldShapeCleanupService"/>. Pure geometry lives in <see cref="HoldShapeCleanup"/>; this class
/// loads each live photo's holds, applies the planned changes and saves them in one named journal batch. It
/// needs no outliner and no photo decoding, so it runs on any host. See the Planning part for plan caching.
/// </summary>
public sealed partial class HoldShapeCleanupService : IHoldShapeCleanupService
{
    /// <summary>The journal batch label of an applied clean-up.</summary>
    public const string BatchLabel = "hold-shape-cleanup";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IChangeJournal journal;
    private readonly ChangeJournalReverter reverter;
    private readonly ILogger<HoldShapeCleanupService> logger;
    private readonly IKioskContext? kioskContext;
    private readonly IHoldFootprintService? footprints;

    /// <summary>Initializes a new instance of the <see cref="HoldShapeCleanupService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="journal">Opens the named journal batch.</param>
    /// <param name="reverter">Reverts a batch.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    /// <param name="footprints">Refreshes the 3D hold footprints after shapes changed; optional.</param>
    public HoldShapeCleanupService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IChangeJournal journal,
        ChangeJournalReverter reverter,
        ILogger<HoldShapeCleanupService> logger,
        IKioskContext? kioskContext = null,
        IHoldFootprintService? footprints = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.journal = journal;
        this.reverter = reverter;
        this.logger = logger;
        this.kioskContext = kioskContext;
        this.footprints = footprints;
    }

    /// <inheritdoc/>
    public async Task<HoldShapeCleanupStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
            var batches = await db.ChangeJournalBatches.AsNoTracking()
                .Where(b => b.Label == BatchLabel && b.ScopeId == wallId && b.Status == ChangeJournalStatus.Recorded)
                .ToListAsync(ct);
            var latest = batches.MaxBy(b => b.CreatedAt);
            return new HoldShapeCleanupStatus(latest?.Id, latest?.CreatedAt);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldShapeCleanupSummary> PreviewAsync(
        Guid wallId, CancellationToken ct = default, IProgress<HoldShapeCleanupProgress>? progress = null)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var (_, plan) = await PlanAsync(db, wallId, progress, ct);
            return plan.Summary(null);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldShapeCleanupSummary> ApplyAsync(
        Guid wallId,
        CancellationToken ct = default,
        string? expectedVersion = null,
        IProgress<HoldShapeCleanupProgress>? progress = null)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            using var wallLock = AcquireWallLock(wallId);
            var (holds, plan) = await PlanAsync(db, wallId, progress, ct);
            if (expectedVersion is not null && expectedVersion != plan.Version)
            {
                throw new UserFacingException("The holds changed since the preview. Check the hold shapes again, then apply.");
            }

            if (plan.Changes.Count == 0)
            {
                return plan.Summary(null);
            }

            foreach (var change in plan.Changes)
            {
                Write(holds[change.HoldId], change);
            }

            ct.ThrowIfCancellationRequested();
            var batchId = await SaveInBatchAsync(db, wallId, ct);
            await RefreshFootprintsAsync(wallId);

            logger.LogInformation(
                "Hold shape clean-up on wall {WallId} by {UserId}: {Changed} of {Shapes} outlines and {Circles} circles considered changed, journal batch {BatchId}",
                wallId, userId, plan.Changes.Count, plan.AutoShapes, plan.AutoCircles, batchId);
            return plan.Summary(batchId);
        }
    }

    /// <inheritdoc/>
    public async Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var known = await db.ChangeJournalBatches.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && b.Label == BatchLabel && b.ScopeId == wallId, ct);
            if (!known)
            {
                throw new UserFacingException("That shape clean-up was not found on this wall.");
            }
        }

        using var wallLock = AcquireWallLock(wallId);
        var result = await reverter.RevertBatchAsync(batchId, force: false, ct);
        if (result.Reverted)
        {
            await RefreshFootprintsAsync(wallId);
        }

        return result;
    }

    /// <summary>Saves inside a named batch and returns the id of exactly the batch that was opened for this write.</summary>
    private async Task<Guid> SaveInBatchAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        using var scope = journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
        var batchId = ((ChangeJournalBatchScope)scope).BatchId;
        await db.SaveChangesAsync(ct);
        return batchId;
    }

    /// <summary>Fails fast when any hold writer (another clean-up, an outline upgrade, a wall-update commit) is running.</summary>
    private static IDisposable AcquireWallLock(Guid wallId) =>
        WallHoldWriteLock.TryAcquire(wallId, "Another update of this wall's holds (a clean-up, an outline upgrade or a wall update) is running. Try again in a moment.");

    /// <summary>
    /// The 3D footprints derive from the outlines. This runs AFTER the holds were saved, so it ignores the caller's
    /// cancellation (a cancelled request must not report "nothing changed" over written holds) and a failure here
    /// is logged, not reported as a failed clean-up: the shapes are saved and the refresh can be run again.
    /// </summary>
    private async Task RefreshFootprintsAsync(Guid wallId)
    {
        if (footprints is null)
        {
            return;
        }

        try
        {
            await footprints.RefineFromPipelineAsync(wallId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The 3D footprint refresh after the shape clean-up on wall {WallId} failed; run \"refine 3D hold shapes\" again", wallId);
        }
    }

    /// <summary>Writes one change onto the tracked hold: geometry only, never position, never boulders.</summary>
    private static void Write(Hold hold, HoldShapeChange change)
    {
        hold.Radius = change.Radius;
        if (change.Shape is null)
        {
            if (hold.ShapePoints is not null)
            {
                hold.ShapePoints = null;
                hold.ShapeHoles = null;
                hold.OutlineSource = HoldOutlineSource.AutoCircle;
                hold.OutlineConfidence = null;
            }

            return;
        }

        hold.ShapePoints = change.Shape;
        hold.ShapeHoles = HoldShapeHoles.Inside(hold.ShapeHoles, change.Shape);
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, "Cleaning up hold shapes");
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
