using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public record ManualAlignHold(
    Guid StagedHoldId,
    double X,
    double Y,
    double Radius,
    List<ShapePoint>? ShapePoints,
    string? Color,
    HoldCategory Category,
    bool IsOnKickboard,
    bool DidChange,
    bool IsNew);

public interface IWallService
{
    Task<Wall> CreateWallAsync(string name, string? description, int angle = 0);

    Task<Wall?> GetWallAsync(Guid wallId);

    Task<Wall?> GetWallByShareTokenAsync(string shareToken);

    Task<List<Wall>> GetMyWallsAsync();

    Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description, int? angle = null);

    Task DeleteWallAsync(Guid wallId);

    Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType, bool autoDetect = true);

    Task<Wall> StagePhotoAsync(Guid wallId, byte[] photo, string contentType);

    Task<Wall> StageManualAlignmentAsync(Guid wallId, byte[] photo, string contentType);

    Task<Wall> ConfirmStagedPhotoAsync(Guid wallId);

    Task<Wall> ConfirmManualAlignmentAsync(Guid wallId, List<ManualAlignHold> holds, List<Guid> deletedStagedIds);

    /// <summary>
    /// Estimates the old-photo -> staged-photo transform locally (normalized 0-1
    /// coordinates). Returns null when no reliable alignment could be found.
    /// Callers apply it to the overlay holds in-memory so it flows through the
    /// editor's normal Save/Discard.
    /// </summary>
    Task<Homography?> EstimateStagingAlignmentAsync(Guid wallId);

    Task DiscardStagedPhotoAsync(Guid wallId);

    Task<byte[]?> GetStagedPhotoAsync(Guid wallId);

    Task<Hold> MarkHoldModifiedAsync(Guid holdId);

    Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId);

    Task<string> GenerateShareTokenAsync(Guid wallId);

    Task<Wall> JoinWallAsync(string shareToken);

    Task<byte[]?> GetPhotoAsync(Guid wallId);

    Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken);

    Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false);

    Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null);

    Task DeleteHoldAsync(Guid holdId);

    Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null);

    Task ClearAutoDetectedHoldsAsync(Guid wallId);

    Task SetBorderPointsAsync(Guid wallId, List<ShapePoint> points);

    Task<int> CleanOutsideBorderAsync(Guid wallId);

    Task<List<WallMember>> GetMembersAsync(Guid wallId);

    Task SetMemberRoleAsync(Guid wallId, Guid userId, WallRole role);
}

