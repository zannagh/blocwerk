using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

public interface IBoulderService
{
    Task<Boulder> CreateBoulderAsync(Guid wallId, string name, string? grade, List<BoulderHoldInput> holds, FootholdMode footholdMode = FootholdMode.AllKickboard);

    Task<Boulder?> GetBoulderAsync(Guid boulderId);

    Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false);

    Task<Boulder> UpdateBoulderAsync(Guid boulderId, string name, string? grade, List<BoulderHoldInput>? holds = null);

    Task DeleteBoulderAsync(Guid boulderId);
}

public record BoulderHoldInput(Guid HoldId, HoldType Type = HoldType.Normal);

public class BoulderService : IBoulderService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;

    public BoulderService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ICurrentUserService currentUserService)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
    }

    public async Task<Boulder> CreateBoulderAsync(Guid wallId, string name, string? grade, List<BoulderHoldInput> holds, FootholdMode footholdMode = FootholdMode.AllKickboard)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
                   ?? throw new InvalidOperationException("Wall not found");

        var boulder = new Boulder
        {
            WallId = wallId,
            Name = name,
            Grade = grade,
            CreatedByUserId = user.Id,
            Generation = wall.CurrentGeneration,
            FootholdMode = footholdMode,
        };

        db.Boulders.Add(boulder);

        foreach (var h in holds)
        {
            db.BoulderHolds.Add(new BoulderHold
            {
                BoulderId = boulder.Id,
                HoldId = h.HoldId,
                Type = h.Type,
            });
        }

        await db.SaveChangesAsync();
        return boulder;
    }

    public async Task<Boulder?> GetBoulderAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        return await db.Boulders
            .Include(b => b.BoulderHolds).ThenInclude(bh => bh.Hold)
            .Include(b => b.Attempts.OrderByDescending(a => a.Timestamp))
            .Include(b => b.CreatedBy)
            .Include(b => b.Wall)
            .FirstOrDefaultAsync(b => b.Id == boulderId);
    }

    public async Task<List<Boulder>> GetBouldersForWallAsync(Guid wallId, bool includeArchived = false)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var query = db.Boulders
            .Include(b => b.BoulderHolds)
            .Include(b => b.Attempts)
            .Include(b => b.CreatedBy)
            .Where(b => b.WallId == wallId);

        if (!includeArchived)
        {
            query = query.Where(b => !b.IsArchived);
        }

        return await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
    }

    public async Task<Boulder> UpdateBoulderAsync(Guid boulderId, string name, string? grade, List<BoulderHoldInput>? holds = null)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders
                          .Include(b => b.BoulderHolds)
                          .FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        boulder.Name = name;
        boulder.Grade = grade;

        if (holds != null)
        {
            db.BoulderHolds.RemoveRange(boulder.BoulderHolds);
            foreach (var h in holds)
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = boulderId,
                    HoldId = h.HoldId,
                    Type = h.Type,
                });
            }
        }

        await db.SaveChangesAsync();
        return boulder;
    }

    public async Task DeleteBoulderAsync(Guid boulderId)
    {
        var user = await _currentUserService.GetCurrentUserAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId)
                      ?? throw new InvalidOperationException("Boulder not found");

        db.Boulders.Remove(boulder);
        await db.SaveChangesAsync();
    }
}
