using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The confirm and discard tail of the stitched staging mode: moving the staged photo pair onto
/// the live columns, promoting the staged holds without breaking boulder links, and clearing the
/// staged slot again. Kept next to <see cref="WallStitchStagingApplier"/> rather than inside
/// <see cref="WallService"/> so the three older staging modes stay exactly as they were.
/// </summary>
internal static class WallStitchStagingPromotion
{
    /// <summary>
    /// Moves the staged photo pair, projection, master paths, angle and vertical scale onto the
    /// live columns and clears the staged ones. Returns the master paths of the photo just
    /// retired, so the caller can delete those files once nothing references them.
    /// </summary>
    public static IReadOnlyList<string?> PromotePhoto(Wall wall)
    {
        var retired = new List<string?> { wall.OrthoMasterPath, wall.AngledMasterPath };

        wall.Photo = wall.StagedPhoto;
        wall.PhotoContentType = wall.StagedPhotoContentType;
        wall.PhotoAlternate = wall.StagedPhotoAlternate;
        wall.PhotoAlternateContentType = wall.StagedPhotoAlternateContentType;
        wall.PhotoProjection = wall.StagedPhotoProjection;
        wall.OrthoMasterPath = wall.StagedOrthoMasterPath;
        wall.AngledMasterPath = wall.StagedAngledMasterPath;
        wall.PhotoWallAngleDegrees = wall.StagedPhotoWallAngleDegrees;
        wall.PhotoVerticalScale = wall.StagedPhotoVerticalScale;

        ClearStagedPhotoColumns(wall);
        return retired;
    }

    /// <summary>
    /// Clears the stitch-specific staged columns and returns the staged master paths, so a
    /// discarded staging leaves no file behind. A no-op for the older staging modes, which never
    /// populate these columns.
    /// </summary>
    public static IReadOnlyList<string?> ClearStagedStitchColumns(Wall wall)
    {
        var staged = new List<string?> { wall.StagedOrthoMasterPath, wall.StagedAngledMasterPath };
        ClearStagedPhotoColumns(wall);
        return staged;
    }

    /// <summary>
    /// Promotes generation N+1 onto the live holds. A staged clone that came from a live hold is
    /// written back onto that hold and then dropped, so the hold id — and with it every
    /// <see cref="BoulderHold"/> link — survives the update. Boulders are never marked historic
    /// here; that is the recreate flow's job.
    /// </summary>
    public static async Task<(int Promoted, int Added, int Flagged)> PromoteHoldsAsync(
        BlocwerkDbContext db,
        Wall wall,
        CancellationToken ct = default)
    {
        var liveGen = wall.CurrentGeneration;
        var stagedGen = liveGen + 1;

        var holds = await db.Holds
            .Where(h => h.WallId == wall.Id && (h.Generation == liveGen || h.Generation == stagedGen))
            .ToListAsync(ct);

        var liveById = holds.Where(h => h.Generation == liveGen).ToDictionary(h => h.Id);
        var promoted = 0;
        var added = 0;
        var flagged = 0;

        foreach (var clone in holds.Where(h => h.Generation == stagedGen).ToList())
        {
            if (clone.NeedsReview)
            {
                flagged++;
            }

            if (clone.AlignmentSourceHoldId is { } sourceId && liveById.TryGetValue(sourceId, out var source))
            {
                CopyOnto(source, clone);
                source.Generation = stagedGen;
                liveById.Remove(sourceId);
                db.Holds.Remove(clone);
                promoted++;
                continue;
            }

            clone.AlignmentSourceHoldId = null;
            added++;
        }

        // Safety net: a live hold whose clone was deleted during review still has to move up,
        // otherwise it drops out of the current generation and its boulders lose a move.
        foreach (var orphaned in liveById.Values)
        {
            orphaned.Generation = stagedGen;
        }

        return (promoted, added, flagged);
    }

    private static void CopyOnto(Hold source, Hold clone)
    {
        source.X = clone.X;
        source.Y = clone.Y;
        source.Radius = clone.Radius;
        source.ShapePoints = clone.ShapePoints?
            .Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy })
            .ToList();
        source.Name = clone.Name;
        source.Color = clone.Color;
        source.Material = clone.Material;
        source.Category = clone.Category;
        source.IsOnKickboard = clone.IsOnKickboard;
        source.IsVirtual = clone.IsVirtual;
        source.Confidence = clone.Confidence;
        source.NeedsReview = clone.NeedsReview;
    }

    private static void ClearStagedPhotoColumns(Wall wall)
    {
        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedPhotoAlternate = null;
        wall.StagedPhotoAlternateContentType = null;
        wall.StagedPhotoProjection = WallPhotoProjection.Angled;
        wall.StagedOrthoMasterPath = null;
        wall.StagedAngledMasterPath = null;
        wall.StagedPhotoWallAngleDegrees = null;
        wall.StagedPhotoVerticalScale = null;
    }
}
