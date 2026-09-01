using Blocwerk.Core.Data;
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
        // Per-panel hold add is hold-editing, reached by the normal per-panel editor on multi-image
        // walls, so moderators may use it. Admins/owners still pass. The admin-only big-wall staging
        // session is separately gated at the WallBigUpdateService level.
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

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

    /// <inheritdoc/>
    public async Task CreateHoldLinkAsync(Guid wallId, Guid holdAId, Guid holdBId)
    {
        if (holdAId == holdBId)
        {
            throw new InvalidOperationException("A hold cannot be linked to itself.");
        }

        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        // Both holds must exist on this wall — and, to be a cross-panel seam link, sit on two
        // different panels. One query, then validate before touching anything.
        var holds = await db.Holds
            .Where(h => (h.Id == holdAId || h.Id == holdBId) && h.WallId == wallId)
            .Select(h => new { h.Id, h.WallPanelId })
            .ToListAsync();
        if (holds.Count != 2)
        {
            throw new InvalidOperationException("Both holds must exist on this wall.");
        }

        if (holds[0].WallPanelId is null
            || holds[1].WallPanelId is null
            || holds[0].WallPanelId == holds[1].WallPanelId)
        {
            throw new InvalidOperationException("Linked holds must sit on two different panels.");
        }

        // Dedupe on the unordered pair (mirrors ConfirmPanelAsync): re-linking is a no-op.
        var existing = await db.HoldLinks
            .Where(l => l.WallId == wallId)
            .Select(l => new { l.HoldAId, l.HoldBId })
            .ToListAsync();
        var key = Unordered(holdAId, holdBId);
        if (existing.Any(e => Unordered(e.HoldAId, e.HoldBId) == key))
        {
            return;
        }

        db.HoldLinks.Add(new HoldLink
        {
            WallId = wallId,
            HoldAId = holdAId,
            HoldBId = holdBId,
            Kind = HoldLinkKind.Same,
            CreatedByUserId = user.Id,
        });
        await db.SaveChangesAsync();

        // Best-effort: the link itself is committed above; a failure copying appearance must not surface
        // link creation as failed — the startup backfill reconciles it.
        try
        {
            await CopyAppearanceFromMoreCentralAsync(db, wallId, holdAId, holdBId);
        }
        catch (Exception copyEx)
        {
            logger.LogWarning(copyEx, "Failed to copy appearance across new hold link {HoldA} <-> {HoldB}; the backfill will reconcile it.", holdAId, holdBId);
        }

        logger.LogInformation(
            "Hold link {HoldA} <-> {HoldB} created on wall {WallId} by {UserId}",
            holdAId, holdBId, wallId, user.Id);
    }

    // A freshly linked hold inherits the more-central endpoint's appearance immediately, so the two
    // copies of the physical hold match without waiting for the backfill. Centre wins (deterministic
    // centrality → Col → Row → Id). Pairwise from the more-central endpoint is enough at link time;
    // the backfill and live-edit sync converge anything transitive. Appearance fields only.
    private static async Task CopyAppearanceFromMoreCentralAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid holdAId,
        Guid holdBId)
    {
        var linked = await db.Holds
            .Where(h => (h.Id == holdAId || h.Id == holdBId) && h.WallId == wallId)
            .ToListAsync();
        if (linked.Count != 2)
        {
            return;
        }

        var panelById = await LoadPanelPositionsAsync(db, linked);

        HoldCentrality Rank(Hold hold)
        {
            if (hold.WallPanelId is { } panelId && panelById.TryGetValue(panelId, out var pos))
            {
                return new HoldCentrality(hold.Id, pos.Col, pos.Row);
            }

            return new HoldCentrality(hold.Id, null, null);
        }

        var sourceId = HoldPropertySync.MostCentral(linked.Select(Rank)).HoldId;
        var source = linked.First(h => h.Id == sourceId);
        var target = linked.First(h => h.Id != sourceId);

        if (HoldPropertySync.CopyAppearance(source, target))
        {
            await db.SaveChangesAsync();
        }
    }

    private static async Task<Dictionary<Guid, (int Col, int Row)>> LoadPanelPositionsAsync(
        BlocwerkDbContext db,
        IEnumerable<Hold> holds)
    {
        var panelIds = holds
            .Where(h => h.WallPanelId is not null)
            .Select(h => h.WallPanelId!.Value)
            .Distinct()
            .ToList();
        if (panelIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        var panels = await db.WallPanels
            .Where(p => panelIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();
        return panels.ToDictionary(p => p.Id, p => (p.Col, p.Row));
    }

    /// <inheritdoc/>
    public async Task DeleteHoldLinkAsync(Guid wallId, Guid holdAId, Guid holdBId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        // Match the unordered pair within this wall; a no-op when absent keeps breaking idempotent.
        var links = await db.HoldLinks
            .Where(l => l.WallId == wallId
                && ((l.HoldAId == holdAId && l.HoldBId == holdBId)
                    || (l.HoldAId == holdBId && l.HoldBId == holdAId)))
            .ToListAsync();
        if (links.Count == 0)
        {
            return;
        }

        db.HoldLinks.RemoveRange(links);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Hold link {HoldA} <-> {HoldB} removed on wall {WallId} by {UserId}",
            holdAId, holdBId, wallId, user.Id);
    }
}
