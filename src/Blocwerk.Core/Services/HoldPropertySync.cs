using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Ranking input for a hold in a link's connected component: the hold plus the grid position of
/// the panel it sits on. A null <see cref="Col"/>/<see cref="Row"/> is a legacy single-photo /
/// centre hold, treated as the (0,0) centre.
/// </summary>
public readonly record struct HoldCentrality(Guid HoldId, int? Col, int? Row);

/// <summary>
/// Shared, dependency-free logic for keeping linked holds (the same physical hold seen across
/// overlapping big-wall panels, tied by <see cref="Entities.HoldLink"/>) in appearance sync. The
/// most-central hold in a link's connected component is the source of truth; peripheral copies
/// inherit its appearance verbatim. Used by the one-time backfill, the link creator, and the live
/// hold editor so all three converge on identical semantics.
/// </summary>
public static class HoldPropertySync
{
    /// <summary>
    /// Copies the appearance/identity fields — <see cref="Hold.Color"/>, <see cref="Hold.Material"/>,
    /// <see cref="Hold.Category"/>, <see cref="Hold.HandType"/> — from <paramref name="source"/> onto
    /// <paramref name="target"/>. Verbatim, nulls included: the centre wins fully, even when that
    /// clears a value the target had. Never touches geometry, position, or lifecycle fields. Only
    /// assigns a field that actually differs, so a converged pair is left untouched. Returns whether
    /// anything changed.
    /// </summary>
    public static bool CopyAppearance(Hold source, Hold target)
    {
        var changed = false;

        if (target.Color != source.Color)
        {
            target.Color = source.Color;
            changed = true;
        }

        if (target.Material != source.Material)
        {
            target.Material = source.Material;
            changed = true;
        }

        if (target.Category != source.Category)
        {
            target.Category = source.Category;
            changed = true;
        }

        if (target.HandType != source.HandType)
        {
            target.HandType = source.HandType;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Manhattan distance of a panel from the (0,0) centre. A null col/row (legacy single-photo /
    /// centre hold) is treated as the centre (0). Smaller = more central = more authoritative.
    /// </summary>
    public static int Centrality(int? col, int? row)
    {
        if (col is not { } c || row is not { } r)
        {
            return 0;
        }

        return Math.Abs(c) + Math.Abs(r);
    }

    /// <summary>
    /// Picks the single source-of-truth hold from a link's connected component. Deterministic so a
    /// tie can never flip-flop between runs: lowest centrality, then lowest Col, then lowest Row,
    /// then lowest Hold.Id (null col/row count as 0 throughout, matching the legacy=centre rule).
    /// </summary>
    public static HoldCentrality MostCentral(IEnumerable<HoldCentrality> candidates)
    {
        HoldCentrality? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || Compare(candidate, best.Value) < 0)
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        return best.Value;
    }

    /// <summary>
    /// Splits <paramref name="holdIds"/> into connected components over the undirected edge set
    /// <paramref name="links"/> (transitive: centre-right-right2 collapses into one component).
    /// Edges whose endpoints are not both in the id set are ignored.
    /// </summary>
    public static List<HashSet<Guid>> ConnectedComponents(
        IEnumerable<Guid> holdIds,
        IEnumerable<HoldLinkPair> links)
    {
        var adjacency = new Dictionary<Guid, List<Guid>>();
        foreach (var id in holdIds)
        {
            if (!adjacency.ContainsKey(id))
            {
                adjacency[id] = new List<Guid>();
            }
        }

        foreach (var link in links)
        {
            if (!adjacency.TryGetValue(link.HoldAId, out var neighboursOfA)
                || !adjacency.TryGetValue(link.HoldBId, out var neighboursOfB))
            {
                continue;
            }

            neighboursOfA.Add(link.HoldBId);
            neighboursOfB.Add(link.HoldAId);
        }

        var visited = new HashSet<Guid>();
        var components = new List<HashSet<Guid>>();
        foreach (var start in adjacency.Keys)
        {
            if (visited.Contains(start))
            {
                continue;
            }

            var component = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(start);
            visited.Add(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var neighbour in adjacency[current])
                {
                    if (visited.Add(neighbour))
                    {
                        queue.Enqueue(neighbour);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static int Compare(HoldCentrality a, HoldCentrality b)
    {
        var centralityA = Centrality(a.Col, a.Row);
        var centralityB = Centrality(b.Col, b.Row);
        if (centralityA != centralityB)
        {
            return centralityA.CompareTo(centralityB);
        }

        var colA = a.Col ?? 0;
        var colB = b.Col ?? 0;
        if (colA != colB)
        {
            return colA.CompareTo(colB);
        }

        var rowA = a.Row ?? 0;
        var rowB = b.Row ?? 0;
        if (rowA != rowB)
        {
            return rowA.CompareTo(rowB);
        }

        return a.HoldId.CompareTo(b.HoldId);
    }
}
