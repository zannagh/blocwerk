// <copyright file="HoldRelocationProposer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>
/// Decides which "this hold moved" pairs a wall update may SUGGEST, on top of the position-free
/// <see cref="HoldRelocationMatcher"/>. Pure: holds and fingerprints in, pairs out.
/// <para>
/// Two confidence tiers, because a fingerprint without sizes is easily fooled — two DIFFERENT green holds
/// were measured at 0.86, the same score one hold gets across two photos:
/// <list type="bullet">
/// <item><b>Metric</b> — a marker wall where BOTH fingerprints carry millimetre sizes: the matcher's own
/// thresholds (<see cref="HoldRelocationMatcher.DefaultMinScore"/>, margin
/// <see cref="HoldRelocationMatcher.DefaultMinMargin"/>) apply, since size separates look-alikes.</item>
/// <item><b>Look-alike-prone</b> — every other pair: it must reach <see cref="LookAlikeMinScore"/> with a
/// margin of <see cref="LookAlikeMinMargin"/>, and is labelled lower-confidence in the review.</item>
/// </list>
/// On a marker wall a hold is also only suggested where it could plausibly be: a staged hold the marker
/// pass MEASURED but could place on no facet lies off the wall (crash mat, floor), and a hold does not move
/// there. Distance is deliberately not a criterion — a moved hold can be anywhere on the wall.
/// </para>
/// </summary>
public static class HoldRelocationProposer
{
    /// <summary>Minimum similarity for a size-free (look-alike-prone) pair.</summary>
    public const double LookAlikeMinScore = 0.90;

    /// <summary>Minimum lead over the best competing pair for a size-free pair.</summary>
    public const double LookAlikeMinMargin = 0.08;

    private static readonly HoldRelocationMatcher Matcher = new();

    /// <summary>Proposes relocation pairs, best first.</summary>
    /// <param name="disappeared">Old holds with no positional counterpart.</param>
    /// <param name="appeared">Staged holds that matched no old hold.</param>
    /// <param name="fingerprints">Fingerprint per hold id; holds without one take no part.</param>
    /// <param name="markerWall">Whether the wall is a glyph (marker) wall.</param>
    /// <returns>The pairs worth suggesting, by descending score.</returns>
    public static IReadOnlyList<RelocationPair> Propose(
        IReadOnlyList<Hold> disappeared,
        IReadOnlyList<Hold> appeared,
        IReadOnlyDictionary<Guid, HoldFingerprint> fingerprints,
        bool markerWall)
    {
        var olds = Candidates(disappeared, fingerprints, _ => true);
        var news = Candidates(appeared, fingerprints, h => !markerWall || IsOnWall(h));
        if (olds.Count == 0 || news.Count == 0)
        {
            return [];
        }

        var pairs = new List<RelocationPair>();
        foreach (var proposal in Matcher.Propose(olds, news))
        {
            var metric = markerWall && HoldFingerprintSimilarity.SizeScore(
                fingerprints[proposal.DisappearedHoldId], fingerprints[proposal.AppearedHoldId]) is not null;
            if (!metric && (proposal.Score < LookAlikeMinScore || proposal.Margin < LookAlikeMinMargin))
            {
                continue;
            }

            pairs.Add(new RelocationPair(
                proposal.DisappearedHoldId, proposal.AppearedHoldId, proposal.Score, proposal.Margin, metric));
        }

        return pairs;
    }

    /// <summary>
    /// False for a staged hold the marker pass measured (it has a <see cref="Hold.MetricSource"/>) but
    /// could place on no facet: its pixels lie off every wall plane. Unmeasured holds get the benefit of
    /// the doubt — the marker pass simply did not see them.
    /// </summary>
    private static bool IsOnWall(Hold hold) => hold.MetricSource is null || hold.FacetId is not null || HoldTexturePlacer.IsRejected(hold);

    private static List<RelocationCandidate> Candidates(
        IReadOnlyList<Hold> holds,
        IReadOnlyDictionary<Guid, HoldFingerprint> fingerprints,
        Func<Hold, bool> plausible)
    {
        // Virtual holds have no pixels, so no fingerprint could describe them.
        return holds
            .Where(h => !h.IsVirtual && plausible(h))
            .Where(h => fingerprints.ContainsKey(h.Id))
            .Select(h => new RelocationCandidate(h.Id, fingerprints[h.Id]))
            .ToList();
    }
}
