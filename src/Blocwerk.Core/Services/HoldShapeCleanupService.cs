using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldShapeCleanupService"/>. Pure geometry lives in <see cref="HoldShapeCleanup"/>; this class
/// only loads each live photo's holds, applies the planned changes and saves them in one named journal batch.
/// It needs no outliner and no photo decoding, so it runs on any host.
/// </summary>
public sealed class HoldShapeCleanupService : IHoldShapeCleanupService
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
    public async Task<HoldShapeCleanupSummary> PreviewAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            return (await PlanAsync(db, wallId, ct)).Summary(null);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldShapeCleanupSummary> ApplyAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var plan = await PlanAsync(db, wallId, ct);
            if (plan.Changes.Count == 0)
            {
                return plan.Summary(null);
            }

            foreach (var (hold, change) in plan.Changes)
            {
                Write(hold, change);
            }

            using (journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId))
            {
                await db.SaveChangesAsync(ct);
            }

            var batches = await db.ChangeJournalBatches.AsNoTracking()
                .Where(b => b.Label == BatchLabel && b.ScopeId == wallId)
                .ToListAsync(ct);
            var batchId = batches.MaxBy(b => b.CreatedAt)?.Id;
            if (footprints is not null)
            {
                // The 3D footprints derive from the outlines: refresh them (a batch step, as after the circle upgrade).
                await footprints.RefineFromPipelineAsync(wallId, ct);
            }

            logger.LogInformation(
                "Hold shape clean-up on wall {WallId} by {UserId}: {Changed} of {Shapes} outlines changed, journal batch {BatchId}",
                wallId, userId, plan.Changes.Count, plan.AutoShapes, batchId);
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

        var result = await reverter.RevertBatchAsync(batchId, force: false, ct);
        if (result.Reverted && footprints is not null)
        {
            await footprints.RefineFromPipelineAsync(wallId, ct);
        }

        return result;
    }

    /// <summary>Writes one change onto the tracked hold: geometry only, never position, never boulders.</summary>
    private static void Write(Hold hold, HoldShapeChange change)
    {
        hold.Radius = change.Radius;
        if (change.Shape is null)
        {
            hold.ShapePoints = null;
            hold.ShapeHoles = null;
            hold.OutlineSource = HoldOutlineSource.AutoCircle;
            hold.OutlineConfidence = null;
            return;
        }

        hold.ShapePoints = change.Shape;
        if (change.Kind != HoldShapeChangeKind.Smoothed)
        {
            hold.ShapeHoles = null;
        }
    }

    private async Task<HoldShapeCleanupPlan> PlanAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var photos = await HoldOutlineUpgradeService.LoadLivePhotosAsync(db, wallId, ct);
        var plan = new HoldShapeCleanupPlan(photos.Count);
        foreach (var photo in photos)
        {
            var holds = await HoldOutlineUpgradeService.LiveHolds(db, wallId, photo).ToListAsync(ct);
            var byId = holds.ToDictionary(h => h.Id);
            plan.AutoShapes += holds.Count(HoldShapeCleanup.IsCleanable);
            plan.Locked += holds.Count(h => !HoldShapeCleanup.IsCleanable(h));
            var aspect = await PhotoAspectAsync(db, wallId, photo, ct);
            foreach (var change in HoldShapeCleanup.Plan(holds, aspect))
            {
                plan.Changes.Add((byId[change.HoldId], change));
            }
        }

        return plan;
    }

    /// <summary>Width / height of the photo the holds sit on (header read only); 1 when it cannot be read.</summary>
    private static async Task<double> PhotoAspectAsync(BlocwerkDbContext db, Guid wallId, OutlineUpgradePhoto photo, CancellationToken ct)
    {
        var key = new Wall3DPhotoKey(photo.PanelId, photo.Generation);
        var infos = await PanelPhotoInfoLoader.LoadAsync(db, wallId, [key], ct);
        return infos.TryGetValue(key, out var info) && info.Height > 0 ? (double)info.Width / info.Height : 1;
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
