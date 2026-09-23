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
    /// carryover's left image is <see cref="Wall.Photo"/>; observations are stored per PANEL photo, so the
    /// seed is only used when the live centre panel's photo is byte-identical to it (same raw frame).
    /// </summary>
    private async Task<HoldOverlapSeed?> CentreCarrySeedAsync(
        BlocwerkDbContext db,
        Wall wall,
        IReadOnlyList<Hold> centreOldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        OverlapSeedSide stagedCentre)
    {
        if (!wall.GlyphsEnabled || wall.Photo is null)
        {
            return null;
        }

        var liveCentre = oldPanelPhotosById
            .Where(kv => panelPositions.TryGetValue(kv.Key, out var pos) && pos == (0, 0))
            .Select(kv => (Id: kv.Key, Photo: kv.Value))
            .FirstOrDefault(p => p.Photo.AsSpan().SequenceEqual(wall.Photo));
        if (liveCentre.Photo is null)
        {
            return null;
        }

        return await OverlapSeedLoader.LoadAsync(
            db, wall, new OverlapSeedSide(liveCentre.Id, false, wall.Photo, centreOldHolds), stagedCentre, logger);
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
