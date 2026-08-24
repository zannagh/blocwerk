using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Writes a finished stitch result into a wall's staged slot: the display pair plus the master
/// paths onto the staged columns, and the sidecar's transferred holds into generation N+1.
/// Lives outside <see cref="WallService"/> because that file already carries the three older
/// staging modes; the stitched mode only shares the confirm/discard tail with them.
/// </summary>
internal static class WallStitchStagingApplier
{
    public const string DisplayContentType = "image/jpeg";

    /// <summary>
    /// Moves the result's photo data onto the staged columns and returns the master paths of a
    /// staged slot that was already occupied, so the caller can delete those files once they are
    /// no longer referenced.
    /// </summary>
    public static IReadOnlyList<string?> ApplyPhoto(
        Wall wall,
        WallStitchJob job,
        StitchJobResult result,
        byte[] defaultImage,
        byte[] alternateImage,
        string orthoMasterPath,
        string angledMasterPath)
    {
        var retired = new List<string?> { wall.StagedOrthoMasterPath, wall.StagedAngledMasterPath };

        wall.StagedPhoto = defaultImage;
        wall.StagedPhotoContentType = DisplayContentType;
        wall.StagedPhotoAlternate = alternateImage;
        wall.StagedPhotoAlternateContentType = DisplayContentType;
        wall.StagedPhotoProjection = job.RequestedProjection;
        wall.StagedOrthoMasterPath = orthoMasterPath;
        wall.StagedAngledMasterPath = angledMasterPath;
        wall.StagedPhotoWallAngleDegrees = result.WallAngleDegrees;
        wall.StagedPhotoVerticalScale = result.VerticalScale;
        wall.StagedAt = DateTimeOffset.UtcNow;
        wall.StagedByUserId = job.RequestedByUserId;
        wall.StagingMode = WallStagingMode.Stitched;

        return retired;
    }

    /// <summary>
    /// Clones the current generation into generation N+1 using the sidecar's placements.
    /// <para>
    /// A hold the sidecar classified as <c>missing</c> is STILL created, at its predicted position
    /// and flagged <see cref="Hold.NeedsReview"/>. Dropping it would orphan every
    /// <see cref="BoulderHold"/> that points at it and silently break the boulders that use it;
    /// a flagged hold the admin can delete deliberately is strictly better than a boulder that
    /// quietly loses a move. For the same reason a live hold the sidecar did not report at all is
    /// carried forward unchanged and flagged, never dropped.
    /// </para>
    /// </summary>
    public static async Task<StitchStagingHoldSummary> CloneHoldsAsync(
        BlocwerkDbContext db,
        Wall wall,
        StitchJobResult result,
        CancellationToken ct)
    {
        var liveGen = wall.CurrentGeneration;
        var stagedGen = liveGen + 1;

        var occupying = await db.Holds
            .Where(h => h.WallId == wall.Id && h.Generation == stagedGen)
            .ToListAsync(ct);
        db.Holds.RemoveRange(occupying);

        var live = await db.Holds
            .Where(h => h.WallId == wall.Id && h.Generation == liveGen)
            .ToListAsync(ct);
        var liveById = live.ToDictionary(h => h.Id);

        var matched = 0;
        var uncertain = 0;
        var missing = 0;
        var placed = new HashSet<Guid>();

        foreach (var reported in result.Holds ?? [])
        {
            var source = liveById.GetValueOrDefault(reported.Id);
            db.Holds.Add(BuildClone(wall.Id, stagedGen, reported, source));
            if (source is not null)
            {
                placed.Add(source.Id);
            }

            switch (Classify(reported.Classification))
            {
                case StitchHoldClass.Matched:
                    matched++;
                    break;
                case StitchHoldClass.Uncertain:
                    uncertain++;
                    break;
                default:
                    missing++;
                    break;
            }
        }

        var unreported = 0;
        foreach (var source in live.Where(h => !placed.Contains(h.Id)))
        {
            db.Holds.Add(CarryForward(stagedGen, source));
            unreported++;
        }

        return new StitchStagingHoldSummary(matched, uncertain, missing, unreported);
    }

    private static Hold BuildClone(Guid wallId, int stagedGen, StitchResultHold reported, Hold? source)
    {
        var classification = Classify(reported.Classification);
        return new Hold
        {
            WallId = wallId,
            X = reported.X,
            Y = reported.Y,
            Radius = reported.Radius,
            ShapePoints = reported.ShapePoints?
                .Select(sp => new ShapePoint { Dx = sp.Dx, Dy = sp.Dy })
                .ToList(),
            Name = source?.Name,
            Color = source?.Color,
            Material = source?.Material,
            Category = source?.Category ?? HoldCategory.Hand,
            IsOnKickboard = source?.IsOnKickboard ?? false,
            IsVirtual = source?.IsVirtual ?? false,
            IsAutoDetected = source is null,
            Confidence = reported.Confidence,
            Generation = stagedGen,
            NeedsReview = classification != StitchHoldClass.Matched,
            AlignmentSourceHoldId = source?.Id,
        };
    }

    /// <summary>A live hold the sidecar never mentioned: same geometry, flagged for review.</summary>
    private static Hold CarryForward(int stagedGen, Hold source)
    {
        var clone = source.Clone();
        clone.Id = Guid.NewGuid();
        clone.Generation = stagedGen;
        clone.NeedsReview = true;
        clone.AlignmentSourceHoldId = source.Id;
        return clone;
    }

    private static StitchHoldClass Classify(string? classification) =>
        classification?.ToLowerInvariant() switch
        {
            "matched" => StitchHoldClass.Matched,
            "uncertain" => StitchHoldClass.Uncertain,
            _ => StitchHoldClass.Missing,
        };
}
