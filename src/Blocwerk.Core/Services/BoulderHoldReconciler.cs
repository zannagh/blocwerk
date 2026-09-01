using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One boulder-hold membership fact fed to <see cref="BoulderHoldReconciler"/>: which hold the
/// boulder references and how (Type/Usage). Deliberately detached from the <see cref="Entities.BoulderHold"/>
/// entity so the reconciler stays a pure, testable function over plain values.
/// </summary>
public readonly record struct ReconcilableHold(Guid HoldId, HoldType Type, HoldUsage Usage);

/// <summary>
/// One physical hold after twin reconciliation: every linked twin id that stands for the same
/// physical hold, plus the boulder Type/Usage resolved by prominence across the twins that were
/// actually saved. <see cref="HoldIds"/> always holds at least one id (an unlinked hold is its own
/// singleton).
/// </summary>
public readonly record struct ReconciledHold(
    IReadOnlyList<Guid> HoldIds,
    HoldType Type,
    HoldUsage Usage);

/// <summary>
/// Makes boulder membership twin-aware at read time WITHOUT touching stored data. On a multi-panel
/// wall a physical hold has one <see cref="Entities.Hold"/> row per panel, tied by
/// <see cref="Entities.HoldLink"/>. A boulder may reference only one twin (one-sided) or both twins
/// as separate rows (possibly with conflicting Type/Usage). This collapses a boulder's rows to one
/// entry per physical hold: membership = the whole link component (so a one-sided boulder covers the
/// un-saved twin too), and any Type/Usage conflict is resolved by prominence.
/// <para>
/// Prominence mirrors the detail multi-panel viewer (<c>BoulderDetail.razor</c>
/// <c>ExpandLinkedTwinsAsync</c>): Type is the higher <see cref="HoldType"/> value (Top &gt; Start &gt;
/// Normal). Usage extends the same "most prominent wins" idea to the both-sided conflict the viewer
/// never had to resolve: hand-capable beats foot-only (HandAndFoot &gt; HandOnly &gt; FootOnly).
/// </para>
/// </summary>
public static class BoulderHoldReconciler
{
    /// <summary>
    /// Collapses one boulder's <paramref name="holds"/> to one <see cref="ReconciledHold"/> per
    /// physical hold, using the wall's <paramref name="links"/>. Each returned entry's
    /// <see cref="ReconciledHold.HoldIds"/> lists every twin in that hold's link component — including
    /// twins the boulder never saved — so callers can expand membership to all twins. Type/Usage are
    /// resolved by prominence across only the twins the boulder actually saved.
    /// <para>
    /// A wall with no links (single-panel) is the identity: every hold becomes its own singleton entry
    /// with its own Type/Usage, so single-panel behaviour is unchanged.
    /// </para>
    /// </summary>
    public static List<ReconciledHold> Reconcile(
        IEnumerable<ReconcilableHold> holds,
        IReadOnlyCollection<HoldLinkPair> links)
    {
        // Keep the first-seen row per hold id (the composite key already forbids duplicates, but be
        // defensive so a repeated id can never inflate a component's resolution).
        var rowsById = new Dictionary<Guid, ReconcilableHold>();
        foreach (var hold in holds)
        {
            if (!rowsById.ContainsKey(hold.HoldId))
            {
                rowsById[hold.HoldId] = hold;
            }
        }

        if (rowsById.Count == 0)
        {
            return [];
        }

        // Components must span the boulder's own ids AND every id the wall's links touch, or an edge
        // to an un-saved twin would be dropped (ConnectedComponents ignores edges leaving the id set)
        // and the twin would never surface.
        var componentIds = new HashSet<Guid>(rowsById.Keys);
        foreach (var link in links)
        {
            componentIds.Add(link.HoldAId);
            componentIds.Add(link.HoldBId);
        }

        var reconciled = new List<ReconciledHold>();
        foreach (var component in HoldPropertySync.ConnectedComponents(componentIds, links))
        {
            HoldType? type = null;
            HoldUsage? usage = null;
            foreach (var id in component)
            {
                if (!rowsById.TryGetValue(id, out var row))
                {
                    continue;
                }

                if (type is null || TypeRank(row.Type) > TypeRank(type.Value))
                {
                    type = row.Type;
                }

                if (usage is null || UsageRank(row.Usage) > UsageRank(usage.Value))
                {
                    usage = row.Usage;
                }
            }

            // A component with no saved row is a twin of some other hold the boulder does not use.
            if (type is null || usage is null)
            {
                continue;
            }

            reconciled.Add(new ReconciledHold([.. component], type.Value, usage.Value));
        }

        return reconciled;
    }

    /// <summary>
    /// Type prominence, matching the detail viewer's <c>(int)bh.Type &gt; (int)existing.Type</c>:
    /// Top (2) &gt; Start (1) &gt; Normal (0).
    /// </summary>
    private static int TypeRank(HoldType type)
    {
        return (int)type;
    }

    /// <summary>
    /// Usage prominence for the both-sided conflict: hand-capable beats foot-only. HandAndFoot is the
    /// broadest, then HandOnly, then FootOnly — so a hold used with hands on any twin never shows as
    /// foot-only.
    /// </summary>
    private static int UsageRank(HoldUsage usage)
    {
        return usage switch
        {
            HoldUsage.HandAndFoot => 2,
            HoldUsage.HandOnly => 1,
            HoldUsage.FootOnly => 0,
            _ => 0,
        };
    }
}
