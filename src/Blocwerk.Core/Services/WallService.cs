using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public class WallService : IWallService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHoldDetectionService _holdDetectionService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<WallService> _logger;
    private readonly IKioskContext? _kioskContext;
    private readonly IPushNotificationService? _pushNotificationService;

    /// <summary>Creates the service.</summary>
    /// <remarks>
    /// <c>kioskContext</c> is optional: hosts without an HTTP layer (tests, tooling) never register
    /// one, which simply means "never a kiosk". The stamped
    /// <see cref="BlocwerkDbContext.KioskWallId"/> is the second source the guards read, so a
    /// missing context is not a missing restriction — see <see cref="KioskGuard"/>.
    /// </remarks>
    public WallService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IActivityLogService activityLogService,
        ILogger<WallService> logger,
        IKioskContext? kioskContext = null,
        IPushNotificationService? pushNotificationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _holdDetectionService = holdDetectionService;
        _activityLogService = activityLogService;
        _logger = logger;
        _kioskContext = kioskContext;
        _pushNotificationService = pushNotificationService;
    }

    public async Task<Wall> CreateWallAsync(string name, string? description, int angle = 0)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Create");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            // A tablet is registered to exactly one wall and can never be registered to a wall
            // created after the fact. Creating one from the kiosk only ever produces a wall the
            // acting user owns and cannot see from here — and it is a write outside the kiosk's wall.
            KioskGuard.EnsureNotKiosk(_kioskContext, db, "Creating a wall");

            var wall = new Wall
            {
                Name = name,
                Description = description,
                OwnerId = user.Id,
                Angle = angle,
            };

            db.Walls.Add(wall);

            db.WallMembers.Add(new WallMember
            {
                UserId = user.Id,
                WallId = wall.Id,
                Role = WallRole.Admin,
            });

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordWallCreated(wall.Id);
            _logger.LogInformation("Wall {WallId} created by {UserId}", wall.Id, user.Id);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// The id to filter a READ of <paramref name="wallId"/> by: the signed-in user, or
    /// <see cref="Guid.Empty"/> for the one anonymous caller that is allowed to look — a registered
    /// kiosk tablet, on its own wall, with nobody picked yet.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> opens the membership half of the wall query filter, exactly as a
    /// share-token read does. It does NOT open the kiosk half: every context this service creates is
    /// stamped with the tablet's wall by <c>KioskScopedDbContextFactory</c>, so the read stays pinned
    /// to that one wall whatever id is passed in here. Any other anonymous caller still throws, and
    /// the page above still sends them to sign in.
    /// </remarks>
    private async Task<Guid> ResolveViewerIdAsync(Guid wallId)
    {
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            return user.Id;
        }
        catch (UnauthorizedAccessException) when (KioskViewing.AllowsAnonymousViewOf(_kioskContext, wallId))
        {
            return Guid.Empty;
        }
    }

    public async Task<Wall?> GetWallAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Get", wallId);
        try
        {
            var viewerId = await ResolveViewerIdAsync(wallId);
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;
            // Project just the column (async, no blob load, no tracking) rather than materialising
            // the whole Wall entity just to read the generation.
            var currentGeneration = await db.Walls
                .Where(wl => wl.Id == wallId)
                .Select(wl => wl.CurrentGeneration)
                .FirstOrDefaultAsync();

            // Live holds are a PER-PANEL fact, not a per-wall one: after a subset (per-panel) promote the
            // wall generation bumps but the panels left untouched — and their holds — stay at the old
            // generation, so a bare "== CurrentGeneration" window drops them and the schematic/border
            // views lose those holds. The live set is the holds parented to the LATEST live panel per
            // position (spanning generations after a subset promote), plus the legacy centre-photo holds
            // (null panel, live at CurrentGeneration), plus the in-flight staged holds (CurrentGeneration
            // + 1) the review overlay needs. Superseded old panel rows are excluded by the live-panel set.
            var livePanelIds = await LoadLivePanelIdsAsync(db, wallId);

            var wall = await db.Walls
                .AsSplitQuery()
                .Include(w => w.Members)
                .Include(w => w.Holds
                    .Where(h
                        => (h.WallPanelId != null && livePanelIds.Contains(h.WallPanelId.Value))
                            || (h.WallPanelId == null && h.Generation == currentGeneration)
                            || h.Generation == currentGeneration + 1))
                .Include(w => w.Boulders.Where(b => !b.IsArchived)).ThenInclude(b => b.CreatedBy)
                .Include(w => w.Boulders).ThenInclude(b => b.BoulderHolds)
                .FirstOrDefaultAsync(w => w.Id == wallId);

            if (wall != null)
            {
                wall.Photo = null;
                wall.StagedPhoto = null;
            }

            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall?> GetWallByShareTokenAsync(string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetByShareToken");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            var wall = await db.Walls
                .AsSplitQuery()
                .Include(w => w.Members)
                .Include(w => w.Holds)
                .Include(w => w.Boulders.Where(b => !b.IsArchived)).ThenInclude(b => b.CreatedBy)
                .Include(w => w.Boulders).ThenInclude(b => b.BoulderHolds)
                .FirstOrDefaultAsync(w => w.ShareToken == shareToken);

            if (wall != null)
            {
                // Live holds only for a share viewer — no in-flight staged rows. As in GetWallAsync the
                // live set spans generations after a subset promote, so filter by the latest live panel
                // per position (plus legacy null-panel holds at the current generation) rather than a
                // bare "== CurrentGeneration", which would drop an un-updated panel's holds.
                var livePanelIds = await LoadLivePanelIdsAsync(db, wall.Id);
                wall.Holds = wall.Holds
                    .Where(h => (h.WallPanelId is { } pid && livePanelIds.Contains(pid))
                        || (h.WallPanelId is null && h.Generation == wall.CurrentGeneration))
                    .ToList();
                wall.Photo = null;
            }

            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Dictionary<Guid, List<string>>> GetBoulderSetterNamesAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetBoulderSetterNames", wallId);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            return await BoulderSetterNames.LoadForWallAsync(db, wallId);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Wall>> GetMyWallsAsync()
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetMyWalls");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Single collection include (Members) so AsNoTracking is safe here — no reliance on
            // identity resolution the way GetWallAsync has (it includes Boulders twice).
            var walls = await db.Walls
                .AsNoTracking()
                .Include(w => w.Members)
                .ToListAsync();

            foreach (var w in walls)
            {
                w.Photo = null;
            }

            return walls;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description, int? angle = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Update", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for update by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            wall.Name = name;
            wall.Description = description;
            if (angle.HasValue)
            {
                wall.Angle = angle.Value;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} updated by {UserId}", wall.Id, user.Id);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteWallAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Delete", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId && w.OwnerId == user.Id);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found or {UserId} is not owner for delete", wallId, user.Id);
                throw new InvalidOperationException("Wall not found or not owner");
            }

            db.Walls.Remove(wall);
            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} deleted by {UserId}", wallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType, bool autoDetect = true)
    {
        // The upload is stored byte-for-byte: hold detection below, and every later alignment or
        // re-detection pass, must see the camera's full resolution. Browsers are served downscaled
        // variants derived from this original instead (see IImageVariantCache).
        using var op = BlocwerkMetrics.TimeOperation("Wall.UploadPhoto", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for photo upload by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            wall.Photo = photo;
            wall.PhotoContentType = contentType;

            if (!autoDetect)
            {
                await EnsureCenterPanelAsync(db, wall);
                await db.SaveChangesAsync();
                await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoUploaded);
                _logger.LogInformation("Photo uploaded to wall {WallId} by {UserId} without auto-detection", wallId, user.Id);
                return wall;
            }

            var detectedHolds = await _holdDetectionService.DetectHoldsAsync(photo);
            foreach (var detected in detectedHolds)
            {
                db.Holds.Add(new Hold
                {
                    WallId = wallId,
                    X = detected.X,
                    Y = detected.Y,
                    Radius = detected.Radius,
                    Color = detected.Color,
                    Confidence = detected.Confidence,
                    IsAutoDetected = true,
                    Generation = wall.CurrentGeneration,
                });
            }

            await EnsureCenterPanelAsync(db, wall);
            await db.SaveChangesAsync();
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoUploaded, $"{detectedHolds.Count} holds detected");
            _logger.LogInformation("Photo uploaded to wall {WallId} by {UserId} with {DetectedHoldCount} holds detected", wallId, user.Id, detectedHolds.Count);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Hold> MarkHoldModifiedAsync(Guid holdId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.MarkHoldModified");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                _logger.LogWarning("Hold {HoldId} not found for mark-modified by {UserId}", holdId, user.Id);
                throw new InvalidOperationException("Hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, hold.WallId, user.Id, CancellationToken.None);

            hold.NeedsReview = true;

            var affectedBoulders = await db.BoulderHolds
                .Where(bh => bh.HoldId == holdId)
                .Select(bh => bh.Boulder)
                .Where(b => !b.IsArchived)
                .ToListAsync();

            foreach (var b in affectedBoulders)
            {
                b.NeedsReview = true;
            }

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "modified");
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldMarkedModified,
                $"{affectedBoulders.Count} boulder(s) flagged for review");
            _logger.LogInformation("Hold {HoldId} on wall {WallId} marked modified by {UserId}, {ReviewCount} boulder(s) flagged for review", holdId, hold.WallId, user.Id, affectedBoulders.Count);
            return hold;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<int> RestoreBouldersForUnchangedHoldAsync(Guid holdId, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.RestoreBouldersForUnchangedHold");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId, ct);
            if (hold == null)
            {
                _logger.LogWarning("Hold {HoldId} not found for restore-unchanged by {UserId}", holdId, user.Id);
                throw new InvalidOperationException("Hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, hold.WallId, user.Id, ct);

            // Marking this hold unchanged confirms it did not move. On a big wall the same physical hold
            // also appears on more peripheral panels as linked twins, and this (more central) panel is
            // ground truth for them — settle those twins too so the verdict propagates from the centre
            // outward. clearedHolds = this hold plus those twins.
            var peripheralTwinIds = await GetPeripheralTwinIdsAsync(db, hold);
            var confirmedUnchanged = new HashSet<Guid>(peripheralTwinIds) { holdId };

            var clearedHolds = await db.Holds
                .Where(h => confirmedUnchanged.Contains(h.Id))
                .ToListAsync(ct);
            foreach (var cleared in clearedHolds)
            {
                cleared.NeedsReview = false;
            }

            // Candidate boulders: historic ones that reference this hold or any of its settled twins.
            var candidates = await db.Boulders
                .Include(b => b.BoulderHolds)
                .Where(b => b.IsHistoric && b.BoulderHolds.Any(bh => confirmedUnchanged.Contains(bh.HoldId)))
                .ToListAsync(ct);

            // Every hold still present on this wall — used to test each boulder's completeness.
            var existingHoldIds = (await db.Holds
                .Where(h => h.WallId == hold.WallId)
                .Select(h => h.Id)
                .ToListAsync(ct))
                .ToHashSet();

            // Holds on this wall STILL flagged as modified, excluding the ones we just settled (the DB
            // query can't see those unsaved changes yet). A boulder referencing any of these genuinely
            // changed on some OTHER hold; restoring it would present stale geometry as current, so it
            // must stay historic even though these holds are unchanged.
            var stillModifiedHoldIds = (await db.Holds
                .Where(h => h.WallId == hold.WallId && h.NeedsReview && !confirmedUnchanged.Contains(h.Id))
                .Select(h => h.Id)
                .ToListAsync(ct))
                .ToHashSet();

            var restored = 0;
            var skipped = 0;
            foreach (var boulder in candidates)
            {
                // Restore only when EVERY hold the boulder references still exists AND none of them is
                // still flagged modified — i.e. all of the boulder's holds are confirmed unchanged.
                var allHoldsExist = boulder.BoulderHolds.All(bh => existingHoldIds.Contains(bh.HoldId));
                var anyStillModified = boulder.BoulderHolds.Any(bh => stillModifiedHoldIds.Contains(bh.HoldId));
                if (allHoldsExist && !anyStillModified)
                {
                    boulder.IsHistoric = false;
                    boulder.NeedsReview = false;
                    restored++;
                }
                else
                {
                    skipped++;
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Hold {HoldId} on wall {WallId} marked unchanged by {UserId}: {Restored} boulder(s) restored, {Skipped} skipped; {Twins} peripheral twin(s) settled",
                holdId, hold.WallId, user.Id, restored, skipped, peripheralTwinIds.Count);
            return restored;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task MergeVirtualHoldAsync(Guid virtualHoldId, Guid actualHoldId, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.MergeVirtualHold");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            if (virtualHoldId == actualHoldId)
            {
                throw new InvalidOperationException("A hold cannot be merged into itself");
            }

            var virtualHold = await db.Holds.FirstOrDefaultAsync(h => h.Id == virtualHoldId, ct);
            if (virtualHold == null)
            {
                _logger.LogWarning("Virtual hold {VirtualHoldId} not found for make-actual merge by {UserId}", virtualHoldId, user.Id);
                throw new InvalidOperationException("Virtual hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, virtualHold.WallId, user.Id, ct);

            if (!virtualHold.IsVirtual)
            {
                throw new InvalidOperationException("Selected hold is not virtual");
            }

            var actualHold = await db.Holds.FirstOrDefaultAsync(h => h.Id == actualHoldId, ct);
            if (actualHold == null)
            {
                _logger.LogWarning("Target hold {ActualHoldId} not found for make-actual merge by {UserId}", actualHoldId, user.Id);
                throw new InvalidOperationException("Target hold not found");
            }

            if (virtualHold.WallId != actualHold.WallId)
            {
                throw new InvalidOperationException("Holds belong to different walls");
            }

            // The virtual hold is the survivor: boulders already point at it, so keeping its
            // Id preserves every BoulderHold link. Adopt the detected hold's geometry and look.
            virtualHold.X = actualHold.X;
            virtualHold.Y = actualHold.Y;
            virtualHold.Radius = actualHold.Radius;
            virtualHold.ShapePoints = actualHold.ShapePoints?
                .Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy })
                .ToList();
            if (!string.IsNullOrEmpty(actualHold.Color))
            {
                virtualHold.Color = actualHold.Color;
            }

            virtualHold.Material = actualHold.Material;
            virtualHold.Category = actualHold.Category;
            virtualHold.IsAutoDetected = actualHold.IsAutoDetected;
            virtualHold.Confidence = actualHold.Confidence;
            virtualHold.IsVirtual = false;
            virtualHold.NeedsReview = true;

            // Geometry moved, so the panel the geometry belongs to must move with it.
            await AdoptMergedPanelStampAsync(db, virtualHold, actualHold, ct);

            // Re-point any BoulderHold rows off the consumed actual hold onto the survivor so no
            // boulder loses a hold. Detected holds normally have none, but handle it correctly.
            // HoldId is part of the composite key and can't be mutated in place, so we drop the
            // old row and add an equivalent on the survivor, deduped against its existing links.
            var actualLinks = await db.BoulderHolds.Where(bh => bh.HoldId == actualHoldId).ToListAsync(ct);
            var survivorBoulderIds = (await db.BoulderHolds
                    .Where(bh => bh.HoldId == virtualHoldId)
                    .Select(bh => bh.BoulderId)
                    .ToListAsync(ct))
                .ToHashSet();
            foreach (var link in actualLinks)
            {
                if (survivorBoulderIds.Add(link.BoulderId))
                {
                    db.BoulderHolds.Add(new BoulderHold
                    {
                        BoulderId = link.BoulderId,
                        HoldId = virtualHoldId,
                        Type = link.Type,
                        Usage = link.Usage,
                    });
                }

                db.BoulderHolds.Remove(link);
            }

            // The detected hold may carry HoldLink rows (Restrict FK on both hold ends), which would
            // reject the delete below. They are alignment-graph artifacts, safe to drop on a merge.
            var actualHoldLinks = await db.HoldLinks
                .Where(l => l.HoldAId == actualHoldId || l.HoldBId == actualHoldId)
                .ToListAsync(ct);
            db.HoldLinks.RemoveRange(actualHoldLinks);

            db.Holds.Remove(actualHold);

            await db.SaveChangesAsync(ct);
            BlocwerkMetrics.RecordHoldUpdated(virtualHold.WallId, "merged");
            await _activityLogService.LogAsync(virtualHold.WallId, null, ActivityType.HoldMerged,
                "virtual hold merged into a detected hold");
            _logger.LogInformation("Virtual hold {VirtualHoldId} merged into actual hold {ActualHoldId} on wall {WallId} by {UserId}", virtualHoldId, actualHoldId, virtualHold.WallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task PromoteVirtualHoldAsync(Guid virtualHoldId, CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.PromoteVirtualHold");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = user.Id;

            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == virtualHoldId, ct);
            if (hold == null)
            {
                _logger.LogWarning("Virtual hold {VirtualHoldId} not found for promote by {UserId}", virtualHoldId, user.Id);
                throw new InvalidOperationException("Virtual hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, hold.WallId, user.Id, ct);

            if (!hold.IsVirtual)
            {
                throw new InvalidOperationException("Selected hold is not virtual");
            }

            // Promote in place: the hold keeps its Id, geometry and boulder links untouched.
            hold.IsVirtual = false;

            // Virtual holds created before AddHoldAsync stamped panels carry no panel id; their
            // coordinates are the center photo's, which is exactly where the viewer draws them. Make
            // that explicit now the hold is a real one so the panel-keyed reads can see it too. A hold
            // that already has a panel keeps it, and Generation is left alone (see the helper).
            if (hold.WallPanelId is null)
            {
                hold.WallPanelId = await ResolveCenterPanelStampAsync(db, hold.WallId, ct);
            }

            await db.SaveChangesAsync(ct);
            BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "modified");
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldMarkedModified,
                "virtual hold promoted to actual");
            _logger.LogInformation("Virtual hold {VirtualHoldId} promoted to actual on wall {WallId} by {UserId}", virtualHoldId, hold.WallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<string> GenerateShareTokenAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GenerateShareToken", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // A share link is the one thing a wall admin can mint that OUTLIVES the kiosk session:
            // it is redeemed later, from any browser, by anyone who read it off the tablet, and it
            // inserts a permanent WallMember that survives the 30-minute window, the PIN and
            // revoking the kiosk key. So it is refused for every kiosk session — including one
            // acting as a genuine admin of this very wall, which is why the wall guard below is not
            // enough on its own.
            KioskGuard.EnsureNotKiosk(_kioskContext, db, "Generating a share link");

            // Owner-aware admin check: a bare-owner (no explicit Admin member row) counts as admin,
            // matching the client _isAdmin gate. Everyone else is rejected exactly as before.
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                           .Include(w => w.Members)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for share token generation by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            wall.ShareToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            await db.SaveChangesAsync();
            _logger.LogInformation("Share token generated for wall {WallId} by {UserId}", wallId, user.Id);
            return wall.ShareToken;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<string> GetOrCreateShareTokenAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetOrCreateShareToken", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // A share link outlives any session, so kiosks never mint one — same reasoning as
            // GenerateShareTokenAsync. Below this, the member gate is enough: unlike regeneration,
            // handing out the existing link is something any member of the wall may do.
            KioskGuard.EnsureNotKiosk(_kioskContext, db, "Sharing an invite link");

            // IgnoreQueryFilters so a bare-owner (no member row) can still be found: the Wall filter
            // gates on membership only, not ownership, so a filtered read would hide a legacy owner's
            // own wall. Authorization is enforced explicitly just below, matching the owner branches of
            // WallAdminGuard.
            var wall = await db.Walls.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for share token lookup by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            // Member gate with an owner fallback: any WallMember may share the link, but so may the
            // wall owner even on a legacy wall where they have no explicit member row (mirrors the
            // owner-aware admin check in GenerateShareTokenAsync — an owner is never denied their own
            // wall's existing link).
            var isMember = await db.WallMembers.AnyAsync(m => m.WallId == wallId && m.UserId == user.Id);
            if (!isMember && wall.OwnerId != user.Id)
            {
                throw new UnauthorizedAccessException($"User {user.Id} is not a member of wall {wallId}.");
            }

            // Non-destructive: reuse an existing link so members do not silently invalidate one
            // another's invites. Only mint (and save) when the wall has never had a token.
            if (!string.IsNullOrEmpty(wall.ShareToken))
            {
                return wall.ShareToken;
            }

            wall.ShareToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            await db.SaveChangesAsync();
            _logger.LogInformation("Share token minted for wall {WallId} by member {UserId}", wallId, user.Id);
            return wall.ShareToken;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall> JoinWallAsync(string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Join");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            // The redeeming half of the same escalation: joining writes a permanent WallMember for
            // the acting user, and this query deliberately runs with the membership gate disabled so
            // a token alone is enough. From a public tablet that is somebody else's account being
            // enrolled into a wall they never chose.
            KioskGuard.EnsureNotKiosk(_kioskContext, db, "Joining a wall from a share link");

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.ShareToken == shareToken);
            if (wall == null)
            {
                _logger.LogWarning("Invalid share token used to join by {UserId}", user.Id);
                throw new InvalidOperationException("Invalid share token");
            }

            var existingMembership = await db.WallMembers
                .FirstOrDefaultAsync(wm => wm.WallId == wall.Id && wm.UserId == user.Id);

            if (existingMembership == null)
            {
                db.WallMembers.Add(new WallMember
                {
                    UserId = user.Id,
                    WallId = wall.Id,
                    Role = WallRole.Member,
                });
                await db.SaveChangesAsync();
                BlocwerkMetrics.RecordMemberJoined(wall.Id);
                await _activityLogService.LogAsync(wall.Id, null, ActivityType.MemberJoined);
                _logger.LogInformation("User {UserId} joined wall {WallId}", user.Id, wall.Id);

                // Only on the path that actually inserted a new membership (the already-a-member case
                // skips this block). Guarded internally, so it never breaks or blocks the join.
                if (_pushNotificationService is not null)
                {
                    await _pushNotificationService.NotifyMemberJoinedAsync(wall.Id, user.Id);
                }
            }

            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<byte[]?> GetPhotoAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhoto", wallId);
        try
        {
            // The photo IS the wall page. Without the same anonymous-kiosk allowance the page below
            // renders and every <img> 500s, which on a tablet looks exactly like the wall being
            // broken. This is a separate HTTP request, so the kiosk context is primed by the kiosk
            // middleware from the device cookie, not from a circuit.
            var viewerId = await ResolveViewerIdAsync(wallId);
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            var wall = await db.Walls
                .AsNoTracking()
                .Where(w => w.Id == wallId)
                .Select(w => new { w.Photo })
                .FirstOrDefaultAsync();

            return wall?.Photo;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhotoByShareToken", wallId);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            var wall = await db.Walls
                .AsNoTracking()
                .Where(w => w.Id == wallId && w.ShareToken == shareToken)
                .Select(w => new { w.Photo })
                .FirstOrDefaultAsync();

            return wall?.Photo;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<WallPhotoTag?> GetPhotoTagAsync(Guid wallId, string? shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhotoTag", wallId);
        try
        {
            // Same two gates as GetPhotoAsync/GetPhotoByShareTokenAsync, in the same order: a share
            // token reads anonymously, otherwise the membership filter (with the anonymous-kiosk
            // allowance) decides. Only the projection differs — metadata instead of the bytes.
            var viewerId = string.IsNullOrEmpty(shareToken) ? await ResolveViewerIdAsync(wallId) : Guid.Empty;
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            var wall = await AccessibleWall(db, wallId, shareToken)
                .AsNoTracking()
                .Where(w => w.Photo != null)
                .Select(w => new { Length = w.Photo!.Length, w.PhotoContentType, w.CurrentGeneration })
                .FirstOrDefaultAsync();

            return wall is null
                ? null
                : new WallPhotoTag(wall.Length, wall.PhotoContentType, wall.CurrentGeneration, IsArchived: false);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<Hold>> GetHoldsForGenerationAsync(Guid wallId, int generation)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetHoldsForGeneration", wallId);
        try
        {
            // Same anonymous-kiosk allowance as GetWallAsync: a boulder set on an older generation
            // renders its schematic from that generation's holds, and the tablet must be able to
            // open such a boulder. The membership-filtered wall lookup below is what enforces
            // access, and it stays pinned to the kiosk's own wall.
            var viewerId = await ResolveViewerIdAsync(wallId);
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            // Holds carry no query filter of their own, so the membership-filtered wall
            // lookup is what enforces access here.
            if (!await db.Walls.AnyAsync(w => w.Id == wallId))
            {
                throw new InvalidOperationException("Wall not found");
            }

            return await db.Holds
                .AsNoTracking()
                .Where(h => h.WallId == wallId && h.Generation == generation)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<WallPhoto?> GetPhotoForGenerationAsync(Guid wallId, int generation)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhotoForGeneration", wallId);
        try
        {
            // As GetPhotoAsync: a historic boulder opened on the tablet renders against the photo of
            // the generation it was set on.
            var viewerId = await ResolveViewerIdAsync(wallId);
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            return await ResolveGenerationPhotoAsync(db, db.Walls.Where(w => w.Id == wallId), wallId, generation);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<WallPhoto?> GetPhotoForGenerationByShareTokenAsync(Guid wallId, string shareToken, int generation)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhotoForGenerationByShareToken", wallId);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            return await ResolveGenerationPhotoAsync(
                db,
                db.Walls.Where(w => w.Id == wallId && w.ShareToken == shareToken),
                wallId,
                generation);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<WallPhotoTag?> GetPhotoTagForGenerationAsync(Guid wallId, string? shareToken, int generation)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetPhotoTagForGeneration", wallId);
        try
        {
            var viewerId = string.IsNullOrEmpty(shareToken) ? await ResolveViewerIdAsync(wallId) : Guid.Empty;
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = viewerId;

            // Mirrors ResolveGenerationPhotoAsync's fallback exactly (current-or-newer generation
            // resolves to the live photo, older to the archived reset row) so the tag always
            // describes the bytes the byte route would return.
            var wall = await AccessibleWall(db, wallId, shareToken)
                .AsNoTracking()
                .Select(w => new
                {
                    Length = w.Photo == null ? 0 : w.Photo.Length,
                    w.PhotoContentType,
                    w.CurrentGeneration,
                })
                .FirstOrDefaultAsync();

            if (wall is null)
            {
                return null;
            }

            if (generation >= wall.CurrentGeneration)
            {
                return wall.Length == 0
                    ? null
                    : new WallPhotoTag(wall.Length, wall.PhotoContentType, wall.CurrentGeneration, IsArchived: false);
            }

            var reset = await db.WallResets
                .AsNoTracking()
                .Where(r => r.WallId == wallId && r.Generation == generation && r.PreviousPhoto != null)
                .Select(r => new { Length = r.PreviousPhoto!.Length, r.PreviousPhotoContentType })
                .FirstOrDefaultAsync();

            // A retired generation is content-addressed by its number and is never rewritten, so the
            // browser may hold it without revalidating.
            return reset is null
                ? null
                : new WallPhotoTag(reset.Length, reset.PreviousPhotoContentType, generation, IsArchived: true);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false, HoldMaterial? material = null, HoldHandType? handType = null, Guid? wallPanelId = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.AddHold", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Placing an ACTUAL hold is an editor capability (owner/admin/moderator). Placing a
            // VIRTUAL placeholder hold is a plain member capability used while building a boulder, so
            // it is only gated by wall membership (already enforced by the wall query filter). A
            // virtual hold can become actual only via the editor-gated PromoteVirtualHoldAsync, so
            // exempting virtual creation here grants no editor privilege.
            if (!isVirtual)
            {
                await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);
            }

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for add hold by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            var targetGen = wall.StagedAt != null ? wall.CurrentGeneration + 1 : wall.CurrentGeneration;
            var stamp = await ResolvePanelStampAsync(db, wall, wallPanelId);
            if (stamp is { } panelStamp)
            {
                targetGen = panelStamp.Generation;
            }

            var hold = new Hold
            {
                WallId = wallId,
                WallPanelId = stamp?.PanelId,
                X = x,
                Y = y,
                Radius = radius,
                Color = color,
                Material = material,
                Category = category,
                HandType = handType,
                ShapePoints = shapePoints,
                IsAutoDetected = false,
                IsVirtual = isVirtual,
                Generation = targetGen,
            };

            db.Holds.Add(hold);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordHoldAdded(wallId);
            await _activityLogService.LogAsync(wallId, null, ActivityType.HoldAdded);
            _logger.LogInformation("Hold {HoldId} added to wall {WallId} at generation {Generation} by {UserId}", hold.Id, wallId, targetGen, user.Id);
            return hold;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Resolves the panel stamp for a hold placed while a specific panel was on screen: the panel
    /// row plus the generation that panel's own reads key on. Returns null — meaning "no panel, keep
    /// the wall-level generation", exactly as before — when no panel was given, the panel doesn't
    /// belong to this wall, or a wall update is currently staged.
    /// </summary>
    /// <remarks>
    /// Two things this must not get wrong.
    /// Generation: panel reads filter on <c>panel.Generation</c> (WallPanelService.Reads), never on
    /// <c>wall.CurrentGeneration</c> — after a subset promote a panel left untouched stays on an
    /// older generation, so stamping the wall's number would hide the hold from that panel's overlay.
    /// Staging: while <c>wall.StagedAt</c> is set the hold belongs to the staged set at
    /// <c>CurrentGeneration + 1</c>, whose panels don't exist yet under these ids; pinning it to a
    /// live panel would make it disappear at promotion. In that case no stamp is taken and the hold
    /// behaves exactly as it did before (null panel, rendered on the centre panel by the viewer).
    /// </remarks>
    private static async Task<(Guid PanelId, int Generation)?> ResolvePanelStampAsync(
        BlocwerkDbContext db,
        Wall wall,
        Guid? wallPanelId)
    {
        if (wallPanelId is not { } panelId || wall.StagedAt != null)
        {
            return null;
        }

        var panel = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wall.Id && p.Photo != null)
            .Select(p => new { p.Id, p.Generation })
            .FirstOrDefaultAsync();

        return panel is null ? null : (panel.Id, panel.Generation);
    }

    /// <summary>
    /// The wall's live CENTER panel (Col 0, Row 0, latest generation carrying bytes), or null when the
    /// wall has none. This is the panel the viewer already draws a panel-less hold on
    /// (BoulderDetail/BoulderCreate/BoulderRevise all fall back to the center), so stamping it is a
    /// data-only clarification and never moves a hold that renders today.
    /// </summary>
    /// <remarks>
    /// Same staging rule as <see cref="ResolvePanelStampAsync"/>: while <c>wall.StagedAt</c> is set the
    /// hold belongs to the staged set at <c>CurrentGeneration + 1</c>, and pinning it to a live panel
    /// would make it vanish at promotion — so no stamp is taken and the null-panel center fallback
    /// keeps rendering it. <c>Hold.Generation</c> is deliberately NOT touched by this stamp: it keys
    /// cross-generation lineage (<see cref="HoldGenerationLink"/>), the outdated-panel flag and the
    /// change journal, while every read that shows such a hold today resolves it by panel id alone.
    /// </remarks>
    private static async Task<Guid?> ResolveCenterPanelStampAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var stagedAt = await db.Walls
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => w.StagedAt)
            .FirstOrDefaultAsync(ct);
        if (stagedAt != null)
        {
            return null;
        }

        return await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Col == 0 && p.Row == 0 && p.Photo != null)
            .OrderByDescending(p => p.Generation)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Moves the merge survivor onto the panel its new geometry belongs to. The survivor has just
    /// adopted the detected hold's coordinates, and those are panel-LOCAL — kept on another panel they
    /// would be drawn against the wrong image — so it takes the target's panel AND that target's
    /// generation, the pair the panel overlay reads key on together. Only a LIVE target panel is
    /// adopted: inheriting a superseded panel row would drop the survivor out of the live-panel window.
    /// A target with no panel (legacy single-image row) leaves the survivor's own stamp alone, and a
    /// survivor that still has none falls back to the live center panel it already renders on.
    /// </summary>
    private static async Task AdoptMergedPanelStampAsync(
        BlocwerkDbContext db,
        Hold survivor,
        Hold target,
        CancellationToken ct)
    {
        if (target.WallPanelId is { } targetPanelId
            && await db.WallPanels.AnyAsync(p => p.Id == targetPanelId && p.Photo != null, ct))
        {
            survivor.WallPanelId = targetPanelId;
            survivor.Generation = target.Generation;
            return;
        }

        if (survivor.WallPanelId is null)
        {
            survivor.WallPanelId = await ResolveCenterPanelStampAsync(db, survivor.WallId, ct);
        }
    }

    public async Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null, HoldMaterial? material = null, bool flagBouldersOnMove = true, HoldHandType? handType = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.UpdateHold");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                _logger.LogWarning("Hold {HoldId} not found for update by {UserId}", holdId, user.Id);
                throw new InvalidOperationException("Hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, hold.WallId, user.Id, CancellationToken.None);

            var wallStagedAt = await db.Walls.Where(w => w.Id == hold.WallId).Select(w => w.StagedAt).FirstOrDefaultAsync();
            bool isStaging = wallStagedAt != null;

            bool positionChanged = Math.Abs(hold.X - x) > 0.0001 || Math.Abs(hold.Y - y) > 0.0001;

            // Callers send the full intended state, so color/material are assigned
            // unconditionally — allowing them to be cleared, not only set.
            bool colorChanged = hold.Color != color;
            bool shapeChanged = shapePoints != null;
            bool nameChanged = name != null && hold.Name != name;

            hold.X = x;
            hold.Y = y;
            hold.Radius = radius;
            hold.Color = color;
            hold.Material = material;
            hold.HandType = handType;

            if (category.HasValue)
            {
                hold.Category = category.Value;
            }

            if (isOnKickboard.HasValue)
            {
                hold.IsOnKickboard = isOnKickboard.Value;
            }

            if (shapePoints != null)
            {
                hold.ShapePoints = shapePoints;
            }

            if (name != null)
            {
                hold.Name = name;
            }

            if (positionChanged)
            {
                // Big-wall panel edits pass flagBouldersOnMove:false — between two panel photos taken from
                // slightly different spots, a hold's position drifts by parallax, so a move alone must not
                // flag its boulders. There, "changed" is a manual per-hold decision (Mark modified / Mark
                // unchanged). The single-image editors keep the default, so a move still retires boulders.
                if (flagBouldersOnMove)
                {
                    var affectedBoulders = await db.BoulderHolds
                        .Where(bh => bh.HoldId == holdId)
                        .Select(bh => bh.Boulder)
                        .Where(b => !b.IsArchived)
                        .ToListAsync();

                    foreach (var boulder in affectedBoulders)
                    {
                        if (isStaging)
                        {
                            boulder.NeedsReview = true;
                        }
                        else if (!boulder.IsHistoric)
                        {
                            boulder.IsHistoric = true;
                        }
                    }
                }

                BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "moved");
                await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldMoved);
            }
            else if (nameChanged)
            {
                BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "named");
                await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldNamed, name);
            }
            else if (colorChanged)
            {
                BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "color");
                await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldColorChanged);
            }
            else if (shapeChanged)
            {
                BlocwerkMetrics.RecordHoldUpdated(hold.WallId, "shape");
                await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldShapeChanged);
            }

            await db.SaveChangesAsync();

            // The edited hold is now authoritative: every hold transitively linked to it (the same
            // physical hold seen on other panels) inherits its appearance verbatim. Runs alongside the
            // existing move/name/cascade logic above — it only touches appearance fields and re-saves
            // when a twin actually changed. Best-effort: the edit itself is already committed above, so a
            // failure here (transient DB error / concurrency) must NOT surface the edit as failed — the
            // startup backfill reconciles linked twins on the next start.
            try
            {
                await SyncLinkedAppearanceAsync(db, hold);
            }
            catch (Exception syncEx)
            {
                _logger.LogWarning(syncEx, "Failed to sync appearance to linked twins of hold {HoldId}; the backfill will reconcile it.", holdId);
            }

            _logger.LogInformation("Hold {HoldId} on wall {WallId} updated by {UserId} (moved: {Moved}, renamed: {Renamed}, recolored: {Recolored}, reshaped: {Reshaped})", holdId, hold.WallId, user.Id, positionChanged, nameChanged, colorChanged, shapeChanged);
            return hold;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// After a live edit, propagates the edited hold's appearance (Color/Material/Category/HandType)
    /// to every hold transitively linked to it on the same wall — the same physical hold seen on other
    /// panels. The EDITED hold is the source here (its new values win, regardless of centrality). Only
    /// appearance fields, verbatim; write-if-changed, so it re-saves only when a twin actually differs.
    /// </summary>
    private static async Task SyncLinkedAppearanceAsync(BlocwerkDbContext db, Hold source)
    {
        var links = await db.HoldLinks
            .Where(l => l.WallId == source.WallId)
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToListAsync();
        if (links.Count == 0)
        {
            return;
        }

        var holdIds = links.SelectMany(l => new[] { l.HoldAId, l.HoldBId }).Distinct().ToList();
        if (!holdIds.Contains(source.Id))
        {
            return;
        }

        var component = HoldPropertySync.ConnectedComponents(holdIds, links)
            .FirstOrDefault(c => c.Contains(source.Id));
        if (component is null || component.Count < 2)
        {
            return;
        }

        var twinIds = component.Where(id => id != source.Id).ToList();
        var twins = await db.Holds.Where(h => twinIds.Contains(h.Id)).ToListAsync();

        var changed = false;
        foreach (var twin in twins)
        {
            if (HoldPropertySync.CopyAppearance(source, twin))
            {
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Overlap twins of <paramref name="hold"/> (linked via <see cref="HoldLink"/>) that sit on a MORE
    /// peripheral panel — further from the (0,0) centre. The centre panel is ground truth, so a hold
    /// confirmed unchanged here also settles those twins (the same physical hold on an outer photo).
    /// </summary>
    private async Task<List<Guid>> GetPeripheralTwinIdsAsync(BlocwerkDbContext db, Hold hold)
    {
        var twinIds = await db.HoldLinks
            .Where(l => l.HoldAId == hold.Id || l.HoldBId == hold.Id)
            .Select(l => l.HoldAId == hold.Id ? l.HoldBId : l.HoldAId)
            .ToListAsync();
        if (twinIds.Count == 0)
        {
            return [];
        }

        var selfCentrality = await PanelCentralityAsync(db, hold.WallPanelId);
        var twins = await db.Holds
            .Where(h => twinIds.Contains(h.Id))
            .Select(h => new { h.Id, h.WallPanelId })
            .ToListAsync();

        var peripheral = new List<Guid>();
        foreach (var twin in twins)
        {
            if (await PanelCentralityAsync(db, twin.WallPanelId) > selfCentrality)
            {
                peripheral.Add(twin.Id);
            }
        }

        return peripheral;
    }

    // A panel's distance from the (0,0) centre on the sparse panel grid (Manhattan). A null panel is a
    // legacy single-photo/centre hold, treated as the centre (0). Smaller = more central = more authoritative.
    private static async Task<int> PanelCentralityAsync(BlocwerkDbContext db, Guid? panelId)
    {
        if (panelId is not { } id)
        {
            return 0;
        }

        var pos = await db.WallPanels
            .Where(p => p.Id == id)
            .Select(p => new { p.Col, p.Row })
            .FirstOrDefaultAsync();
        return pos is null ? 0 : Math.Abs(pos.Col) + Math.Abs(pos.Row);
    }

    public async Task DeleteHoldAsync(Guid holdId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.DeleteHold");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                _logger.LogWarning("Hold {HoldId} not found for delete by {UserId}", holdId, user.Id);
                throw new InvalidOperationException("Hold not found");
            }

            await WallAdminGuard.EnsureWallEditorAsync(db, hold.WallId, user.Id, CancellationToken.None);

            // The HoldId FK is Restrict, so the hold cannot be removed while any BoulderHold references
            // it — load the link rows, flag each active boulder historic, then drop the links.
            var boulderLinks = await db.BoulderHolds
                .Include(bh => bh.Boulder)
                .Where(bh => bh.HoldId == holdId)
                .ToListAsync();

            var historicCount = 0;
            foreach (var link in boulderLinks)
            {
                if (link.Boulder is { IsArchived: false, IsHistoric: false })
                {
                    link.Boulder.IsHistoric = true;
                    historicCount++;
                }
            }

            db.BoulderHolds.RemoveRange(boulderLinks);

            // Hold-to-hold alignment links are Restrict on both ends too; drop any that touch this hold.
            var holdLinks = await db.HoldLinks
                .Where(l => l.HoldAId == holdId || l.HoldBId == holdId)
                .ToListAsync();
            db.HoldLinks.RemoveRange(holdLinks);

            db.Holds.Remove(hold);
            await db.SaveChangesAsync();

            BlocwerkMetrics.RecordHoldDeleted(hold.WallId);
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldDeleted);
            _logger.LogInformation("Hold {HoldId} on wall {WallId} deleted by {UserId}, {HistoricCount} boulder(s) made historic", holdId, hold.WallId, user.Id, historicCount);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task ClearAutoDetectedHoldsAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.ClearAutoDetectedHolds", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for clearing auto-detected holds by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            var autoHolds = await db.Holds
                .Where(h => h.WallId == wallId && h.IsAutoDetected && h.Generation == wall.CurrentGeneration)
                .ToListAsync();

            db.Holds.RemoveRange(autoHolds);
            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} auto-detected holds cleared by {UserId}: {RemovedCount} removed", wallId, user.Id, autoHolds.Count);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task SetBorderPointsAsync(Guid wallId, List<ShapePoint> points)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.SetBorderPoints", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for setting border points by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            wall.BorderPoints = points;
            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} border points set by {UserId}: {PointCount} point(s)", wallId, user.Id, points.Count);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<int> CleanOutsideBorderAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.CleanOutsideBorder", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                           .Include(w => w.Holds)
                           .Include(w => w.Segments)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for clean outside border by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            // Segments describe the wall in full when there are any, so a hold survives if it
            // sits in any one of them. Only a segment-less wall falls back to the border.
            var segments = wall.Segments.ToList();
            Func<Hold, bool> isInside;
            if (segments.Count > 0)
            {
                isInside = h => WallProjection.IsInsideAnySegment(h.X, h.Y, segments);
            }
            else
            {
                if (wall.BorderPoints == null || wall.BorderPoints.Count < 3)
                {
                    return 0;
                }

                var borderPolygon = wall.BorderPoints.Select(p => (p.Dx, p.Dy)).ToList();
                isInside = h => IsPointInPolygon(h.X, h.Y, borderPolygon);
            }

            var toRemove = wall.Holds
                .Where(h => h.Generation == wall.CurrentGeneration && !isInside(h))
                .ToList();

            db.Holds.RemoveRange(toRemove);
            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} cleaned outside border by {UserId}: {RemovedCount} hold(s) removed", wallId, user.Id, toRemove.Count);
            return toRemove.Count;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<WallMember>> GetMembersAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetMembers", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            return await db.WallMembers
                .AsNoTracking()
                .Include(wm => wm.User)
                .Where(wm => wm.WallId == wallId)
                .OrderBy(wm => wm.JoinedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<bool> UsersShareAWallAsync(Guid userA, Guid userB)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.UsersShareAWall");
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

            var wallsOfA = db.WallMembers.Where(m => m.UserId == userA).Select(m => m.WallId);
            return await db.WallMembers.AnyAsync(m => m.UserId == userB && wallsOfA.Contains(m.WallId));
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task SetMemberRoleAsync(Guid wallId, Guid userId, WallRole role)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.SetMemberRole", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls
                           .Include(w => w.Members)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for member role change by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            // Owner-aware admin check: a bare-owner (no explicit Admin member row) counts as admin,
            // matching the client _isAdmin gate. Everyone else is rejected exactly as before.
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var membership = await db.WallMembers
                .FirstOrDefaultAsync(wm => wm.WallId == wallId && wm.UserId == userId);
            if (membership == null)
            {
                _logger.LogWarning("Member {TargetUserId} not found on wall {WallId} for role change by {UserId}", userId, wallId, user.Id);
                throw new InvalidOperationException("Member not found");
            }

            membership.Role = role;
            await db.SaveChangesAsync();

            await _activityLogService.LogAsync(wallId, null, ActivityType.MemberRoleChanged, $"Role changed to {role}");
            _logger.LogInformation("Member {TargetUserId} on wall {WallId} role changed to {Role} by {UserId}", userId, wallId, role, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SetMaintenanceAsync(Guid wallId, bool underMaintenance)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.SetMaintenance", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Owner or an Admin member may toggle update mode. Loading is filter-ignoring so an owner
            // without an explicit member row can still administer their own wall.
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                throw new InvalidOperationException("Wall not found");
            }

            wall.UnderMaintenance = underMaintenance;
            wall.MaintenanceByUserId = underMaintenance ? user.Id : null;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Wall {WallId} update mode set to {State} by {UserId}", wallId, underMaintenance, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task SetAnonymousKioskSettingAsync(Guid wallId, bool allowed)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.SetAnonymousKioskSetting", wallId);
        try
        {
            // A kiosk session must not be able to grant its own tablet an unauthenticated write.
            // The service guard is the real gate; the card is also hidden on a tablet.
            KioskGuard.EnsureNotKiosk(_kioskContext, "Changing kiosk setting permission");

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Owner or an Admin member, exactly as update mode. Filter-ignoring so an owner without
            // an explicit member row can still administer their own wall.
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                throw new InvalidOperationException("Wall not found");
            }

            wall.AllowAnonymousKioskSetting = allowed;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Wall {WallId} anonymous kiosk setting set to {State} by {UserId}", wallId, allowed, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task SetKioskKeyboardShortcutsAsync(Guid wallId, bool allowed)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.SetKioskKeyboardShortcuts", wallId);
        try
        {
            // A kiosk session must not be able to grant its own tablet keyboard shortcuts.
            // The service guard is the real gate; the card is also hidden on a tablet.
            KioskGuard.EnsureNotKiosk(_kioskContext, "Changing kiosk keyboard shortcut permission");

            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            // Owner or an Admin member, exactly as update mode. Filter-ignoring so an owner without
            // an explicit member row can still administer their own wall.
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                throw new InvalidOperationException("Wall not found");
            }

            wall.AllowKioskKeyboardShortcuts = allowed;
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Wall {WallId} kiosk keyboard shortcuts set to {State} by {UserId}", wallId, allowed, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    private static bool IsPointInPolygon(double px, double py, List<(double X, double Y)> polygon) =>
        WallProjection.IsPointInPolygon(px, py, polygon);

    /// <summary>
    /// The ids of the panels currently LIVE — the latest-generation panel that still has a Photo at
    /// each (Col,Row) position. Mirrors <c>WallPanelService.GetPanelsAsync</c>'s dedup so a wall-level
    /// hold read shows exactly the holds on the panels the grid renders: after a subset promote this
    /// spans generations (an updated position at the new generation, an untouched one at its old
    /// generation) and never the superseded panel rows a re-shoot leaves behind.
    /// </summary>
    private static async Task<List<Guid>> LoadLivePanelIdsAsync(BlocwerkDbContext db, Guid wallId)
    {
        var panels = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
            .ToListAsync();

        return panels
            .GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).First().Id)
            .ToList();
    }

    /// <summary>
    /// Every wall is a big wall: keeps the (0,0) center panel in step with a freshly-uploaded photo and
    /// flags the wall multi-image, re-parenting the wall's current-generation unassigned holds (both the
    /// just-detected, still-tracked ones and any already persisted) onto it. When no live center panel
    /// exists yet it seeds one (shared with the startup converge, <see cref="WallCenterPanelConvergence"/>);
    /// when one already exists it REFRESHES that panel's bytes so a re-upload serves the new image and
    /// parents the new holds instead of orphaning them. Replaces the removed <c>EnableMultiImageAsync</c>
    /// toggle for the upload path. The caller commits.
    /// </summary>
    private static async Task EnsureCenterPanelAsync(BlocwerkDbContext db, Wall wall)
    {
        var panels = await db.WallPanels.Where(p => p.WallId == wall.Id).ToListAsync();

        var unassigned = db.Holds.Local
            .Where(h => h.WallId == wall.Id
                && h.Generation == wall.CurrentGeneration
                && h.WallPanelId == null)
            .ToList();
        var persisted = await db.Holds
            .Where(h => h.WallId == wall.Id
                && h.Generation == wall.CurrentGeneration
                && h.WallPanelId == null)
            .ToListAsync();
        foreach (var hold in persisted)
        {
            if (!unassigned.Contains(hold))
            {
                unassigned.Add(hold);
            }
        }

        var liveCenter = panels.FirstOrDefault(p => p.Col == 0 && p.Row == 0 && p.Photo != null);
        if (liveCenter is not null)
        {
            // Re-upload onto an existing big wall: refresh the live center panel's bytes and adopt the
            // freshly-detected (still null-panel) holds so /photo and the panel hold reads stay in step.
            // Full re-capture/generation semantics are a later phase; here we only keep it consistent.
            //
            // The prior detection set already parented to this panel would otherwise survive alongside the
            // fresh set and double up. Drop the panel's current-generation auto-detected holds that no
            // boulder references before adopting the new ones — boulder-safe: manual holds and any hold a
            // BoulderHold points at stay put (mirrors WallPanelService.Detection's redetect contract). On a
            // first upload no live center panel exists yet, so this path never runs and nothing is removed.
            await RemoveReplacedCenterPanelAutoHoldsAsync(db, wall, liveCenter.Id);

            liveCenter.Photo = wall.Photo is null ? null : (byte[])wall.Photo.Clone();
            liveCenter.PhotoContentType = wall.PhotoContentType;
            foreach (var hold in unassigned)
            {
                hold.WallPanelId = liveCenter.Id;
            }

            return;
        }

        WallCenterPanelConvergence.EnsureCenterPanel(db, wall, panels, unassigned);
    }

    /// <summary>
    /// Removes the center panel's current-generation auto-detected holds that no boulder references, so a
    /// re-upload's fresh detection set replaces the previous one instead of layering on top of it. Manual
    /// holds and any hold a <see cref="BoulderHold"/> points at are always kept, so no boulder is orphaned
    /// (same boulder-safe contract as WallPanelService's redetect/clean). The caller commits.
    /// </summary>
    private static async Task RemoveReplacedCenterPanelAutoHoldsAsync(BlocwerkDbContext db, Wall wall, Guid centerPanelId)
    {
        var autoHolds = await db.Holds
            .Where(h => h.WallPanelId == centerPanelId
                && h.WallId == wall.Id
                && h.IsAutoDetected
                && h.Generation == wall.CurrentGeneration)
            .ToListAsync();
        if (autoHolds.Count == 0)
        {
            return;
        }

        var autoIds = autoHolds.Select(h => h.Id).ToList();
        var referenced = (await db.BoulderHolds
                .Where(bh => autoIds.Contains(bh.HoldId))
                .Select(bh => bh.HoldId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        var removable = autoHolds.Where(h => !referenced.Contains(h.Id)).ToList();
        if (removable.Count > 0)
        {
            db.Holds.RemoveRange(removable);
        }
    }

    /// <summary>
    /// Resolves a generation to a photo: the live photo for the current generation and
    /// beyond, otherwise the archived photo of the reset that retired it.
    /// </summary>
    /// <summary>
    /// The wall as this caller may see it: filtered by the share token when one was supplied,
    /// otherwise left to the context's membership query filter.
    /// </summary>
    private static IQueryable<Wall> AccessibleWall(BlocwerkDbContext db, Guid wallId, string? shareToken) =>
        string.IsNullOrEmpty(shareToken)
            ? db.Walls.Where(w => w.Id == wallId)
            : db.Walls.Where(w => w.Id == wallId && w.ShareToken == shareToken);

    private static async Task<WallPhoto?> ResolveGenerationPhotoAsync(
        BlocwerkDbContext db,
        IQueryable<Wall> accessibleWall,
        Guid wallId,
        int generation)
    {
        var wall = await accessibleWall
            .AsNoTracking()
            .Select(w => new { w.Photo, w.PhotoContentType, w.CurrentGeneration })
            .FirstOrDefaultAsync();

        if (wall == null)
        {
            return null;
        }

        if (generation >= wall.CurrentGeneration)
        {
            return wall.Photo == null ? null : new WallPhoto(wall.Photo, wall.PhotoContentType);
        }

        var reset = await db.WallResets
            .AsNoTracking()
            .Where(r => r.WallId == wallId && r.Generation == generation)
            .Select(r => new { r.PreviousPhoto, r.PreviousPhotoContentType })
            .FirstOrDefaultAsync();

        return reset?.PreviousPhoto == null
            ? null
            : new WallPhoto(reset.PreviousPhoto, reset.PreviousPhotoContentType);
    }
}
