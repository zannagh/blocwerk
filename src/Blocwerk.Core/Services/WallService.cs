using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IWallService
{
    Task<Wall> CreateWallAsync(string name, string? description);

    Task<Wall?> GetWallAsync(Guid wallId);

    Task<List<Wall>> GetMyWallsAsync();

    Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description);

    Task DeleteWallAsync(Guid wallId);

    Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType);

    Task<string> GenerateShareTokenAsync(Guid wallId);

    Task<Wall> JoinWallAsync(string shareToken);

    Task<byte[]?> GetPhotoAsync(Guid wallId);

    Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color);

    Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius);

    Task DeleteHoldAsync(Guid holdId);

    Task<int> RedetectHoldsAsync(Guid wallId, HoldDetectionParameters? parameters = null);

    Task ClearAutoDetectedHoldsAsync(Guid wallId);
}

public class WallService : IWallService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHoldDetectionService _holdDetectionService;

    public WallService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _holdDetectionService = holdDetectionService;
    }

    public async Task<Wall> CreateWallAsync(string name, string? description)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var wall = new Wall
        {
            Name = name,
            Description = description,
            OwnerId = user.Id,
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

        return await db.Walls
            .Include(w => w.Members)
            .Include(w => w.Holds.Where(h => h.Generation == db.Walls.First(wl => wl.Id == wallId).CurrentGeneration))
            .Include(w => w.Boulders.Where(b => !b.IsArchived))
            .FirstOrDefaultAsync(w => w.Id == wallId);
    }

    public async Task<List<Wall>> GetMyWallsAsync()
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        return await db.Walls
            .Include(w => w.Members)
            .ToListAsync();
    }

    public async Task<Wall> UpdateWallAsync(Guid wallId, string name, string? description)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        wall.Name = name;
        wall.Description = description;
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

    public async Task<Wall> UploadPhotoAsync(Guid wallId, byte[] photo, string contentType)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        wall.Photo = photo;
        wall.PhotoContentType = contentType;

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
        return wall;
    }

    public async Task<string> GenerateShareTokenAsync(Guid wallId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId && w.OwnerId == user.Id)
                   ?? throw new InvalidOperationException("Wall not found or not owner");

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

    public async Task<Hold> AddHoldAsync(Guid wallId, double x, double y, double radius, string? color)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var hold = new Hold
        {
            WallId = wallId,
            X = x,
            Y = y,
            Radius = radius,
            Color = color,
            IsAutoDetected = false,
            Generation = wall.CurrentGeneration,
        };

        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    public async Task<Hold> UpdateHoldAsync(Guid holdId, double x, double y, double radius)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId)
                   ?? throw new InvalidOperationException("Hold not found");

        hold.X = x;
        hold.Y = y;
        hold.Radius = radius;
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

        db.Holds.Remove(hold);
        await db.SaveChangesAsync();
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
}
