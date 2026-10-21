using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
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
    private readonly IHoldRefinementQueue? refinementQueue;

    /// <summary>Initializes a new instance of the <see cref="HoldShapeCleanupService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="journal">Opens the named journal batch.</param>
    /// <param name="reverter">Reverts a batch.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    /// <param name="refinementQueue">Refreshes the 3D footprints of the changed holds in the background; optional.</param>
    public HoldShapeCleanupService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IChangeJournal journal,
        ChangeJournalReverter reverter,
        ILogger<HoldShapeCleanupService> logger,
        IKioskContext? kioskContext = null,
        IHoldRefinementQueue? refinementQueue = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.journal = journal;
        this.reverter = reverter;
        this.logger = logger;
        this.kioskContext = kioskContext;
        this.refinementQueue = refinementQueue;
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
            HoldShapeCleanupPlan plan;
            Guid batchId;
            using (AcquireWallLock(wallId))
            {
                var (holds, planned) = await PlanAsync(db, wallId, progress, ct);
                plan = planned;
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
                batchId = await SaveInBatchAsync(db, wallId, ct);
            }

            logger.LogInformation(
                "Hold shape clean-up on wall {WallId} by {UserId}: {Changed} of {Shapes} outlines and {Circles} circles considered changed, journal batch {BatchId}",
                wallId, userId, plan.Changes.Count, plan.AutoShapes, plan.AutoCircles, batchId);
            QueueFootprints(wallId, plan.Changes.Select(c => c.HoldId));
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

        var touched = await TouchedHoldsAsync(batchId, ct);
        ChangeJournalRevertResult result;
        using (AcquireWallLock(wallId))
        {
            result = await reverter.RevertBatchAsync(batchId, force: false, ct);
        }

        if (result.Reverted)
        {
            QueueFootprints(wallId, touched);
        }

        return result;
    }

    /// <summary>Saves inside a named batch and returns the id of exactly the batch that was opened for this write.</summary>
    private async Task<Guid> SaveInBatchAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        using var scope = journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
        using var feedScope = FeedScope.Begin(wallId, BatchLabel);
        var batchId = ((ChangeJournalBatchScope)scope).BatchId;
        await db.SaveChangesAsync(ct);
        return batchId;
    }

    /// <summary>Fails fast when any hold writer (another clean-up, an outline upgrade, a wall-update commit) is running.</summary>
    private static IDisposable AcquireWallLock(Guid wallId) =>
        WallHoldWriteLock.TryAcquire(wallId, "Another update of this wall's holds (a clean-up, an outline upgrade or a wall update) is running. Try again in a moment.");

    /// <summary>
    /// The 3D footprints derive from the outlines. Only the changed holds are refined, in the background and outside the
    /// wall's hold lock (<see cref="IHoldRefinementQueue"/>): a refinement of the whole wall traces every hold in every
    /// capture photo, which takes many minutes, and running it inline kept the request and the lock busy all that time.
    /// Until it ran, the 3D view projects the new outlines.
    /// </summary>
    private void QueueFootprints(Guid wallId, IEnumerable<Guid> holdIds) => refinementQueue?.Enqueue(wallId, holdIds);

    /// <summary>The holds a recorded batch changed, read from its journal entries.</summary>
    private async Task<List<Guid>> TouchedHoldsAsync(Guid batchId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var keys = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => e.BatchId == batchId && e.EntityType == nameof(Hold))
            .Select(e => e.KeyJson)
            .ToListAsync(ct);
        return keys.Select(HoldIdOf).OfType<Guid>().ToList();
    }

    private static Guid? HoldIdOf(string keyJson)
    {
        using var key = JsonDocument.Parse(keyJson);
        return key.RootElement.ValueKind == JsonValueKind.Object
               && key.RootElement.TryGetProperty(nameof(Hold.Id), out var id)
               && id.TryGetGuid(out var guid)
            ? guid
            : null;
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
