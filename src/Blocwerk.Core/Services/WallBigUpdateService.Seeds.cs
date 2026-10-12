using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Marker seeds for the session's matcher passes (glyph walls only; every helper returns null on any
/// other wall, so the matcher then runs exactly as before).
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Seed for the centre carryover: old holds on the live centre photo vs. the staged centre. The
    /// carryover's left image is the live centre PANEL photo (<see cref="OldCentrePhoto"/>), the frame the
    /// panel's observations and holds are stored in — a crop re-maps both with the photo. A wall without a
    /// live centre panel has no observations to seed from.
    /// </summary>
    private async Task<HoldOverlapSeed?> CentreCarrySeedAsync(
        BlocwerkDbContext db,
        Wall wall,
        IReadOnlyList<Hold> centreOldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        OverlapSeedSide stagedCentre)
    {
        if (!wall.GlyphsEnabled || LiveCentrePanelPhoto(oldPanelPhotosById, panelPositions) is not { } liveCentre)
        {
            return null;
        }

        return await OverlapSeedLoader.LoadAsync(
            db, wall, new OverlapSeedSide(liveCentre.PanelId, false, liveCentre.Photo, centreOldHolds), stagedCentre, logger);
    }

    /// <summary>Seed for one neighbour's own carryover: its old holds on its live photo vs. its staged photo.</summary>
    private async Task<HoldOverlapSeed?> NeighbourCarrySeedAsync(
        BlocwerkDbContext db,
        Wall wall,
        List<Hold>? neighbourOldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        OverlapSeedSide stagedNeighbour)
    {
        if (!wall.GlyphsEnabled
            || neighbourOldHolds is not { Count: > 0 }
            || neighbourOldHolds[0].WallPanelId is not { } oldPanelId
            || !oldPanelPhotosById.TryGetValue(oldPanelId, out var oldPhoto))
        {
            return null;
        }

        return await OverlapSeedLoader.LoadAsync(
            db, wall, new OverlapSeedSide(oldPanelId, false, oldPhoto, neighbourOldHolds), stagedNeighbour, logger);
    }
}