public class WallService : IWallService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHoldDetectionService _holdDetectionService;
    private readonly IImageAlignmentService _imageAlignmentService;
    private readonly IActivityLogService _activityLogService;

    public WallService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IImageAlignmentService imageAlignmentService,
        IActivityLogService activityLogService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _holdDetectionService = holdDetectionService;
        _imageAlignmentService = imageAlignmentService;
        _activityLogService = activityLogService;
    }

    public async Task<Wall> CreateWallAsync(string name, string? description, int angle = 0)
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
        return wall;
    }

    public async Task<Wall?> GetWallAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
            .AsSplitQuery()
            .Include(w => w.Members)
            .Include(w => w.Holds.Where(h => h.Generation >= db.Walls.First(wl => wl.Id == wallId).CurrentGeneration
                                             && h.Generation <= db.Walls.First(wl => wl.Id == wallId).CurrentGeneration + 1))
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

    public async Task<Wall?> GetWallByShareTokenAsync(string shareToken)
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

    public async Task<List<Wall>> GetMyWallsAsync()
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var walls = await db.Walls
            .Include(w => w.Members)
            .ToListAsync();

        foreach (var w in walls)
        {
            w.Photo = null;
        }

        return walls;
    }

    public async Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description, int? angle = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        wall.Name = name;
        wall.Description = description;
        if (angle.HasValue)
        {
            wall.Angle = angle.Value;
        }

        await db.SaveChangesAsync();
        return wall;
    }

    public async Task DeleteWallAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId && w.OwnerId == user.Id)
                   ?? throw new InvalidOperationException("Wall not found or not owner");

        db.Walls.Remove(wall);
        await db.SaveChangesAsync();
    }

    public async Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType, bool autoDetect = true)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        wall.Photo = photo;
        wall.PhotoContentType = contentType;

        if (!autoDetect)
        {
            await db.SaveChangesAsync();
            await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoUploaded);
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
        return wall;
    }

    public async Task<Wall> StagePhotoAsync(Guid wallId, byte[] photo, string contentType)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.Photo == null)
        {
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
        wall.StagingMode = WallStagingMode.Detected;

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
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoStaged, $"{detectedHolds.Count} holds detected");
        return wall;
    }

    public async Task<Wall> StageManualAlignmentAsync(Guid wallId, byte[] photo, string contentType)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.Photo == null)
        {
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
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoStaged, $"{liveHolds.Count} holds staged for manual alignment");
        return wall;
    }

    public async Task<Wall> ConfirmStagedPhotoAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.StagedPhoto == null)
        {
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

        wall.Photo = wall.StagedPhoto;
        wall.PhotoContentType = wall.StagedPhotoContentType;
        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedAt = null;
        wall.StagedByUserId = null;
        wall.StagingMode = WallStagingMode.None;
        wall.CurrentGeneration = stagedGen;

        await db.SaveChangesAsync();
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoConfirmed,
            $"{carried} carried, {stagedCount} new");
        return wall;
    }

    public async Task<Wall> ConfirmManualAlignmentAsync(Guid wallId, List<ManualAlignHold> holds, List<Guid> deletedStagedIds)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.StagedPhoto == null || wall.StagingMode != WallStagingMode.Manual)
        {
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

        wall.Photo = wall.StagedPhoto;
        wall.PhotoContentType = wall.StagedPhotoContentType;
        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedAt = null;
        wall.StagedByUserId = null;
        wall.StagingMode = WallStagingMode.None;
        wall.CurrentGeneration = stagedGen;

        await db.SaveChangesAsync();
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoConfirmed,
            $"manual alignment, {reviewCount} boulder(s) flagged for review");
        return wall;
    }

    public async Task<Homography?> EstimateStagingAlignmentAsync(Guid wallId)
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

    public async Task DiscardStagedPhotoAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.StagedPhoto == null)
        {
            return;
        }

        var stagedGen = wall.CurrentGeneration + 1;
        var stagedHolds = await db.Holds
            .Where(h => h.WallId == wallId && h.Generation == stagedGen)
            .ToListAsync();
        db.Holds.RemoveRange(stagedHolds);

        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedAt = null;
        wall.StagedByUserId = null;
        wall.StagingMode = WallStagingMode.None;

        await db.SaveChangesAsync();
        await _activityLogService.LogAsync(wallId, null, ActivityType.WallPhotoDiscarded);
    }

    public async Task<byte[]?> GetStagedPhotoAsync(Guid wallId)
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

    public async Task<Hold> MarkHoldModifiedAsync(Guid holdId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");

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
        await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldMarkedModified,
            $"{affectedBoulders.Count} boulder(s) flagged for review");
        return hold;
    }

    public async Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var staged = await db.Holds.FirstOrDefaultAsync(h => h.Id == stagedHoldId)
                     ?? throw new InvalidOperationException("Staged hold not found");
        var live = await db.Holds.FirstOrDefaultAsync(h => h.Id == liveHoldId)
                   ?? throw new InvalidOperationException("Live hold not found");

        if (staged.WallId != live.WallId)
        {
            throw new InvalidOperationException("Holds belong to different walls");
        }

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == staged.WallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.StagedAt == null)
        {
            throw new InvalidOperationException("Wall is not in staging mode");
        }

        var liveGen = wall.CurrentGeneration;
        var stagedGen = liveGen + 1;

        if (staged.Generation != stagedGen || live.Generation > liveGen)
        {
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
        await _activityLogService.LogAsync(wall.Id, null, ActivityType.HoldMerged,
            $"{affectedBoulders.Count} boulder(s) flagged for review");
        return live;
    }

    public async Task<string> GenerateShareTokenAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Members)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var membership = wall.Members.FirstOrDefault(m => m.UserId == user.Id);
        if (membership?.Role != WallRole.Admin)
        {
            throw new InvalidOperationException("Not authorized");
        }

        wall.ShareToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await db.SaveChangesAsync();
        return wall.ShareToken;
    }

    public async Task<Wall> JoinWallAsync(string shareToken)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.ShareToken == shareToken)
                   ?? throw new InvalidOperationException("Invalid share token");

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
            await _activityLogService.LogAsync(wall.Id, null, ActivityType.MemberJoined);
        }

        return wall;
    }

    public async Task<byte[]?> GetPhotoAsync(Guid wallId)
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

    public async Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken)
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

    public async Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var targetGen = wall.StagedAt != null ? wall.CurrentGeneration + 1 : wall.CurrentGeneration;
        var hold = new Hold
        {
            WallId = wallId,
            X = x,
            Y = y,
            Radius = radius,
            Color = color,
            Category = category,
            ShapePoints = shapePoints,
            IsAutoDetected = false,
            IsVirtual = isVirtual,
            Generation = targetGen,
        };

        db.Holds.Add(hold);
        await db.SaveChangesAsync();

        await _activityLogService.LogAsync(wallId, null, ActivityType.HoldAdded);
        return hold;
    }

    public async Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");

        var wallStagedAt = await db.Walls.Where(w => w.Id == hold.WallId).Select(w => w.StagedAt).FirstOrDefaultAsync();
        bool isStaging = wallStagedAt != null;

        bool positionChanged = Math.Abs(hold.X - x) > 0.0001 || Math.Abs(hold.Y - y) > 0.0001;
        bool colorChanged = color != null && hold.Color != color;
        bool shapeChanged = shapePoints != null;
        bool nameChanged = name != null && hold.Name != name;

        hold.X = x;
        hold.Y = y;
        hold.Radius = radius;
        if (color != null)
        {
            hold.Color = color;
        }

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

            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldMoved);
        }
        else if (nameChanged)
        {
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldNamed, name);
        }
        else if (colorChanged)
        {
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldColorChanged);
        }
        else if (shapeChanged)
        {
            await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldShapeChanged);
        }

        await db.SaveChangesAsync();
        return hold;
    }

    public async Task DeleteHoldAsync(Guid holdId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");

        var affectedBoulders = await db.BoulderHolds
            .Where(bh => bh.HoldId == holdId)
            .Select(bh => bh.Boulder)
            .Where(b => !b.IsArchived && !b.IsHistoric)
            .ToListAsync();

        foreach (var boulder in affectedBoulders)
        {
            boulder.IsHistoric = true;
        }

        db.Holds.Remove(hold);
        await db.SaveChangesAsync();

        await _activityLogService.LogAsync(hold.WallId, null, ActivityType.HoldDeleted);
    }

    public async Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

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
        return detected.Count;
    }

    public async Task ClearAutoDetectedHoldsAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var autoHolds = await db.Holds
            .Where(h => h.WallId == wallId && h.IsAutoDetected && h.Generation == wall.CurrentGeneration)
            .ToListAsync();

        db.Holds.RemoveRange(autoHolds);
        await db.SaveChangesAsync();
    }

    public async Task SetBorderPointsAsync(Guid wallId, List<ShapePoint> points)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        wall.BorderPoints = points;
        await db.SaveChangesAsync();
    }

    public async Task<int> CleanOutsideBorderAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Holds)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        if (wall.BorderPoints == null || wall.BorderPoints.Count < 3)
        {
            return 0;
        }

        var borderPolygon = wall.BorderPoints.Select(p => (p.Dx, p.Dy)).ToList();
        var toRemove = wall.Holds
            .Where(h => h.Generation == wall.CurrentGeneration && !IsPointInPolygon(h.X, h.Y, borderPolygon))
            .ToList();

        db.Holds.RemoveRange(toRemove);
        await db.SaveChangesAsync();
        return toRemove.Count;
    }

    public async Task<List<WallMember>> GetMembersAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        return await db.WallMembers
            .Include(wm => wm.User)
            .Where(wm => wm.WallId == wallId)
            .OrderBy(wm => wm.JoinedAt)
            .ToListAsync();
    }

    public async Task SetMemberRoleAsync(Guid wallId, Guid userId, WallRole role)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls
                       .Include(w => w.Members)
                       .FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var callerMembership = wall.Members.FirstOrDefault(m => m.UserId == user.Id);
        if (callerMembership?.Role != WallRole.Admin)
        {
            throw new InvalidOperationException("Not authorized");
        }

        var membership = await db.WallMembers
            .FirstOrDefaultAsync(wm => wm.WallId == wallId && wm.UserId == userId)
            ?? throw new InvalidOperationException("Member not found");

        membership.Role = role;
        await db.SaveChangesAsync();

        await _activityLogService.LogAsync(wallId, null, ActivityType.MemberRoleChanged, $"Role changed to {role}");
    }

    private static bool IsPointInPolygon(double px, double py, List<(double X, double Y)> polygon)
    {
        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; i++)
        {
            if ((polygon[i].Y > py) != (polygon[j].Y > py) &&
                px < ((polygon[j].X - polygon[i].X) * (py - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X)
            {
                inside = !inside;
            }

            j = i;
        }

        return inside;
    }
}
