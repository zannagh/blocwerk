// <copyright file="RevisePickSet.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The picks <see cref="RevisePrefillMapper"/> collects, one per current hold, in the order they were made.
/// The first mark to reach a hold decides its type and source; a later one adds itself as a source and has its usage
/// reconciled in (see <see cref="ReconcileUsage"/>).
/// </summary>
internal sealed class RevisePickSet
{
    private static readonly Guid UsageProbeA = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid UsageProbeB = new("00000000-0000-0000-0000-000000000002");

    private readonly List<Guid> order = [];
    private readonly Dictionary<Guid, (ReviseMark Mark, HoldUsage Usage, RevisePickSource Source, List<Guid> Sources)> picks = [];

    /// <summary>Whether <paramref name="holdId"/> is already picked.</summary>
    public bool Contains(Guid holdId) => picks.ContainsKey(holdId);

    /// <summary>Picks <paramref name="holdId"/> for <paramref name="mark"/>, or records a convergence onto it.</summary>
    public void Add(Guid holdId, ReviseMark mark, RevisePickSource source)
    {
        if (picks.TryGetValue(holdId, out var existing))
        {
            if (!existing.Sources.Contains(mark.HoldId))
            {
                existing.Sources.Add(mark.HoldId);
                picks[holdId] = existing with { Usage = ReconcileUsage(existing.Usage, mark.Usage) };
            }

            return;
        }

        order.Add(holdId);
        picks[holdId] = (mark, mark.Usage, source, [mark.HoldId]);
    }

    /// <summary>The picks, in the order they were made.</summary>
    public List<RevisePick> Build() => order
        .Select(id =>
        {
            var (mark, usage, source, sources) = picks[id];
            return new RevisePick(id, mark.Type, usage, source) { SourceHoldIds = sources };
        })
        .ToList();

    /// <summary>
    /// The usage two converging marks end up with. HandAndFoot is the entity DEFAULT as well as the broadest value,
    /// so it reads as "never chosen" and yields to the other mark's deliberate value; two deliberate values go by
    /// <see cref="BoulderHoldReconciler"/>'s prominence. The same rule as merging two virtual holds
    /// (<c>WallService.ReconcileHoldUsage</c>), so a merge never widens a narrowed hold into a foothold.
    /// </summary>
    /// <param name="first">The usage so far.</param>
    /// <param name="other">The converging mark's usage.</param>
    /// <returns>The reconciled usage.</returns>
    internal static HoldUsage ReconcileUsage(HoldUsage first, HoldUsage other)
    {
        if (first == other || other == HoldUsage.HandAndFoot)
        {
            return first;
        }

        if (first == HoldUsage.HandAndFoot)
        {
            return other;
        }

        // Two stand-in ids for one physical hold: only the reconciler's usage prominence is wanted here.
        return BoulderHoldReconciler.Reconcile(
            [new ReconcilableHold(UsageProbeA, HoldType.Normal, first), new ReconcilableHold(UsageProbeB, HoldType.Normal, other)],
            [new HoldLinkPair(UsageProbeA, UsageProbeB)])[0].Usage;
    }
}
