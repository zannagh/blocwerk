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
    private readonly IImageAlignmentService _imageAlignmentService;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<WallService> _logger;

    public WallService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IImageAlignmentService imageAlignmentService,
        IActivityLogService activityLogService,
        ILogger<WallService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _holdDetectionService = holdDetectionService;
        _imageAlignmentService = imageAlignmentService;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<Wall> CreateWallAsync(string name, string? description, int angle = 0)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Create");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

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

    public async Task<Wall?> GetWallAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Get", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            // Project just the column (async, no blob load, no tracking) rather than materialising
            // the whole Wall entity just to read the generation.
            var currentGeneration = await db.Walls
                .Where(wl => wl.Id == wallId)
                .Select(wl => wl.CurrentGeneration)
                .FirstOrDefaultAsync();

            var wall = await db.Walls
                .AsSplitQuery()
                .Include(w => w.Members)
                .Include(w => w.Holds
                    .Where(h
                        => h.Generation >= currentGeneration
                            && h.Generation <= currentGeneration + 1))
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
                wall.Holds = wall.Holds.Where(h => h.Generation == wall.CurrentGeneration).ToList();
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
        using var op = BlocwerkMetrics.TimeOperation("Wall.UploadPhoto", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

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

    public Task<Wall> StagePhotoAsync(Guid wallId, byte[] photo, string contentType) =>
        StageDetectedAsync(wallId, photo, contentType, WallStagingMode.Detected);

    public Task<Wall> StageRecreateAsync(Guid wallId, byte[] photo, string contentType) =>
        StageDetectedAsync(wallId, photo, contentType, WallStagingMode.Recreate);

    public async Task<Wall> StageManualAlignmentAsync(Guid wallId, byte[] photo, string contentType)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.StageManual", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls
                           .Include(w => w.Holds)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for manual alignment staging by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.Photo == null)
            {
                _logger.LogWarning("Wall {WallId} has no live photo to stage manual alignment against for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("No live photo yet; use UploadPhotoAsync for the first photo.");
            }

            var liveGen = wall.CurrentGeneration;
            var stagedGen = liveGen + 1;

            var oldStagedHolds = wall.Holds.Where(h => h.Generation == stagedGen).ToList();
            db.Holds.RemoveRange(oldStagedHolds);

            wall.StagedPhoto = photo;
            wall.StagedPhotoContentType = contentType;
            wall.StagedAt = DateTimeOffset.UtcNow;
            wall.StagedByUserId = user.Id;
            wall.StagingMode = WallStagingMode.Manual;

            var liveHolds = wall.Holds.Where(h => h.Generation == liveGen).ToList();
            foreach (var source in liveHolds)
            {
                db.Holds.Add(new Hold
                {
                    WallId = wallId,
                    X = source.X,
                    Y = source.Y,
                    Radius = source.Radius,
                    ShapePoints = source.ShapePoints?.Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy }).ToList(),
                    Color = source.Color,
                    Category = source.Category,
                    IsOnKickboard = source.IsOnKickboard,
                    Name = source.Name,
                    IsAutoDetected = false,
                    NeedsReview = false,
                    Generation = stagedGen,
                    AlignmentSourceHoldId = source.Id,
                });
            }

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordWallPhotoStaged(wallId, "Manual");
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoStaged, $"{liveHolds.Count} holds staged for manual alignment");
            _logger.LogInformation("Wall {WallId} staged for manual alignment by {UserId} with {StagedHoldCount} holds carried", wallId, user.Id, liveHolds.Count);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall> ConfirmStagedPhotoAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.ConfirmPhoto", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls
                           .Include(w => w.Holds)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for staged photo confirmation by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.StagedPhoto == null)
            {
                _logger.LogWarning("Wall {WallId} has no staged photo to confirm for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("No staged photo to confirm.");
            }

            var liveGen = wall.CurrentGeneration;
            var stagedGen = liveGen + 1;

            var carried = 0;
            foreach (var hold in wall.Holds.Where(h => h.Generation == liveGen).ToList())
            {
                hold.Generation = stagedGen;
                carried++;
            }

            var stagedCount = wall.Holds.Count(h => h.Generation == stagedGen) - carried;

            ArchiveRetiredPhoto(db, wall, user.Id);

            wall.Photo = wall.StagedPhoto;
            wall.PhotoContentType = wall.StagedPhotoContentType;
            wall.StagedPhoto = null;
            wall.StagedPhotoContentType = null;
            wall.StagedAt = null;
            wall.StagedByUserId = null;
            wall.StagingMode = WallStagingMode.None;
            wall.CurrentGeneration = stagedGen;

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordWallPhotoConfirmed(wallId, "Staged");
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoConfirmed,
                $"{carried} carried, {stagedCount} new");
            _logger.LogInformation("Wall {WallId} staged photo confirmed by {UserId}: {CarriedCount} carried, {NewCount} new", wallId, user.Id, carried, stagedCount);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Wall> ConfirmManualAlignmentAsync(Guid wallId, List<ManualAlignHold> holds, List<Guid> deletedStagedIds)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.ConfirmManual", wallId);
        try
        {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId);
        if (wall == null)
        {
            _logger.LogWarning("Wall {WallId} not found for manual alignment confirmation by {UserId}", wallId, user.Id);
            throw new InvalidOperationException("Wall not found");
        }

        if (wall.StagedPhoto == null || wall.StagingMode != WallStagingMode.Manual)
        {
            _logger.LogWarning("Wall {WallId} is not in manual alignment mode for {UserId}", wallId, user.Id);
            throw new InvalidOperationException("Wall is not in manual alignment mode.");
        }

        var liveGen = wall.CurrentGeneration;
        var stagedGen = liveGen + 1;

        var liveHolds = wall.Holds.Where(h => h.Generation == liveGen).ToList();
        var stagedHolds = wall.Holds.Where(h => h.Generation == stagedGen).ToList();
        var liveById = liveHolds.ToDictionary(h => h.Id);
        var stagedById = stagedHolds.ToDictionary(h => h.Id);

        // Boulder links are resolved once for all affected source holds.
        var reviewCount = 0;

        // Holds the admin removed during alignment: drop the matching source hold and
        // flag its boulders as historic (physical hold no longer exists).
        foreach (var deletedId in deletedStagedIds)
        {
            if (!stagedById.TryGetValue(deletedId, out var deletedClone))
            {
                continue;
            }

            if (deletedClone.AlignmentSourceHoldId is { } srcId && liveById.TryGetValue(srcId, out var source))
            {
                // BoulderHold -> Hold is Restrict, so the links must be removed before
                // the source hold can be deleted. Boulders that used it become historic.
                var links = await db.BoulderHolds
                    .Where(bh => bh.HoldId == source.Id)
                    .Include(bh => bh.Boulder)
                    .ToListAsync();
                foreach (var link in links)
                {
                    if (link.Boulder is { IsArchived: false, IsHistoric: false })
                    {
                        link.Boulder.IsHistoric = true;
                    }
                }

                db.BoulderHolds.RemoveRange(links);
                db.Holds.Remove(source);
                liveById.Remove(srcId);
            }

            db.Holds.Remove(deletedClone);
            stagedById.Remove(deletedId);
        }

        // Surviving staged holds: apply their geometry, then promote.
        foreach (var input in holds)
        {
            if (input.IsNew)
            {
                // Hold added during alignment: create it as a brand-new live hold.
                db.Holds.Add(new Hold
                {
                    WallId = wallId,
                    X = input.X,
                    Y = input.Y,
                    Radius = input.Radius,
                    ShapePoints = input.ShapePoints,
                    Color = input.Color,
                    Material = input.Material,
                    Category = input.Category,
                    IsOnKickboard = input.IsOnKickboard,
                    IsAutoDetected = false,
                    Generation = stagedGen,
                });
                continue;
            }

            if (!stagedById.TryGetValue(input.StagedHoldId, out var clone))
            {
                continue;
            }

            if (clone.AlignmentSourceHoldId is { } srcId && liveById.TryGetValue(srcId, out var source))
            {
                source.X = input.X;
                source.Y = input.Y;
                source.Radius = input.Radius;
                source.ShapePoints = input.ShapePoints;
                source.Color = input.Color;
                source.Material = input.Material;
                source.Category = input.Category;
                source.IsOnKickboard = input.IsOnKickboard;

                if (input.DidChange)
                {
                    source.NeedsReview = true;
                    var boulders = await db.BoulderHolds
                        .Where(bh => bh.HoldId == source.Id)
                        .Select(bh => bh.Boulder)
                        .Where(b => !b.IsArchived)
                        .ToListAsync();
                    foreach (var b in boulders)
                    {
                        b.NeedsReview = true;
                        reviewCount++;
                    }
                }

                db.Holds.Remove(clone);
            }
            else
            {
                // Hold added during alignment: keep it as a brand-new live hold.
                clone.X = input.X;
                clone.Y = input.Y;
                clone.Radius = input.Radius;
                clone.ShapePoints = input.ShapePoints;
                clone.Color = input.Color;
                clone.Material = input.Material;
                clone.Category = input.Category;
                clone.IsOnKickboard = input.IsOnKickboard;
                clone.AlignmentSourceHoldId = null;
            }
        }

        // Carry remaining live sources up to the staged generation.
        foreach (var source in liveById.Values)
        {
            source.Generation = stagedGen;
        }

        // Safety net: any staged clone not accounted for above would otherwise
        // survive alongside its carried source and duplicate it. Drop leftovers.
        foreach (var leftover in wall.Holds.Where(h => h.Generation == stagedGen && h.AlignmentSourceHoldId != null).ToList())
        {
            db.Holds.Remove(leftover);
        }

        ArchiveRetiredPhoto(db, wall, user.Id);

        wall.Photo = wall.StagedPhoto;
        wall.PhotoContentType = wall.StagedPhotoContentType;
        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedAt = null;
        wall.StagedByUserId = null;
        wall.StagingMode = WallStagingMode.None;
        wall.CurrentGeneration = stagedGen;

        await db.SaveChangesAsync();
        BlocwerkMetrics.RecordWallPhotoConfirmed(wallId, "Manual");
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoConfirmed,
            $"manual alignment, {reviewCount} boulder(s) flagged for review");
        _logger.LogInformation("Wall {WallId} manual alignment confirmed by {UserId} with {ReviewCount} boulder(s) flagged for review", wallId, user.Id, reviewCount);
        return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<WallRecreateResult> ConfirmRecreateAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.ConfirmRecreate", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for recreation confirmation by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.StagedPhoto == null || wall.StagingMode != WallStagingMode.Recreate)
            {
                _logger.LogWarning("Wall {WallId} has no staged recreation to confirm for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("No staged wall recreation to confirm.");
            }

            var oldGen = wall.CurrentGeneration;
            var newGen = oldGen + 1;

            // Must run before the staged photo is promoted over the live one.
            ArchiveRetiredPhoto(db, wall, user.Id);

            // Holds from retired generations survive only while a boulder still points at
            // them; without this sweep every recreation would leave a full detection behind.
            var prunable = await db.Holds
                .Where(h => h.WallId == wallId && h.Generation <= oldGen && !h.BoulderHolds.Any())
                .ToListAsync();
            db.Holds.RemoveRange(prunable);

            // The hold model is entirely new, so every live boulder needs remapping.
            var staled = await db.Boulders
                .Where(b => b.WallId == wallId && !b.IsArchived && !b.IsHistoric)
                .ToListAsync();
            foreach (var boulder in staled)
            {
                boulder.IsHistoric = true;
                boulder.NeedsReview = false;
            }

            wall.Photo = wall.StagedPhoto;
            wall.PhotoContentType = wall.StagedPhotoContentType;
            wall.StagedPhoto = null;
            wall.StagedPhotoContentType = null;
            wall.StagedAt = null;
            wall.StagedByUserId = null;
            wall.StagingMode = WallStagingMode.None;
            wall.CurrentGeneration = newGen;
            wall.LastResetAt = DateTimeOffset.UtcNow;

            // Border points are normalized against the retired photo's framing.
            wall.BorderPoints = null;

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordWallRecreated(wallId, staled.Count, prunable.Count);
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallRecreated,
                $"{staled.Count} boulder(s) marked historic, {prunable.Count} unused hold(s) pruned");
            _logger.LogInformation("Wall {WallId} recreated by {UserId}: {BouldersMadeHistoric} boulder(s) made historic, {HoldsPruned} hold(s) pruned", wallId, user.Id, staled.Count, prunable.Count);

            return new WallRecreateResult(wall, staled.Count, prunable.Count);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<Homography?> EstimateStagingAlignmentAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.EstimateStagingAlignment", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                       ?? throw new InvalidOperationException("Wall not found");

            if (wall.Photo == null || wall.StagedPhoto == null)
            {
                throw new InvalidOperationException("No staged photo to align.");
            }

            // Homography mapping OLD photo (normalized) -> STAGED photo (normalized).
            // Callers apply this to the overlay holds in-memory so it flows through
            // the editor's normal Save/Discard, never mutating live holds directly.
            return await _imageAlignmentService.AlignNormalizedAsync(wall.StagedPhoto, wall.Photo);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DiscardStagedPhotoAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.DiscardStagedPhoto", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for staged photo discard by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.StagedPhoto == null)
            {
                return;
            }

            var stagedGen = wall.CurrentGeneration + 1;
            var stagedHolds = await db.Holds
                .Where(h => h.WallId == wallId && h.Generation == stagedGen)
                .Include(h => h.BoulderHolds)
                .ToListAsync();

            foreach (var hold in stagedHolds)
            {
                // A virtual hold placed from the boulder picker while a photo was staged
                // lands in the staged generation and may already be linked to a boulder.
                // Deleting it would trip the restricted FK and wedge discard for good, so
                // rescue it into the live generation instead.
                if (hold.BoulderHolds.Count > 0)
                {
                    hold.Generation = wall.CurrentGeneration;
                }
                else
                {
                    db.Holds.Remove(hold);
                }
            }

            wall.StagedPhoto = null;
            wall.StagedPhotoContentType = null;
            wall.StagedAt = null;
            wall.StagedByUserId = null;
            wall.StagingMode = WallStagingMode.None;

            await db.SaveChangesAsync();
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoDiscarded);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<byte[]?> GetStagedPhotoAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetStagedPhoto", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls
                .AsNoTracking()
                .Where(w => w.Id == wallId)
                .Select(w => new { w.StagedPhoto })
                .FirstOrDefaultAsync();

            return wall?.StagedPhoto;
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

    public async Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.MergeHolds");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var staged = await db.Holds.FirstOrDefaultAsync(h => h.Id == stagedHoldId);
            if (staged == null)
            {
                _logger.LogWarning("Staged hold {StagedHoldId} not found for merge by {UserId}", stagedHoldId, user.Id);
                throw new InvalidOperationException("Staged hold not found");
            }

            var live = await db.Holds.FirstOrDefaultAsync(h => h.Id == liveHoldId);
            if (live == null)
            {
                _logger.LogWarning("Live hold {LiveHoldId} not found for merge by {UserId}", liveHoldId, user.Id);
                throw new InvalidOperationException("Live hold not found");
            }

            if (staged.WallId != live.WallId)
            {
                _logger.LogWarning("Merge holds {StagedHoldId} and {LiveHoldId} belong to different walls for {UserId}", stagedHoldId, liveHoldId, user.Id);
                throw new InvalidOperationException("Holds belong to different walls");
            }

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == staged.WallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for hold merge by {UserId}", staged.WallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.StagedAt == null)
            {
                _logger.LogWarning("Wall {WallId} is not in staging mode for hold merge by {UserId}", wall.Id, user.Id);
                throw new InvalidOperationException("Wall is not in staging mode");
            }

            var liveGen = wall.CurrentGeneration;
            var stagedGen = liveGen + 1;

            if (staged.Generation != stagedGen || live.Generation > liveGen)
            {
                _logger.LogWarning("Holds {StagedHoldId} and {LiveHoldId} are not on opposite generations on wall {WallId} for {UserId}", stagedHoldId, liveHoldId, wall.Id, user.Id);
                throw new InvalidOperationException("Holds are not on opposite generations");
            }

            live.X = staged.X;
            live.Y = staged.Y;
            live.Radius = staged.Radius;
            if (staged.ShapePoints != null)
            {
                live.ShapePoints = staged.ShapePoints;
            }

            if (!string.IsNullOrEmpty(staged.Color))
            {
                live.Color = staged.Color;
            }

            // Merging always resolves the surviving hold to a real, detected one.
            live.IsVirtual = false;
            live.NeedsReview = true;

            var affectedBoulders = await db.BoulderHolds
                .Where(bh => bh.HoldId == live.Id)
                .Select(bh => bh.Boulder)
                .Where(b => !b.IsArchived)
                .ToListAsync();
            foreach (var b in affectedBoulders)
            {
                b.NeedsReview = true;
            }

            db.Holds.Remove(staged);

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordHoldUpdated(live.WallId, "merged");
            await _activityLogService.LogAsync(wall.Id, null, ActivityType.HoldMerged,
                $"{affectedBoulders.Count} boulder(s) flagged for review");
            _logger.LogInformation("Staged hold {StagedHoldId} merged into live hold {LiveHoldId} on wall {WallId} by {UserId}, {ReviewCount} boulder(s) flagged for review", stagedHoldId, liveHoldId, wall.Id, user.Id, affectedBoulders.Count);
            return live;
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

            if (!hold.IsVirtual)
            {
                throw new InvalidOperationException("Selected hold is not virtual");
            }

            // Promote in place: the hold keeps its Id, geometry and boulder links untouched.
            hold.IsVirtual = false;

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

            var wall = await db.Walls
                           .Include(w => w.Members)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for share token generation by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            var membership = wall.Members.FirstOrDefault(m => m.UserId == user.Id);
            if (membership?.Role != WallRole.Admin)
            {
                _logger.LogWarning("User {UserId} not authorized to generate share token for wall {WallId}", user.Id, wallId);
                throw new InvalidOperationException("Not authorized");
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

    public async Task<Wall> JoinWallAsync(string shareToken)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Join");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;

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
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

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

    public async Task<List<Hold>> GetHoldsForGenerationAsync(Guid wallId, int generation)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.GetHoldsForGeneration", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

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
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

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

    public async Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false, HoldMaterial? material = null, HoldHandType? handType = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.AddHold", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for add hold by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            var targetGen = wall.StagedAt != null ? wall.CurrentGeneration + 1 : wall.CurrentGeneration;
            var hold = new Hold
            {
                WallId = wallId,
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

    public async Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.RedetectHolds", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls
                           .Include(w => w.Holds)
                           .FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for hold redetection by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.Photo == null)
            {
                return 0;
            }

            var autoHolds = wall.Holds
                .Where(h => h.IsAutoDetected && h.Generation == wall.CurrentGeneration)
                .ToList();
            db.Holds.RemoveRange(autoHolds);

            var detected = await _holdDetectionService.DetectHoldsAsync(wall.Photo, parameters);
            foreach (var d in detected)
            {
                db.Holds.Add(new Hold
                {
                    WallId = wallId,
                    X = d.X,
                    Y = d.Y,
                    Radius = d.Radius,
                    Color = d.Color,
                    Confidence = d.Confidence,
                    IsAutoDetected = true,
                    Generation = wall.CurrentGeneration,
                });
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Wall {WallId} holds redetected by {UserId}: {DetectedHoldCount} holds detected", wallId, user.Id, detected.Count);
            return detected.Count;
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

            var callerMembership = wall.Members.FirstOrDefault(m => m.UserId == user.Id);
            if (callerMembership?.Role != WallRole.Admin)
            {
                _logger.LogWarning("User {UserId} not authorized to change member roles on wall {WallId}", user.Id, wallId);
                throw new InvalidOperationException("Not authorized");
            }

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

    private static bool IsPointInPolygon(double px, double py, List<(double X, double Y)> polygon) =>
        WallProjection.IsPointInPolygon(px, py, polygon);

    /// <summary>
    /// Keeps the outgoing photo so historic boulders can still be rendered against the
    /// wall as it looked when they were set. One row per retired generation.
    /// </summary>
    private static void ArchiveRetiredPhoto(BlocwerkDbContext db, Wall wall, Guid userId)
    {
        if (wall.Photo == null)
        {
            return;
        }

        db.WallResets.Add(new WallReset
        {
            WallId = wall.Id,
            Generation = wall.CurrentGeneration,
            PreviousPhoto = wall.Photo,
            PreviousPhotoContentType = wall.PhotoContentType,
            ResetByUserId = userId,
        });
    }

    /// <summary>
    /// Shared body of the two detection-based staging modes. They differ only in the
    /// staging mode recorded, which decides what confirm does with the live holds.
    /// </summary>
    private async Task<Wall> StageDetectedAsync(Guid wallId, byte[] photo, string contentType, WallStagingMode mode)
    {
        using var op = BlocwerkMetrics.TimeOperation("Wall.Stage", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
            if (wall == null)
            {
                _logger.LogWarning("Wall {WallId} not found for staging by {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            if (wall.Photo == null)
            {
                _logger.LogWarning("Wall {WallId} has no live photo to stage against for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("No live photo yet; use UploadPhotoAsync for the first photo.");
            }

            var stagedGen = wall.CurrentGeneration + 1;
            var oldStagedHolds = await db.Holds
                .Where(h => h.WallId == wallId && h.Generation == stagedGen)
                .ToListAsync();
            db.Holds.RemoveRange(oldStagedHolds);

            wall.StagedPhoto = photo;
            wall.StagedPhotoContentType = contentType;
            wall.StagedAt = DateTimeOffset.UtcNow;
            wall.StagedByUserId = user.Id;
            wall.StagingMode = mode;

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
                    Generation = stagedGen,
                });
            }

            await db.SaveChangesAsync();
            BlocwerkMetrics.RecordWallPhotoStaged(wallId, mode.ToString());
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoStaged, $"{detectedHolds.Count} holds detected");
            _logger.LogInformation("Wall {WallId} staged in {StagingMode} mode by {UserId} with {DetectedHoldCount} holds detected", wallId, mode, user.Id, detectedHolds.Count);
            return wall;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Resolves a generation to a photo: the live photo for the current generation and
    /// beyond, otherwise the archived photo of the reset that retired it.
    /// </summary>
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
