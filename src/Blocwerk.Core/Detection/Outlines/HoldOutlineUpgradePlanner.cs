using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Decides, per existing circle hold on one decoded photo, whether the outliner's result may replace the
/// circle. Pure: no database, no writes — the upgrade service's dry run and apply both start here, and so
/// does any offline measurement.
/// </summary>
/// <remarks>
/// <para><b>Eligible</b>: not virtual, no polygon yet (null or empty <see cref="Hold.ShapePoints"/>), and
/// auto-detected unless manual holds are included.</para>
/// <para><b>Seeds</b>: an auto-detected hold is seeded exactly as at ingest (its detector radius). A manual
/// hold's radius is whatever the editor left behind — often the 0.003 placeholder — so it is raised to
/// <see cref="MinManualSeedRadius"/>, and because that seed is a guess its result must also pass a leak
/// check (<see cref="LooksLikeLeak"/>).</para>
/// </remarks>
public static class HoldOutlineUpgradePlanner
{
    /// <summary>Smallest seed radius for a manual hold (normalized by the image's longer side, ≈ 32 px at 4032 px).</summary>
    public const double MinManualSeedRadius = 0.008;

    /// <summary>A manual hold's outline below this confidence is not trusted.</summary>
    public const double MinManualConfidence = 0.5;

    /// <summary>A manual hold's outline larger than this share of its (raised) seed disc is treated as a leak.</summary>
    public const double MaxManualAreaRatio = 1.3;

    /// <summary>True when the hold is a circle this action may upgrade.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="includeManual">Whether manually placed holds are in scope.</param>
    /// <returns>Whether the hold is eligible.</returns>
    public static bool IsEligible(Hold hold, bool includeManual) =>
        !hold.IsVirtual
        && hold.ShapePoints is not { Count: > 0 }
        && (hold.IsAutoDetected || includeManual);

    /// <summary>The seed the outliner gets for a hold (see the remarks on manual holds).</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The seed; never moves the centre.</returns>
    public static HoldSeed SeedFor(Hold hold) =>
        new(hold.X, hold.Y, hold.IsAutoDetected ? hold.Radius : Math.Max(hold.Radius, MinManualSeedRadius));

    /// <summary>Outlines every eligible hold on the session's photo and classifies the result.</summary>
    /// <param name="session">The decoded photo.</param>
    /// <param name="holds">The photo's live holds (ineligible ones are ignored).</param>
    /// <param name="includeManual">Whether manually placed holds are in scope.</param>
    /// <returns>One proposal per eligible hold, in input order.</returns>
    public static List<HoldOutlineUpgradeProposal> Plan(IHoldOutlineSession session, IEnumerable<Hold> holds, bool includeManual)
    {
        ArgumentNullException.ThrowIfNull(session);
        var proposals = new List<HoldOutlineUpgradeProposal>();
        foreach (var hold in holds.Where(h => IsEligible(h, includeManual)))
        {
            var seed = SeedFor(hold);
            var outline = session.Outline(seed);
            var outcome = Classify(hold, outline, seed, session.ImageWidth, session.ImageHeight);
            proposals.Add(new HoldOutlineUpgradeProposal(hold, outcome, outline));
        }

        return proposals;
    }

    /// <summary>Classifies one outline result for one hold.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="outline">The outliner's result.</param>
    /// <param name="seed">The seed it was run with.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The outcome.</returns>
    public static HoldOutlineUpgradeOutcome Classify(Hold hold, HoldOutlineResult outline, HoldSeed seed, int width, int height)
    {
        if (outline.Method == HoldOutlineMethod.CircleFallback || outline.ShapePoints is not { Count: >= 3 })
        {
            return HoldOutlineUpgradeOutcome.KeepCircle;
        }

        return !hold.IsAutoDetected && LooksLikeLeak(hold, outline, seed, width, height)
            ? HoldOutlineUpgradeOutcome.RejectedLeak
            : HoldOutlineUpgradeOutcome.Outline;
    }

    /// <summary>
    /// The extra guard for a guessed (manual) seed: low confidence, an outline near the outliner's own size
    /// ceiling, or an outline that does not even contain the hold's centre.
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <param name="outline">The outline.</param>
    /// <param name="seed">The seed used.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>True when the outline should not be used.</returns>
    public static bool LooksLikeLeak(Hold hold, HoldOutlineResult outline, HoldSeed seed, int width, int height)
    {
        if (outline.Confidence < MinManualConfidence)
        {
            return true;
        }

        double radiusPx = seed.Radius * Math.Max(width, height);
        double seedArea = Math.PI * radiusPx * radiusPx;
        if (seedArea > 0 && outline.AreaPx / seedArea > MaxManualAreaRatio)
        {
            return true;
        }

        return !Contains(outline.Polygon, hold.X, hold.Y);
    }

    /// <summary>Even-odd point-in-polygon test in normalized image space.</summary>
    private static bool Contains(IReadOnlyList<NormalizedPoint> polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
