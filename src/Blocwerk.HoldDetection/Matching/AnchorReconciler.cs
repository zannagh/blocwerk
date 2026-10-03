using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Reconciles the seed's wall-space anchors with the image matcher's assignment, AFTER it ran. An anchor
/// never overrides an image proposal: agreement is counted, a contradiction is logged (with the image
/// proposal's confidence) and the image proposal stays. Only an anchor whose two holds are BOTH still
/// unassigned becomes a proposal — at confirm-tier confidence, flagged <c>Rescue = "wallspace"</c> — and
/// only when the warp field does not place it wildly off.
/// </summary>
internal static class AnchorReconciler
{
    /// <summary>Rescue tag of an anchor-added proposal.</summary>
    internal const string RescueTag = "wallspace";

    private const int MaxLoggedConflicts = 10;

    /// <summary>Counts of one reconciliation, for the summary log line.</summary>
    internal readonly record struct Outcome(int Agreed, int Conflicts, int Added, int Rejected);

    /// <summary>Applies the anchors to <paramref name="proposals"/> (and the parallel <paramref name="diags"/>).</summary>
    public static Outcome Apply(
        IReadOnlyList<HoldOverlapAnchor> anchors,
        IReadOnlyList<MatcherHold> leftHolds,
        IReadOnlyList<MatcherHold> rightHolds,
        LocalWarpField field,
        Pt[] leftCentres,
        Pt[] rightCentres,
        double maxResidualPx,
        List<Proposal> proposals,
        List<MatchDiag> diags,
        HashSet<int> usedL,
        HashSet<int> usedR,
        ILogger? diag)
    {
        var leftIdx = IndexOf(leftHolds);
        var rightIdx = IndexOf(rightHolds);
        int agreed = 0, conflicts = 0, added = 0, rejected = 0;
        foreach (var a in anchors)
        {
            if (!leftIdx.TryGetValue(a.LeftHoldId, out int i) || !rightIdx.TryGetValue(a.RightHoldId, out int j))
            {
                continue;
            }

            int existing = proposals.FindIndex(p => p.LeftIdx == i || p.RightIdx == j);
            if (existing >= 0)
            {
                var p = proposals[existing];
                if (p.LeftIdx == i && p.RightIdx == j)
                {
                    agreed++;
                    continue;
                }

                if (conflicts++ < MaxLoggedConflicts)
                {
                    diag?.LogInformation(
                        "[Matcher] anchor conflict: wall-space {AnchorLeft}->{AnchorRight} ({Mm:F0}mm, sim {Sim:F2}) vs image {ImageLeft}->{ImageRight} (conf {Conf:F2}); keeping image",
                        a.LeftHoldId, a.RightHoldId, a.PlaneDistanceMm, a.Similarity,
                        leftHolds[p.LeftIdx].Id, rightHolds[p.RightIdx].Id, p.Confidence);
                }

                continue;
            }

            double resid = field.AnchorCount == 0 ? double.NaN : field.Predict(leftCentres[i]).Dist(rightCentres[j]);
            if (!double.IsFinite(resid) || resid > maxResidualPx)
            {
                rejected++;
                continue;
            }

            usedL.Add(i);
            usedR.Add(j);
            proposals.Add(new Proposal(i, j, 0.30 + (0.14 * Math.Clamp(a.Similarity, 0, 1)), false, resid, RescueTag));
            diags.Add(new MatchDiag(40.0, 0.0));
            added++;
        }

        return new Outcome(agreed, conflicts, added, rejected);
    }

    private static Dictionary<int, int> IndexOf(IReadOnlyList<MatcherHold> holds)
    {
        var map = new Dictionary<int, int>();
        for (int k = 0; k < holds.Count; k++)
        {
            map.TryAdd(holds[k].Id, k);
        }

        return map;
    }
}
