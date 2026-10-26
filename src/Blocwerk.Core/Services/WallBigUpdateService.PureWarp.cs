// <copyright file="WallBigUpdateService.PureWarp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="HoldMoveOptions.AssumeNothingMoved"/> at promote: the person asserted that holds were only added, so a carried
/// old hold keeps exactly what the photo change does to it (the pure warp), found again or not. The detection only confirms
/// it is still there; its own centre, radius and contour are jitter and re-outlines, not news about the hold.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>The photo side assumed when neither the old nor the new photo of a panel decodes (only the ratio matters then).</summary>
    private const int NominalPhotoSide = 4000;

    /// <summary>
    /// The pure-warp radius of every old hold the warp predicts a spot for, by old hold id; null when the option is off.
    /// <para>
    /// A radius is normalized to the LONGEST side of its photo while X and Y are normalized per side, and the new photo can
    /// differ in size and aspect (The Attic: 4015x2292 old, 4032x3024 new). So the scale is taken in pixels: per panel, an
    /// affine is fitted to the nearest old → warped centre pairs around the hold (<see cref="LocalAffine.AreaScale"/>, the
    /// same local neighbourhood the warp field itself is built from) and its area scale s gives
    /// r_new = r_old · maxSide_old · s / maxSide_new. One method for circles and outlines alike: the warped outline already
    /// carries the scale for an outlined hold, and its radius is only a stale fallback there. A photo that does not decode
    /// takes the other photo's size (same size assumed); a fit with fewer than three pairs keeps s = 1.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, double>?> PureWarpRadiiAsync(
        BlocwerkDbContext db,
        IReadOnlyDictionary<Guid, Hold> oldHolds,
        IReadOnlyDictionary<Guid, HoldPositionNorm>? warpPositions,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlyDictionary<(int Col, int Row), Guid> newGenPanelByPosition,
        IReadOnlyDictionary<Guid, (int Width, int Height)> newSizes)
    {
        if (!moveOptions.AssumeNothingMoved || warpPositions is null)
        {
            return null;
        }

        // A virtual hold is not on the photo: it keeps today's carry.
        var warped = oldHolds.Values.Where(h => !h.IsVirtual && warpPositions.ContainsKey(h.Id)).ToList();
        var oldSizes = await LoadOldPhotoSizesAsync(db, warped, panelPositions);
        var radii = new Dictionary<Guid, double>();
        foreach (var group in warped.GroupBy(h => PositionOf(h, panelPositions)))
        {
            var newSize = newGenPanelByPosition.TryGetValue(group.Key, out var panelId) && newSizes.TryGetValue(panelId, out var n)
                ? n
                : ((int Width, int Height)?)null;
            var oldSize = oldSizes.TryGetValue(group.Key, out var o) ? o : ((int Width, int Height)?)null;
            var from = oldSize ?? newSize ?? (NominalPhotoSide, NominalPhotoSide);
            var to = newSize ?? oldSize ?? (NominalPhotoSide, NominalPhotoSide);
            var pairs = group
                .Select(h => new PointPair(h.X * from.Width, h.Y * from.Height, warpPositions[h.Id].X * to.Width, warpPositions[h.Id].Y * to.Height))
                .ToList();
            foreach (var hold in group)
            {
                var fitted = LocalAffine.AreaScale(pairs, hold.X * from.Width, hold.Y * from.Height);
                var scale = fitted is { } s && double.IsFinite(s) && s > 0 ? s : 1;
                radii[hold.Id] = hold.Radius * Math.Max(from.Width, from.Height) * scale / Math.Max(to.Width, to.Height);
            }
        }

        return radii;
    }

    /// <summary>
    /// Gives the successor the old hold's pure-warp geometry when the option is on and the warp predicts a spot for it: the
    /// warped centre, the scaled radius and the old hold's own outline fields (a circle stays a circle). The outline itself
    /// is then warped by <see cref="ApplyWarpedShape"/>, which rebases it on this centre. A hold a person said moved
    /// (<paramref name="changed"/>) is where its detection is, and is left alone. Returns whether it applied.
    /// </summary>
    private static bool ApplyPureWarp(
        Hold successor,
        Hold oldHold,
        bool changed,
        IReadOnlyDictionary<Guid, HoldPositionNorm>? warpPositions,
        IReadOnlyDictionary<Guid, double>? warpRadii)
    {
        if (changed || warpRadii is null || !warpRadii.TryGetValue(oldHold.Id, out var radius)
            || warpPositions is null || !warpPositions.TryGetValue(oldHold.Id, out var at))
        {
            return false;
        }

        successor.X = at.X;
        successor.Y = at.Y;
        successor.Radius = radius;
        successor.ShapePoints = oldHold.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        successor.ShapeControlPoints = oldHold.ShapeControlPoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        successor.ShapeHoles = ShapePoint.CloneRings(oldHold.ShapeHoles);
        successor.OutlineSource = oldHold.OutlineSource;
        successor.OutlineConfidence = oldHold.OutlineConfidence;
        return true;
    }

    /// <summary>The grid position of an old hold's panel; a legacy hold without a panel is on the centre.</summary>
    private static (int Col, int Row) PositionOf(Hold hold, IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions) =>
        hold.WallPanelId is { } id && panelPositions.TryGetValue(id, out var pos) ? pos : (0, 0);

    /// <summary>The raw pixel size of the live (old) photo at each grid position the given holds are on.</summary>
    private static async Task<Dictionary<(int Col, int Row), (int Width, int Height)>> LoadOldPhotoSizesAsync(
        BlocwerkDbContext db, IEnumerable<Hold> holds, IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions)
    {
        var panelIds = holds.Where(h => h.WallPanelId is not null).Select(h => h.WallPanelId!.Value).Distinct().ToList();
        var sizes = new Dictionary<(int Col, int Row), (int Width, int Height)>();
        foreach (var (id, photo) in await LoadPanelPhotosAsync(db, panelIds))
        {
            if (panelPositions.TryGetValue(id, out var pos) && OverlapSeedLoader.RawSize(photo) is { } size)
            {
                sizes[pos] = size;
            }
        }

        return sizes;
    }
}
