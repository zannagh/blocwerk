using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

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
    /// Copies the appearance/identity fields — <see cref="Hold.Name"/>, <see cref="Hold.Color"/>,
    /// <see cref="Hold.Material"/>, <see cref="Hold.Category"/>, <see cref="Hold.HandType"/> — from
    /// <paramref name="source"/> onto <paramref name="target"/>. Verbatim, nulls included: the source
    /// wins fully, even when that clears a value the target had. Never touches geometry, position, or
    /// lifecycle fields — <see cref="Hold.ShapePoints"/>, X/Y and radius are per-photo facts, since
    /// panels photograph the same hold from different perspectives. Only assigns a field that actually
    /// differs, so a converged pair is left untouched. Returns whether anything changed.
    /// <para>
    /// <see cref="Hold.Name"/> is the one exception to "nulls included": a blank source name is SKIPPED
    /// rather than copied. A name is a physical label that belongs to the hold on every panel, but most
    /// holds are unnamed, so letting an unnamed edit propagate would blank a deliberately named twin.
    /// </para>
    /// </summary>
    public static bool CopyAppearance(Hold source, Hold target)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(source.Name) && target.Name != source.Name)
        {
            target.Name = source.Name;
            changed = true;
        }

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
    /// The non-destructive half of the same propagation: fills only the appearance fields the target has
    /// never had set, and leaves every value the target already carries alone. Same write-if-changed
    /// discipline as <see cref="CopyAppearance"/>; returns whether anything changed.
    /// <para>
    /// This is the rule for UNATTENDED reconciliation — the startup backfill. Source of truth differs by
    /// who is asking: a live edit and an explicit link creation are user actions, so the hold the user
    /// touched (respectively the more-central end they just tied to) wins outright and overwrites. A
    /// backfill is nobody's decision, and its centre-most-wins pick would otherwise REVERT a deliberate
    /// edit made on a peripheral panel at the next restart — a value set on the periphery and a value set
    /// on the centre are indistinguishable to it. Filling gaps still converges the legacy components the
    /// backfill exists for (an uncurated twin has nothing set), and can never undo a curation.
    /// </para>
    /// <para>
    /// "Unset" per field: null/blank <see cref="Hold.Name"/> and <see cref="Hold.Color"/>, null
    /// <see cref="Hold.Material"/> and null <see cref="Hold.HandType"/>.
    /// </para>
    /// <para>
    /// <see cref="Hold.Category"/> is deliberately NOT filled. The property is not nullable and every
    /// fresh detection is created at <see cref="HoldCategory.Hand"/>, so "never chosen" and "deliberately
    /// a hand hold" are the same stored value — the same fact that keeps Category out of the editor's
    /// completeness overlay. Treating the default as unset made the backfill overwrite a peripheral hold
    /// the user had deliberately set BACK to Hand with the centre's Foot on every single restart: a
    /// revert-on-restart loop, and precisely the destruction gap-filling exists to prevent. A category
    /// difference between linked twins is left to the user's own edit, which does propagate
    /// (<see cref="CopyAppearance"/>).
    /// </para>
    /// </summary>
    public static bool FillMissingAppearance(Hold source, Hold target)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(source.Name))
        {
            target.Name = source.Name;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.Color) && !string.IsNullOrWhiteSpace(source.Color))
        {
            target.Color = source.Color;
            changed = true;
        }

        if (target.Material is null && source.Material is not null)
        {
            target.Material = source.Material;
            changed = true;
        }

        if (target.HandType is null && source.HandType is not null)
        {
            target.HandType = source.HandType;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Direction-agnostic gap fill across ONE link component: for each appearance field independently, the
    /// value is taken from whichever member HAS one — the most central of those that do — and written onto
    /// every member that has none. Returns the number of holds that changed.
    /// <para>
    /// Strict centre-outward filling does not converge. It only ever moves a value from the single
    /// most-central hold outwards, so a legacy component whose CENTRE is the uncurated end stays
    /// permanently inconsistent: nothing carries a value inward, and nothing carries one sideways between
    /// two peripheral panels. Since a gap fill never overwrites, there is no reason to privilege the
    /// centre for a field the centre does not have — it can only ever fill a hole. Centrality survives as
    /// the TIE-BREAK, so when several members disagree the most central of them still wins, and the pick
    /// stays deterministic across runs.
    /// </para>
    /// <para>
    /// <paramref name="ordered"/> must be sorted most-central-first; see <see cref="MostCentral"/> for the
    /// ordering (centrality, Col, Row, Id).
    /// </para>
    /// </summary>
    public static int FillGapsAcrossComponent(IReadOnlyList<Hold> ordered)
    {
        if (ordered.Count < 2)
        {
            return 0;
        }

        var name = ordered.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.Name))?.Name;
        var color = ordered.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.Color))?.Color;
        var material = ordered.FirstOrDefault(h => h.Material is not null)?.Material;
        var handType = ordered.FirstOrDefault(h => h.HandType is not null)?.HandType;

        var changed = 0;
        foreach (var hold in ordered)
        {
            var holdChanged = false;

            if (string.IsNullOrWhiteSpace(hold.Name) && !string.IsNullOrWhiteSpace(name))
            {
                hold.Name = name;
                holdChanged = true;
            }

            if (string.IsNullOrWhiteSpace(hold.Color) && !string.IsNullOrWhiteSpace(color))
            {
                hold.Color = color;
                holdChanged = true;
            }

            if (hold.Material is null && material is not null)
            {
                hold.Material = material;
                holdChanged = true;
            }

            if (hold.HandType is null && handType is not null)
            {
                hold.HandType = handType;
                holdChanged = true;
            }

            if (holdChanged)
            {
                changed++;
            }
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

    /// <summary>
    /// Orders a component's holds most-central-first, by the same rule <see cref="MostCentral"/> picks
    /// with (centrality, Col, Row, Id) — so the head of the list IS the most central member and the order
    /// is stable across runs. The ordering <see cref="FillGapsAcrossComponent"/> expects.
    /// </summary>
    public static List<HoldCentrality> ByCentrality(IEnumerable<HoldCentrality> candidates)
    {
        var ordered = candidates.ToList();
        ordered.Sort(Compare);
        return ordered;
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
