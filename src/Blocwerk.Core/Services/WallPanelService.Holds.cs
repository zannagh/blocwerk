using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// User-placed hold edits made during big-wall overlap confirmation, for holds the matcher
/// missed. Mutations are gated by <see cref="WallAdminGuard"/>.
/// </summary>
public partial class WallPanelService
{
    /// <inheritdoc/>
    public async Task<Guid> AddPanelHoldAsync(
        Guid wallId,
        Guid panelId,
        double x,
        double y,
        double radius,
        string? color = null,
        HoldCategory? category = null,
        List<ShapePoint>? shapePoints = null,
        HoldMaterial? material = null,
        HoldHandType? handType = null)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
        if (wall is null)
        {
            throw new InvalidOperationException("Wall not found");
        }

        var panelExists = await db.WallPanels.AnyAsync(p => p.Id == panelId && p.WallId == wallId);
        if (!panelExists)
        {
            throw new InvalidOperationException("Panel not found");
        }

        var hold = new Hold
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
            Radius = Math.Clamp(radius, 0.003, 0.2),
            Color = color,
            ShapePoints = shapePoints,
            Material = material,
            HandType = handType,
            Generation = wall.CurrentGeneration,
            IsAutoDetected = false,
            NeedsReview = true,
        };
        if (category is not null)
        {
            hold.Category = category.Value;
        }
        db.Holds.Add(hold);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Hold {HoldId} manually added to panel {PanelId} on wall {WallId} by {UserId}",
            hold.Id, panelId, wallId, user.Id);
        return hold.Id;
    }
}
