using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Dev-only <see cref="IWallService"/> that pulls one wall (the first available)
/// from Postgres on first use and then serves all reads and writes from a local
/// JSON + binary snapshot on disk. Lets the wall editor be iterated with hot
/// reload against stable local state without round-tripping to PG.
/// </summary>
public class DevSnapshotWallService : IWallService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<DevSnapshotWallService> logger;
    private readonly string snapshotDir;
    private readonly string snapshotJsonPath;
    private readonly string photoPath;
    private readonly string stagedPhotoPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    private Wall? wall;
    private byte[]? photoBytes;
    private byte[]? stagedPhotoBytes;
    private bool loaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public DevSnapshotWallService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IHostEnvironment env,
        ILogger<DevSnapshotWallService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
        snapshotDir = Path.Combine(env.ContentRootPath, "dev-wall-snapshot");
        snapshotJsonPath = Path.Combine(snapshotDir, "wall.json");
        photoPath = Path.Combine(snapshotDir, "photo.bin");
        stagedPhotoPath = Path.Combine(snapshotDir, "staged-photo.bin");
    }

    private async Task EnsureLoadedAsync()
    {
        if (loaded)
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            if (loaded)
            {
                return;
            }

            Directory.CreateDirectory(snapshotDir);

            if (File.Exists(snapshotJsonPath))
            {
                logger.LogInformation("DevSnapshot: loading wall from {Path}", snapshotJsonPath);
                var json = await File.ReadAllTextAsync(snapshotJsonPath);
                wall = JsonSerializer.Deserialize<Wall>(json, JsonOpts)
                       ?? throw new InvalidOperationException("Snapshot JSON is empty or malformed.");
                if (File.Exists(photoPath))
                {
                    photoBytes = await File.ReadAllBytesAsync(photoPath);
                }

                if (File.Exists(stagedPhotoPath))
                {
                    stagedPhotoBytes = await File.ReadAllBytesAsync(stagedPhotoPath);
                }
            }
            else
            {
                logger.LogInformation("DevSnapshot: no local snapshot, seeding from PG…");
                await using var db = await dbContextFactory.CreateDbContextAsync();
                db.CurrentUserId = Guid.Empty;

                var seeded = await db.Walls
                    .AsSplitQuery()
                    .Include(w => w.Members)
                    .Include(w => w.Holds)
                    .Include(w => w.Boulders).ThenInclude(b => b.BoulderHolds)
                    .OrderBy(w => w.CreatedAt)
                    .FirstOrDefaultAsync()
                    ?? throw new InvalidOperationException(
                        "DevSnapshot: no walls in PG to seed from. Create one first or disable BLOCWERK_DEV_WALL_SNAPSHOT.");

                photoBytes = seeded.Photo;
                stagedPhotoBytes = seeded.StagedPhoto;
                seeded.Photo = null;
                seeded.StagedPhoto = null;
                wall = seeded;

                await PersistAsync();
                logger.LogInformation(
                    "DevSnapshot: seeded wall {Id} ({Name}) with {HoldCount} holds to {Path}",
                    seeded.Id, seeded.Name, seeded.Holds.Count, snapshotDir);
            }

            loaded = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistAsync()
    {
        Directory.CreateDirectory(snapshotDir);
        var json = JsonSerializer.Serialize(wall, JsonOpts);
        await File.WriteAllTextAsync(snapshotJsonPath, json);

        if (photoBytes != null)
        {
            await File.WriteAllBytesAsync(photoPath, photoBytes);
        }
        else if (File.Exists(photoPath))
        {
            File.Delete(photoPath);
        }

        if (stagedPhotoBytes != null)
        {
            await File.WriteAllBytesAsync(stagedPhotoPath, stagedPhotoBytes);
        }
        else if (File.Exists(stagedPhotoPath))
        {
            File.Delete(stagedPhotoPath);
        }
    }

    private Wall Wall => wall ?? throw new InvalidOperationException("DevSnapshot: not loaded yet.");

    private Wall ProjectForRead()
    {
        var w = Wall;
        return new Wall
        {
            Id = w.Id,
            Name = w.Name,
            Description = w.Description,
            OwnerId = w.OwnerId,
            ShareToken = w.ShareToken,
            Angle = w.Angle,
            BorderPoints = w.BorderPoints,
            IsActive = w.IsActive,
            CreatedAt = w.CreatedAt,
            LastResetAt = w.LastResetAt,
            CurrentGeneration = w.CurrentGeneration,
            StagedAt = w.StagedAt,
            StagedByUserId = w.StagedByUserId,
            StagingMode = w.StagingMode,
            PhotoContentType = w.PhotoContentType,
            StagedPhotoContentType = w.StagedPhotoContentType,
            Members = w.Members,
            Boulders = w.Boulders,
            Holds = w.Holds
                .Where(h => h.Generation >= w.CurrentGeneration && h.Generation <= w.CurrentGeneration + 1)
                .ToList(),
        };
    }

    public Task<Wall> CreateWallAsync(string name, string? description, int angle = 0)
        => throw new NotSupportedException("DevSnapshot: wall creation is disabled. Use the real backend.");

    public async Task<Wall?> GetWallAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        return ProjectForRead();
    }

    public async Task<Wall?> GetWallByShareTokenAsync(string shareToken)
    {
        await EnsureLoadedAsync();
        if (Wall.ShareToken != shareToken)
        {
            return null;
        }

        var w = ProjectForRead();
        w.Holds = w.Holds.Where(h => h.Generation == w.CurrentGeneration).ToList();
        return w;
    }

    public async Task<List<Wall>> GetMyWallsAsync()
    {
        await EnsureLoadedAsync();
        return new List<Wall> { ProjectForRead() };
    }

    public async Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description, int? angle = null)
    {
        await EnsureLoadedAsync();
        Wall.Name = name;
        Wall.Description = description;
        if (angle.HasValue)
        {
            Wall.Angle = angle.Value;
        }

        await PersistAsync();
        return ProjectForRead();
    }

    public Task DeleteWallAsync(Guid wallId)
        => throw new NotSupportedException("DevSnapshot: wall deletion is disabled.");

    public async Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType, bool autoDetect = true)
    {
        await EnsureLoadedAsync();
        photoBytes = photo;
        Wall.PhotoContentType = contentType;
        await PersistAsync();
        logger.LogInformation("DevSnapshot: UploadPhoto ({Bytes} bytes, autoDetect={AutoDetect} ignored in dev)", photo.Length, autoDetect);
        return ProjectForRead();
    }

    public async Task<Wall> StagePhotoAsync(Guid wallId, byte[] photo, string contentType)
    {
        await EnsureLoadedAsync();
        if (photoBytes == null)
        {
            throw new InvalidOperationException("No live photo yet; use UploadPhotoAsync first.");
        }

        var stagedGen = Wall.CurrentGeneration + 1;
        Wall.Holds = Wall.Holds.Where(h => h.Generation != stagedGen).ToList();

        stagedPhotoBytes = photo;
        Wall.StagedPhotoContentType = contentType;
        Wall.StagedAt = DateTimeOffset.UtcNow;
        Wall.StagedByUserId = Guid.Empty;
        Wall.StagingMode = WallStagingMode.Detected;

        await PersistAsync();
        return ProjectForRead();
    }

    public async Task<Wall> StageManualAlignmentAsync(Guid wallId, byte[] photo, string contentType)
    {
        await EnsureLoadedAsync();
        if (photoBytes == null)
        {
            throw new InvalidOperationException("No live photo yet; use UploadPhotoAsync first.");
        }

        var liveGen = Wall.CurrentGeneration;
        var stagedGen = liveGen + 1;
        Wall.Holds = Wall.Holds.Where(h => h.Generation != stagedGen).ToList();

        stagedPhotoBytes = photo;
        Wall.StagedPhotoContentType = contentType;
        Wall.StagedAt = DateTimeOffset.UtcNow;
        Wall.StagedByUserId = Guid.Empty;
        Wall.StagingMode = WallStagingMode.Manual;

        foreach (var source in Wall.Holds.Where(h => h.Generation == liveGen).ToList())
        {
            Wall.Holds.Add(new Hold
            {
                WallId = Wall.Id,
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

        await PersistAsync();
        return ProjectForRead();
    }

    public async Task<Wall> ConfirmStagedPhotoAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        if (stagedPhotoBytes == null)
        {
            throw new InvalidOperationException("No staged photo to confirm.");
        }

        var liveGen = Wall.CurrentGeneration;
        var stagedGen = liveGen + 1;
        foreach (var hold in Wall.Holds.Where(h => h.Generation == liveGen).ToList())
        {
            hold.Generation = stagedGen;
        }

        photoBytes = stagedPhotoBytes;
        Wall.PhotoContentType = Wall.StagedPhotoContentType;
        stagedPhotoBytes = null;
        Wall.StagedPhotoContentType = null;
        Wall.StagedAt = null;
        Wall.StagedByUserId = null;
        Wall.StagingMode = WallStagingMode.None;
        Wall.CurrentGeneration = stagedGen;

        await PersistAsync();
        return ProjectForRead();
    }

    public async Task<Wall> ConfirmManualAlignmentAsync(Guid wallId, List<ManualAlignHold> holds, List<Guid> deletedStagedIds)
    {
        await EnsureLoadedAsync();
        if (stagedPhotoBytes == null || Wall.StagingMode != WallStagingMode.Manual)
        {
            throw new InvalidOperationException("Wall is not in manual alignment mode.");
        }

        var liveGen = Wall.CurrentGeneration;
        var stagedGen = liveGen + 1;
        var liveById = Wall.Holds.Where(h => h.Generation == liveGen).ToDictionary(h => h.Id);
        var stagedById = Wall.Holds.Where(h => h.Generation == stagedGen).ToDictionary(h => h.Id);
        var toRemove = new List<Hold>();

        foreach (var deletedId in deletedStagedIds)
        {
            if (!stagedById.TryGetValue(deletedId, out var deletedClone))
            {
                continue;
            }

            if (deletedClone.AlignmentSourceHoldId is { } srcId && liveById.TryGetValue(srcId, out var source))
            {
                toRemove.Add(source);
                liveById.Remove(srcId);
            }

            toRemove.Add(deletedClone);
            stagedById.Remove(deletedId);
        }

        foreach (var input in holds)
        {
            if (input.IsNew)
            {
                Wall.Holds.Add(new Hold
                {
                    WallId = Wall.Id,
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
                }

                toRemove.Add(clone);
            }
            else
            {
                clone.AlignmentSourceHoldId = null;
            }
        }

        foreach (var source in liveById.Values)
        {
            source.Generation = stagedGen;
        }

        toRemove.AddRange(Wall.Holds.Where(h => h.Generation == stagedGen && h.AlignmentSourceHoldId != null));
        Wall.Holds = Wall.Holds.Except(toRemove).ToList();

        photoBytes = stagedPhotoBytes;
        Wall.PhotoContentType = Wall.StagedPhotoContentType;
        stagedPhotoBytes = null;
        Wall.StagedPhotoContentType = null;
        Wall.StagedAt = null;
        Wall.StagedByUserId = null;
        Wall.StagingMode = WallStagingMode.None;
        Wall.CurrentGeneration = stagedGen;

        await PersistAsync();
        return ProjectForRead();
    }

    public async Task DiscardStagedPhotoAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        if (stagedPhotoBytes == null)
        {
            return;
        }

        var stagedGen = Wall.CurrentGeneration + 1;
        Wall.Holds = Wall.Holds.Where(h => h.Generation != stagedGen).ToList();

        stagedPhotoBytes = null;
        Wall.StagedPhotoContentType = null;
        Wall.StagedAt = null;
        Wall.StagedByUserId = null;
        Wall.StagingMode = WallStagingMode.None;

        await PersistAsync();
    }

    public async Task<byte[]?> GetStagedPhotoAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        return stagedPhotoBytes;
    }

    public async Task<Hold> MarkHoldModifiedAsync(Guid holdId)
    {
        await EnsureLoadedAsync();
        var hold = Wall.Holds.FirstOrDefault(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");
        hold.NeedsReview = true;
        await PersistAsync();
        return hold;
    }

    public async Task<Hold> MergeHoldsAsync(Guid stagedHoldId, Guid liveHoldId)
    {
        await EnsureLoadedAsync();
        var staged = Wall.Holds.FirstOrDefault(h => h.Id == stagedHoldId)
                     ?? throw new InvalidOperationException("Staged hold not found");
        var live = Wall.Holds.FirstOrDefault(h => h.Id == liveHoldId)
                   ?? throw new InvalidOperationException("Live hold not found");

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

        live.IsVirtual = false;
        live.NeedsReview = true;
        Wall.Holds = Wall.Holds.Where(h => h.Id != staged.Id).ToList();

        await PersistAsync();
        return live;
    }

    public async Task<string> GenerateShareTokenAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        Wall.ShareToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await PersistAsync();
        return Wall.ShareToken;
    }

    public Task<Wall> JoinWallAsync(string shareToken)
        => throw new NotSupportedException("DevSnapshot: joining walls is disabled.");

    public async Task<byte[]?> GetPhotoAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        return photoBytes;
    }

    public async Task<byte[]?> GetPhotoByShareTokenAsync(Guid wallId, string shareToken)
    {
        await EnsureLoadedAsync();
        if (Wall.ShareToken != shareToken || Wall.Id != wallId)
        {
            return null;
        }

        return photoBytes;
    }

    public async Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color, HoldCategory category = HoldCategory.Hand, List<ShapePoint>? shapePoints = null, bool isVirtual = false)
    {
        await EnsureLoadedAsync();
        var targetGen = Wall.StagedAt != null ? Wall.CurrentGeneration + 1 : Wall.CurrentGeneration;
        var hold = new Hold
        {
            WallId = Wall.Id,
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
        Wall.Holds.Add(hold);
        await PersistAsync();
        return hold;
    }

    public async Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius, string? color = null, HoldCategory? category = null, bool? isOnKickboard = null, List<ShapePoint>? shapePoints = null, string? name = null)
    {
        await EnsureLoadedAsync();
        var hold = Wall.Holds.FirstOrDefault(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");

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

        await PersistAsync();
        return hold;
    }

    public async Task DeleteHoldAsync(Guid holdId)
    {
        await EnsureLoadedAsync();
        var hold = Wall.Holds.FirstOrDefault(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");
        Wall.Holds = Wall.Holds.Where(h => h.Id != hold.Id).ToList();
        await PersistAsync();
    }

    public Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null)
        => throw new NotSupportedException("DevSnapshot: hold redetection is disabled (no ONNX in snapshot mode).");

    public async Task ClearAutoDetectedHoldsAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        Wall.Holds = Wall.Holds
            .Where(h => !(h.IsAutoDetected && h.Generation == Wall.CurrentGeneration))
            .ToList();
        await PersistAsync();
    }

    public async Task SetBorderPointsAsync(Guid wallId, List<ShapePoint> points)
    {
        await EnsureLoadedAsync();
        Wall.BorderPoints = points;
        await PersistAsync();
    }

    public async Task<int> CleanOutsideBorderAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        if (Wall.BorderPoints == null || Wall.BorderPoints.Count < 3)
        {
            return 0;
        }

        var polygon = Wall.BorderPoints.Select(p => (p.Dx, p.Dy)).ToList();
        var toRemove = Wall.Holds
            .Where(h => h.Generation == Wall.CurrentGeneration && !IsPointInPolygon(h.X, h.Y, polygon))
            .ToList();

        Wall.Holds = Wall.Holds.Except(toRemove).ToList();
        await PersistAsync();
        return toRemove.Count;
    }

    public async Task<List<WallMember>> GetMembersAsync(Guid wallId)
    {
        await EnsureLoadedAsync();
        var members = Wall.Members.OrderBy(m => m.JoinedAt).ToList();

        var missing = members.Where(m => m.User == null).Select(m => m.UserId).ToList();
        if (missing.Count > 0)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = Guid.Empty;
            var users = await db.Users.Where(u => missing.Contains(u.Id)).ToListAsync();
            foreach (var m in members)
            {
                if (m.User == null)
                {
                    m.User = users.FirstOrDefault(u => u.Id == m.UserId)!;
                }
            }
        }

        return members;
    }

    public async Task SetMemberRoleAsync(Guid wallId, Guid userId, WallRole role)
    {
        await EnsureLoadedAsync();
        var member = Wall.Members.FirstOrDefault(m => m.UserId == userId)
                     ?? throw new InvalidOperationException("Member not found");
        member.Role = role;
        await PersistAsync();
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
