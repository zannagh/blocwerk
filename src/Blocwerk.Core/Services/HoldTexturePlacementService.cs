using System.Collections.Concurrent;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldTexturePlacementService"/>: per live panel photo, one <see cref="IPhotoTextureMatcher"/>
/// session matched against every facet texture of the active model, each match judged by
/// <see cref="PhotoTextureRegistration"/>, each hold placed by <see cref="HoldTexturePlacer"/>, and the writes
/// recorded batch by batch on a <see cref="Entities.HoldPlacementRun"/>. Gate: <see cref="WallAdminGuard"/>
/// plus <see cref="KioskGuard"/>, like the outline upgrade next to it in the wall settings.
/// </summary>
/// <remarks>The matcher and the capture file store are optional: a host without them reports the action as off.</remarks>
public sealed partial class HoldTexturePlacementService : IHoldTexturePlacementService
{
    /// <summary>Holds written per SaveChanges (each batch also updates the run record, atomically).</summary>
    public const int BatchSize = 100;

    private const string KioskRefusal = "Placing holds on the 3D model";

    /// <summary>Walls with a run in progress in this process: a second run would only redo the same work.</summary>
    private static readonly ConcurrentDictionary<Guid, byte> Running = new();

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly ILogger<HoldTexturePlacementService> logger;
    private readonly IPhotoTextureMatcher? matcher;
    private readonly ICaptureFileStore? files;
    private readonly IHoldRefinementQueue? refinementQueue;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="HoldTexturePlacementService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="matcher">Photo ↔ texture feature matching; null means the action is unavailable.</param>
    /// <param name="files">Where the textures are stored; null means the action is unavailable.</param>
    /// <param name="refinementQueue">Refines the placed holds' footprints afterwards; optional.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public HoldTexturePlacementService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<HoldTexturePlacementService> logger,
        IPhotoTextureMatcher? matcher = null,
        ICaptureFileStore? files = null,
        IHoldRefinementQueue? refinementQueue = null,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.logger = logger;
        this.matcher = matcher;
        this.files = files;
        this.refinementQueue = refinementQueue;
        this.kioskContext = kioskContext;
    }

    private bool Enabled => matcher is not null && files is not null;

    /// <inheritdoc/>
    public async Task<HoldPlacementStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var hasTextures = await db.WallGeometryTextures.AnyAsync(t => t.GeometryModel.WallId == wallId && t.GeometryModel.IsActive, ct);

            // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
            var runs = await db.HoldPlacementRuns.AsNoTracking().Where(r => r.WallId == wallId).ToListAsync(ct);
            var latest = runs.MaxBy(r => r.CreatedAt);
            var info = latest is null
                ? null
                : new HoldPlacementRunInfo(
                    latest.Id, latest.CreatedAt, latest.Trigger, latest.PlacedCount, latest.SkippedCount, latest.FailedCount,
                    PanelSummaries.FromJson(latest.PanelsJson), latest.RevertedAt);
            return new HoldPlacementStatus(Enabled, hasTextures, info);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldPlacementResult> PlaceAsync(Guid wallId, string trigger = HoldPlacementTrigger.Admin, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            throw new UserFacingException("Placing holds on the 3D model is not available on this server.");
        }

        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            return await ExclusiveAsync(wallId, () => ExecuteAsync(db, wallId, userId, trigger, enqueue: true, ct))
                   ?? throw new UserFacingException("Holds are already being placed on this wall. Try again in a minute.");
        }
    }

    /// <inheritdoc/>
    public async Task<HoldPlacementResult?> PlaceFromPipelineAsync(Guid wallId, Guid modelId, Guid actingUserId, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = actingUserId;
        if (await UnplacedHoldIdsAsync(db, wallId, modelId, ct) is not { Count: > 0 } unplaced)
        {
            return null;
        }

        return await ExclusiveAsync(
            wallId, () => ExecuteAsync(db, wallId, actingUserId, HoldPlacementTrigger.Capture, enqueue: false, ct, unplaced));
    }

    /// <summary>
    /// The automatic run's holds: when the model is the wall's active one and has textures, the live panel holds this
    /// action may place (<see cref="HoldTexturePlacer.IsEligible"/>: never one placed by markers or an edit) that are
    /// not on the model yet — no facet, a facet the model does not have, or placed by texture registration on an
    /// earlier model. Null when the run does not apply.
    /// </summary>
    private async Task<HashSet<Guid>?> UnplacedHoldIdsAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var active = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => new { m.Id, m.Json }).FirstOrDefaultAsync(ct);
        if (active?.Id != modelId || !await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == modelId, ct))
        {
            return null;
        }

        var facets = Facets(active.Json).Extents.Keys.ToHashSet(StringComparer.Ordinal);
        var live = await (await LiveHoldsQueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var eligible = live.Where(HoldTexturePlacer.IsEligible).ToList();
        var onThisModel = await PlacedOnModelAsync(db, wallId, modelId, ct);
        var unplaced = eligible
            .Where(h => h.FacetId is null || h.PlaneAMm is null || h.PlaneBMm is null || !facets.Contains(h.FacetId))
            .Select(h => h.Id)
            .ToHashSet();

        // A new model keeps the facet ids but its planes moved (a re-solve), so what an earlier run placed by
        // texture registration on an OLDER model is placed again on this one's textures. Marker-placed and
        // edited holds are not eligible at all; a hold a run on this model already placed is not redone.
        var replaced = eligible
            .Where(h => h.MetricSource == HoldMetric.TextureRegistration && !unplaced.Contains(h.Id) && !onThisModel.Contains(h.Id))
            .Select(h => h.Id)
            .ToList();
        unplaced.UnionWith(replaced);
        logger.LogInformation(
            "Wall {WallId}: model {ModelId} is live; {Live} live holds, {Eligible} placeable here, {Unplaced} to place "
            + "({Replaced} placed on an earlier model, placed again)",
            wallId, modelId, live.Count, eligible.Count, unplaced.Count, replaced.Count);
        return unplaced;
    }

    /// <summary>The holds a run on <paramref name="modelId"/> placed and that was not reverted.</summary>
    private static async Task<HashSet<Guid>> PlacedOnModelAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var runs = await db.HoldPlacementRuns.AsNoTracking()
            .Where(r => r.WallId == wallId && r.GeometryModelId == modelId && r.RevertedAt == null)
            .Select(r => r.HoldsJson)
            .ToListAsync(ct);
        return runs.SelectMany(HoldPlacementEntry.FromJson).Select(e => e.HoldId).ToHashSet();
    }

    /// <summary>Runs <paramref name="action"/> unless the wall already has a run in progress (then null).</summary>
    private static async Task<HoldPlacementResult?> ExclusiveAsync(Guid wallId, Func<Task<HoldPlacementResult>> action)
    {
        if (!Running.TryAdd(wallId, 0))
        {
            return null;
        }

        try
        {
            return await action();
        }
        finally
        {
            Running.TryRemove(wallId, out _);
        }
    }

    /// <summary>Opens a context for a wall admin, refused outright from any kiosk tablet.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
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
