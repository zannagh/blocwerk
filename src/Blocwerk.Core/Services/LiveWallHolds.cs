using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The one definition of a wall's LIVE panels and holds, shared by the wall view and every service that works
/// on "the wall as it is now" (hold placement, footprints, protrusion), so they cannot drift apart. A panel is
/// live when it is the newest panel with a Photo at its (Col, Row); after a subset promote that spans
/// generations, and the superseded rows a re-shoot leaves behind — with their historic holds — never count.
/// Staged panels carry only a StagedPhoto, so they are not live either.
/// </summary>
internal static class LiveWallHolds
{
    /// <summary>The ids of the wall's live panels: per (Col, Row), the newest panel that has a Photo.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The live panel ids.</returns>
    public static async Task<List<Guid>> LoadPanelIdsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var panels = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
            .ToListAsync(ct);

        // Smallest id breaks a generation tie, so the same wall always resolves a cell to the same panel.
        return panels
            .GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).ThenBy(p => p.Id).First().Id)
            .ToList();
    }

    /// <summary>
    /// The wall's live holds: those on a live panel (not staged ahead of the wall's generation), plus the legacy
    /// centre-photo holds without a panel at the current generation.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The query; callers add tracking and further filters.</returns>
    /// <exception cref="InvalidOperationException">The wall does not exist.</exception>
    public static async Task<IQueryable<Hold>> QueryAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var generation = await db.Walls.Where(w => w.Id == wallId).Select(w => (int?)w.CurrentGeneration).FirstOrDefaultAsync(ct)
                         ?? throw new InvalidOperationException("Wall not found");
        var panels = await LoadPanelIdsAsync(db, wallId, ct);
        return db.Holds.Where(h => h.WallId == wallId
            && ((h.WallPanelId != null && panels.Contains(h.WallPanelId.Value) && h.Generation <= generation)
                || (h.WallPanelId == null && h.Generation == generation)));
    }
}
