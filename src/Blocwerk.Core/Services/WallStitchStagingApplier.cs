using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Writes a finished stitch result into a wall's staged slot: the display pair plus the master
/// paths, camera parameters and curvature block onto the staged columns, and the pipeline's
/// carryover into generation N+1. Lives outside <see cref="WallService"/> because that file already
/// carries the three older staging modes; the stitched mode only shares the confirm/discard tail
/// with them.
/// </summary>
internal static class WallStitchStagingApplier
{
    public const string DisplayContentType = "image/jpeg";

    /// <summary>
    /// How far a carried-over hold may land from where it was predicted, in multiples of its own
    /// radius, before the match stops being worth anything. A match at exactly this distance scores
    /// zero confidence.
    /// </summary>
    private const double MaxMatchDistanceRadii = 2.0;

    /// <summary>Confidence below which a carried-over hold is flagged for review.</summary>
    private const double CarriedOverReviewThreshold = 0.6;

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
        string flatMasterPath,
        string naturalMasterPath,
        string? camerasJson,
        string? curvatureJson)
    {
        var retired = new List<string?> { wall.StagedFlatMasterPath, wall.StagedNaturalMasterPath };

        wall.StagedPhoto = defaultImage;
        wall.StagedPhotoContentType = DisplayContentType;
        wall.StagedPhotoAlternate = alternateImage;
        wall.StagedPhotoAlternateContentType = DisplayContentType;
        wall.StagedPhotoProjection = job.RequestedProjection;
        wall.StagedFlatMasterPath = flatMasterPath;
        wall.StagedNaturalMasterPath = naturalMasterPath;
        wall.StagedPhotoWallWidthM = result.WallWidthM;
        wall.StagedPhotoWallHeightM = result.WallHeightM;
        wall.StagedPhotoCurvatureJson = curvatureJson;
        wall.StagedCamerasJson = camerasJson;
        wall.StagedCarryoverBlocker = Truncate(result.Carryover?.Blocker, 1024);
        wall.StagedAt = DateTimeOffset.UtcNow;
        wall.StagedByUserId = job.RequestedByUserId;
        wall.StagingMode = WallStagingMode.Stitched;

        return retired;
    }

    /// <summary>
    /// Clones the current generation into generation N+1 from the pipeline's carryover lists.
    /// <para>
    /// A hold in <c>carryover.missing</c> is STILL created, at its transferred position and flagged
    /// <see cref="Hold.NeedsReview"/>. Dropping it would orphan every <see cref="BoulderHold"/> that
    /// points at it and silently break the boulders that use it; a flagged hold the admin can delete
    /// deliberately is strictly better than a boulder that quietly loses a move. For the same reason
    /// a live hold that appears in no list at all is carried forward unchanged and flagged, never
    /// dropped. A hold in <c>carryover.new</c> has no source and is created auto-detected and
    /// flagged.
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

        // Radius is normalised against the master's LONGER side, so that is what turns a
        // pixel distance back into a fraction of a hold.
        var longerSidePx = Math.Max(result.FlatMaster.Width, result.FlatMaster.Height);

        var carryover = result.Carryover;
        var carriedOver = 0;
        var carriedOverFlagged = 0;
        var missing = 0;
        var added = 0;
        var placed = new HashSet<Guid>();

        foreach (var reported in carryover?.Carried ?? [])
        {
            var source = liveById.GetValueOrDefault(reported.Id);
            var confidence = ConfidenceFor(reported, longerSidePx);
            var needsReview = NeedsReview(reported, confidence);

            db.Holds.Add(BuildClone(wall.Id, stagedGen, reported, source, confidence, needsReview));
            if (source is not null)
            {
                placed.Add(source.Id);
            }

            carriedOver++;
            if (needsReview)
            {
                carriedOverFlagged++;
            }
        }

        foreach (var reported in carryover?.Missing ?? [])
        {
            var source = liveById.GetValueOrDefault(reported.Id);
            db.Holds.Add(BuildClone(wall.Id, stagedGen, reported, source, confidence: 0.0, needsReview: true));
            if (source is not null)
            {
                placed.Add(source.Id);
            }

            missing++;
        }

        foreach (var detected in carryover?.New ?? [])
        {
            db.Holds.Add(BuildDetected(wall.Id, stagedGen, detected));
            added++;
        }

        var unreported = 0;
        foreach (var source in live.Where(h => !placed.Contains(h.Id)))
        {
            db.Holds.Add(CarryForward(stagedGen, source));
            unreported++;
        }

        return new StitchStagingHoldSummary(carriedOver, missing, added, unreported)
        {
            CarriedOverNeedingReview = carriedOverFlagged,
            Blocker = carryover?.Blocker,
        };
    }

    /// <summary>An existing hold transferred onto the new master, keeping its identity link.</summary>
    private static Hold BuildClone(
        Guid wallId,
        int stagedGen,
        StitchCarriedHold reported,
        Hold? source,
        double confidence,
        bool needsReview)
    {
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
            Confidence = confidence,
            Generation = stagedGen,
            NeedsReview = needsReview,
            AlignmentSourceHoldId = source?.Id,
        };
    }

    /// <summary>A detection no existing hold claimed: no history, no source, always flagged.</summary>
    private static Hold BuildDetected(Guid wallId, int stagedGen, StitchNewHold detected)
    {
        return new Hold
        {
            WallId = wallId,
            X = detected.X,
            Y = detected.Y,
            Radius = detected.Radius,
            Category = HoldCategory.Hand,
            IsAutoDetected = true,
            Confidence = detected.Confidence,
            Generation = stagedGen,
            NeedsReview = true,
            AlignmentSourceHoldId = null,
        };
    }

    /// <summary>A live hold the carryover never mentioned: same geometry, flagged for review.</summary>
    private static Hold CarryForward(int stagedGen, Hold source)
    {
        var clone = source.Clone();
        clone.Id = Guid.NewGuid();
        clone.Generation = stagedGen;
        clone.NeedsReview = true;
        clone.AlignmentSourceHoldId = source.Id;
        return clone;
    }

    /// <summary>
    /// Turns the carryover's <c>matchDistancePx</c> into a 0..1 confidence. The pixel distance is
    /// meaningless on its own — the same 30px is a direct hit on a jug and a miss on a crimp — so it
    /// is first expressed in multiples of the hold's own radius:
    /// <c>radiusPx = radius · longerSidePx</c>, <c>d = matchDistancePx / radiusPx</c>, and
    /// <c>confidence = clamp(1 - d / MaxMatchDistanceRadii, 0, 1)</c>. A match dead on the
    /// prediction scores 1; one two radii away scores 0. No distance at all (or a hold with no
    /// radius to scale by) scores 0, which flags it.
    /// </summary>
    private static double ConfidenceFor(StitchCarriedHold reported, int longerSidePx)
    {
        if (reported.MatchDistancePx is not { } distancePx)
        {
            return 0.0;
        }

        var radiusPx = reported.Radius * longerSidePx;
        if (radiusPx <= 0.0)
        {
            return 0.0;
        }

        var distanceRadii = Math.Abs(distancePx) / radiusPx;
        return Math.Clamp(1.0 - (distanceRadii / MaxMatchDistanceRadii), 0.0, 1.0);
    }

    /// <summary>
    /// A carried-over hold needs a look when the match was weak, when it fell outside the new
    /// master, or when the matcher explicitly disagreed about the colour — that last one usually
    /// means the hold was swapped rather than moved.
    /// </summary>
    private static bool NeedsReview(StitchCarriedHold reported, double confidence) =>
        confidence < CarriedOverReviewThreshold
        || !reported.InFrame
        || reported.ColourAgrees == false;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
