using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Right-side (staged) hold edits made during a whole-wall big update: add, move+resize, delete.
/// Every method is structurally scoped to the staged generation (<c>Wall.CurrentGeneration + 1</c>)
/// of the in-flight update — the same generation the big-update flow stages holds at (see
/// <see cref="WallBigUpdateService"/>). Any staged panel of that update (centre or neighbour) is
/// editable, so the manual touch-up step can correct detections on every panel; a live
/// current-generation hold can never satisfy the generation filter, so these methods remain
/// incapable of mutating live rows. Mutations are gated by <see cref="WallAdminGuard"/>.
/// </summary>
public partial class WallPanelService
{
    /// <inheritdoc/>
    public async Task<Guid> AddStagedHoldAsync(
        Guid wallId,
        Guid panelId,
        double x,
        double y,
        double radius,
        string? color = null,
        HoldCategory? category = null,
        List<ShapePoint>? shapePoints = null,
        HoldMaterial? material = null,
        HoldHandType? handType = null,
        bool needsReview = true)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        var stagedGen = await ResolveStagedGenerationAsync(db, wallId);
        var stagedPanelExists = await db.WallPanels.AnyAsync(p =>
            p.Id == panelId && p.WallId == wallId
            && p.Generation == stagedGen && p.StagedPhoto != null);
        if (!stagedPanelExists)
        {
            throw new InvalidOperationException("Staged holds can only be added to a staged panel of the in-flight update.");
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
            Generation = stagedGen,
            IsAutoDetected = false,
            NeedsReview = needsReview,
        };
        if (category is not null)
        {
            hold.Category = category.Value;
        }

        db.Holds.Add(hold);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Staged hold {HoldId} added to panel {PanelId} on wall {WallId} (gen {Gen}, needsReview {NeedsReview}) by {UserId}",
            hold.Id, panelId, wallId, stagedGen, needsReview, user.Id);
        return hold.Id;
    }

    /// <inheritdoc/>
    public async Task UpdateStagedHoldAsync(Guid wallId, Guid holdId, double x, double y, double radius)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        var hold = await LoadStagedHoldAsync(db, wallId, holdId);
        hold.X = Math.Clamp(x, 0, 1);
        hold.Y = Math.Clamp(y, 0, 1);
        hold.Radius = Math.Clamp(radius, 0.003, 0.2);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Staged hold {HoldId} moved/resized on wall {WallId} by {UserId}", holdId, wallId, user.Id);
    }

    /// <inheritdoc/>
    public async Task DeleteStagedHoldAsync(Guid wallId, Guid holdId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        // Staged rows carry no boulders, so no boulder rescue is required before removal.
        var hold = await LoadStagedHoldAsync(db, wallId, holdId);
        db.Holds.Remove(hold);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Staged hold {HoldId} deleted on wall {WallId} by {UserId}", holdId, wallId, user.Id);
    }

    /// <summary>
    /// Loads a hold filtered to the staged generation of the in-flight update. Because the filter
    /// pins <c>Generation == stagedGen</c> (<c>Wall.CurrentGeneration + 1</c>) and staged-generation
    /// holds only ever live on staged panels, a live current-generation hold can never be returned —
    /// the update/delete paths are structurally unable to touch live rows regardless of which staged
    /// panel the hold sits on. Throws when no such staged hold exists.
    /// </summary>
    private static async Task<Hold> LoadStagedHoldAsync(BlocwerkDbContext db, Guid wallId, Guid holdId)
    {
        var stagedGen = await ResolveStagedGenerationAsync(db, wallId);
        var hold = await db.Holds.FirstOrDefaultAsync(h =>
            h.Id == holdId
            && h.WallId == wallId
            && h.Generation == stagedGen);
        if (hold is null)
        {
            throw new InvalidOperationException("Hold is not an editable staged hold.");
        }

        return hold;
    }

    /// <summary>
    /// The in-flight big update's staged generation (<c>Wall.CurrentGeneration + 1</c>). Requires an
    /// in-flight update to exist (a staged centre panel), so all staged edits are gated to a live
    /// update the same way <see cref="ResolveStagedCenterAsync"/> is.
    /// </summary>
    private static async Task<int> ResolveStagedGenerationAsync(BlocwerkDbContext db, Guid wallId)
    {
        var (_, stagedGen, _) = await ResolveStagedCenterAsync(db, wallId);
        return stagedGen;
    }

    /// <summary>
    /// Resolves the in-flight big update's staged generation (<c>Wall.CurrentGeneration + 1</c>) and
    /// its staged centre panel — the (0,0) panel at that generation carrying a staged photo, matching
    /// how <see cref="WallBigUpdateService"/> stages and resumes. Throws when the wall is missing or
    /// no in-flight update exists.
    /// </summary>
    private static async Task<(Guid WallId, int StagedGen, Guid CenterPanelId)> ResolveStagedCenterAsync(
        BlocwerkDbContext db, Guid wallId)
    {
        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        var stagedGen = wall.CurrentGeneration + 1;
        var centerPanel = await db.WallPanels.FirstOrDefaultAsync(p =>
            p.WallId == wallId && p.Col == 0 && p.Row == 0
            && p.Generation == stagedGen && p.StagedPhoto != null)
            ?? throw new InvalidOperationException("No in-flight big update to edit.");

        return (wallId, stagedGen, centerPanel.Id);
    }
}
