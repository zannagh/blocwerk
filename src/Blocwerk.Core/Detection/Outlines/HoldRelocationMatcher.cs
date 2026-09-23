using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Proposes "same physical hold, moved elsewhere" pairs between holds that DISAPPEARED from an old wall
/// generation and holds that APPEARED unmatched in the new one, using fingerprints only — position is
/// ignored on purpose, a moved hold can be anywhere. Pure logic, no database.
/// </summary>
/// <remarks>
/// Algorithm: score every disappeared × appeared pair with <see cref="HoldFingerprint.Similarity"/>; a
/// pair's <b>margin</b> is its score minus the best score of any OTHER pair sharing either hold (computed
/// over all candidates, before any assignment, so a hold with two look-alikes stays ambiguous even if one
/// look-alike is taken first). Pairs are then accepted greedily by descending score, 1:1, if the score is
/// ≥ <see cref="MinScore"/> and the margin ≥ <see cref="MinMargin"/>. Precision over recall: a missed
/// relocation is just a "new hold", a wrong one silently rewires boulders.
/// </remarks>
public sealed class HoldRelocationMatcher
{
    /// <summary>Default minimum similarity.</summary>
    public const double DefaultMinScore = 0.75;

    /// <summary>Default ambiguity margin.</summary>
    public const double DefaultMinMargin = 0.05;

    /// <summary>Initializes a new instance of the <see cref="HoldRelocationMatcher"/> class.</summary>
    /// <param name="minScore">Minimum fingerprint similarity for a proposal.</param>
    /// <param name="minMargin">Minimum lead over the best competing pair sharing either hold.</param>
    public HoldRelocationMatcher(double minScore = DefaultMinScore, double minMargin = DefaultMinMargin)
    {
        MinScore = minScore;
        MinMargin = minMargin;
    }

    /// <summary>Gets the minimum fingerprint similarity for a proposal.</summary>
    public double MinScore { get; }

    /// <summary>Gets the minimum lead over the best competing pair.</summary>
    public double MinMargin { get; }

    /// <summary>Proposes relocation pairs, best first.</summary>
    /// <param name="disappeared">Holds gone from the old generation.</param>
    /// <param name="appeared">New holds that matched nothing positionally.</param>
    /// <returns>Accepted 1:1 proposals ordered by descending score.</returns>
    public IReadOnlyList<HoldRelocationProposal> Propose(
        IReadOnlyList<RelocationCandidate> disappeared,
        IReadOnlyList<RelocationCandidate> appeared)
    {
        ArgumentNullException.ThrowIfNull(disappeared);
        ArgumentNullException.ThrowIfNull(appeared);
        if (disappeared.Count == 0 || appeared.Count == 0)
        {
            return [];
        }

        double[,] scores = ScoreAll(disappeared, appeared);
        var pairs = RankPairs(scores);
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();
        var accepted = new List<HoldRelocationProposal>();
        foreach (var (i, j, score, margin) in pairs)
        {
            if (score < MinScore || margin < MinMargin || usedOld.Contains(i) || usedNew.Contains(j))
            {
                continue;
            }

            usedOld.Add(i);
            usedNew.Add(j);
            accepted.Add(new HoldRelocationProposal(disappeared[i].HoldId, appeared[j].HoldId, score, margin));
        }

        return accepted;
    }

    private static double[,] ScoreAll(IReadOnlyList<RelocationCandidate> olds, IReadOnlyList<RelocationCandidate> news)
    {
        var scores = new double[olds.Count, news.Count];
        for (int i = 0; i < olds.Count; i++)
        {
            for (int j = 0; j < news.Count; j++)
            {
                scores[i, j] = HoldFingerprint.Similarity(olds[i].Fingerprint, news[j].Fingerprint);
            }
        }

        return scores;
    }

    private static List<(int I, int J, double Score, double Margin)> RankPairs(double[,] scores)
    {
        int n = scores.GetLength(0);
        int m = scores.GetLength(1);
        var pairs = new List<(int I, int J, double Score, double Margin)>(n * m);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double rival = BestRival(scores, i, j);
                pairs.Add((i, j, scores[i, j], rival < 0 ? 1.0 : scores[i, j] - rival));
            }
        }

        pairs.Sort((x, y) => y.Score.CompareTo(x.Score));
        return pairs;
    }

    /// <summary>Best score of any other pair sharing row <paramref name="i"/> or column <paramref name="j"/>; -1 when none.</summary>
    private static double BestRival(double[,] scores, int i, int j)
    {
        double best = -1;
        for (int k = 0; k < scores.GetLength(1); k++)
        {
            if (k != j)
            {
                best = Math.Max(best, scores[i, k]);
            }
        }

        for (int k = 0; k < scores.GetLength(0); k++)
        {
            if (k != i)
            {
                best = Math.Max(best, scores[k, j]);
            }
        }

        return best;
    }
}
