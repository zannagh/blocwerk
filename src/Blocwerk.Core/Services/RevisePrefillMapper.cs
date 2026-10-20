// <copyright file="RevisePrefillMapper.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Maps a boulder's own holds onto the CURRENT wall for the Revise editor, so it opens with the same holds
/// the boulder page's Then view draws. The forward mirror of <c>BoulderService.WalkBackToGeneration</c>.
/// </summary>
/// <remarks>
/// A frozen boulder still points at old-generation rows. Their X/Y were measured on an older photo, in that
/// panel's own coordinate space, so a raw nearest-neighbour search on today's holds lands on a neighbour, often
/// on another panel the editor never shows. Lineage (<see cref="HoldGenerationLink"/>) records which current
/// row IS that hold, so it is followed first:
/// <list type="number">
/// <item>A hold that is still live is kept as is.</item>
/// <item>Otherwise its links are walked forward, generation by generation, to the live rows they reach. A
/// deleted successor (a tombstoned link) ends that branch: the hold is gone and nothing is guessed for it.</item>
/// <item>Only a hold with NO outgoing link at all falls back to the nearest current hold, and only on the live
/// panel at the same (Col, Row), within <see cref="MaxGuessDistance"/>, among holds no lineage claims. That
/// pick is a <see cref="RevisePickSource.Guessed"/> one.</item>
/// </list>
/// Several old holds reaching one row converge on a single pick; the most prominent mark wins (Top &gt; Start
/// &gt; Normal, then the lower usage, then the smaller id), the same order the backward walk uses.
/// </remarks>
public static class RevisePrefillMapper
{
    /// <summary>
    /// The farthest a guessed hold may be from the old hold's spot, in panel-local 0..1 units. Tighter than the
    /// 0.06 the editor used to use: an old photo is shifted by about 0.01 to 0.03, and a dense wall has a
    /// neighbour within 0.06 of nearly every hold.
    /// </summary>
    public const double MaxGuessDistance = 0.03;

    /// <summary>Maps <paramref name="marks"/> onto <paramref name="liveHolds"/>.</summary>
    /// <param name="marks">The boulder's own holds.</param>
    /// <param name="lineage">The wall's hold lineage, tombstones included.</param>
    /// <param name="liveHolds">The current holds the editor can pick from.</param>
    /// <param name="panelPositions">(Col, Row) of every panel id that can occur, old generations included.</param>
    /// <param name="livePanelIds">The live panels; the one at (0, 0) also carries the holds with no panel.</param>
    /// <returns>The picks, the holds that map to nothing, and the panel to open on.</returns>
    public static RevisePrefill Map(
        IReadOnlyCollection<ReviseMark> marks,
        IReadOnlyCollection<ReviseLineageStep> lineage,
        IReadOnlyCollection<Hold> liveHolds,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlyCollection<Guid> livePanelIds)
    {
        var live = liveHolds.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        var forward = lineage
            .Where(s => s.OldHoldId is not null && s.ToGeneration > s.FromGeneration)
            .ToLookup(s => s.OldHoldId!.Value);
        var claimed = lineage
            .Where(s => s.OldHoldId is not null && s.NewHoldId is not null)
            .Select(s => s.NewHoldId!.Value)
            .ToHashSet();
        var centre = livePanelIds.Where(id => PositionOf(panelPositions, id) == (0, 0)).Order().FirstOrDefault();

        var ordered = marks
            .OrderByDescending(m => m.Type)
            .ThenBy(m => m.Usage)
            .ThenBy(m => m.HoldId)
            .ToList();
        var picks = new RevisePickSet();
        var unmapped = new List<Guid>();

        foreach (var mark in ordered.Where(m => live.ContainsKey(m.HoldId)))
        {
            picks.Add(mark.HoldId, mark, RevisePickSource.Carried);
        }

        var unlinked = PickThroughLineage(ordered.Where(m => !live.ContainsKey(m.HoldId)), forward, live, picks, unmapped);
        foreach (var mark in unlinked)
        {
            var origin = mark.PanelId is { } panel ? PositionOf(panelPositions, panel) : (0, 0);
            var guess = NearestUnclaimed(mark, origin, live.Values, claimed, picks, panelPositions, centre);
            if (guess is null)
            {
                unmapped.Add(mark.HoldId);
                continue;
            }

            picks.Add(guess.Id, mark, RevisePickSource.Guessed);
        }

        var result = picks.Build();
        return new RevisePrefill(result, unmapped, StartPanel(result, live, centre));
    }

    /// <summary>
    /// Picks the live rows each non-live mark's lineage reaches. A mark whose lineage reaches nothing (its
    /// successor was deleted) goes to <paramref name="unmapped"/>; one with no outgoing link at all is returned,
    /// as the only kind of hold a position guess is allowed for.
    /// </summary>
    private static List<ReviseMark> PickThroughLineage(
        IEnumerable<ReviseMark> marks,
        ILookup<Guid, ReviseLineageStep> forward,
        IReadOnlyDictionary<Guid, Hold> live,
        RevisePickSet picks,
        List<Guid> unmapped)
    {
        var unlinked = new List<ReviseMark>();
        foreach (var mark in marks)
        {
            if (!forward.Contains(mark.HoldId))
            {
                unlinked.Add(mark);
                continue;
            }

            var reached = WalkForward(mark.HoldId, forward, live);
            if (reached.Count == 0)
            {
                unmapped.Add(mark.HoldId);
            }

            foreach (var (holdId, changed) in reached)
            {
                picks.Add(holdId, mark, changed ? RevisePickSource.LineageChanged : RevisePickSource.Lineage);
            }
        }

        return unlinked;
    }

    /// <summary>
    /// The live rows <paramref name="start"/>'s lineage reaches, each with whether a link on the way says the
    /// hold changed. Breadth-first, so a split (one old hold becoming two) yields both; a hop must move forward
    /// in time and a row is visited once, so a malformed or cyclic link set cannot spin.
    /// </summary>
    private static List<(Guid HoldId, bool Changed)> WalkForward(
        Guid start,
        ILookup<Guid, ReviseLineageStep> forward,
        IReadOnlyDictionary<Guid, Hold> live)
    {
        var reached = new Dictionary<Guid, bool>();
        var visited = new HashSet<Guid> { start };
        var frontier = new Queue<(Guid HoldId, bool Changed)>();
        frontier.Enqueue((start, false));

        while (frontier.Count > 0)
        {
            var (cursor, changedSoFar) = frontier.Dequeue();
            foreach (var step in forward[cursor].OrderBy(s => s.ToGeneration).ThenBy(s => s.NewHoldId))
            {
                // A tombstoned successor: the hold was deleted, so this branch ends without a row.
                if (step.NewHoldId is not { } next)
                {
                    continue;
                }

                var changed = changedSoFar || step.Kind == HoldGenerationLinkKind.Changed;
                if (live.ContainsKey(next))
                {
                    // A Same path wins over a Changed one reaching the same row.
                    reached[next] = reached.TryGetValue(next, out var before) ? before && changed : changed;
                }
                else if (visited.Add(next))
                {
                    frontier.Enqueue((next, changed));
                }
            }
        }

        return reached.OrderBy(r => r.Key).Select(r => (r.Key, r.Value)).ToList();
    }

    /// <summary>
    /// The nearest live hold to an unlinked old hold: on the panel at the same position only (raw X/Y on
    /// another panel is a different coordinate space), within <see cref="MaxGuessDistance"/>, and not a row any
    /// lineage already ties to some other old hold, nor one already picked.
    /// </summary>
    private static Hold? NearestUnclaimed(
        ReviseMark mark,
        (int Col, int Row) origin,
        IEnumerable<Hold> live,
        IReadOnlySet<Guid> claimed,
        RevisePickSet picks,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        Guid centre)
    {
        Hold? best = null;
        var bestDistanceSquared = MaxGuessDistance * MaxGuessDistance;
        foreach (var hold in live.OrderBy(h => h.Id))
        {
            var panel = hold.WallPanelId ?? centre;
            if (claimed.Contains(hold.Id) || picks.Contains(hold.Id) || PositionOf(panelPositions, panel) != origin)
            {
                continue;
            }

            var dx = hold.X - mark.X;
            var dy = hold.Y - mark.Y;
            var distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = hold;
            }
        }

        return best;
    }

    /// <summary>The live panel carrying most picks (a hold with no panel counts for the centre), ties to the smaller id.</summary>
    private static Guid? StartPanel(IReadOnlyList<RevisePick> picks, IReadOnlyDictionary<Guid, Hold> live, Guid centre)
    {
        return picks
            .Select(p => live[p.HoldId].WallPanelId ?? centre)
            .Where(id => id != Guid.Empty)
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => (Guid?)g.Key)
            .FirstOrDefault();
    }

    private static (int Col, int Row) PositionOf(IReadOnlyDictionary<Guid, (int Col, int Row)> positions, Guid panelId) =>
        positions.TryGetValue(panelId, out var position) ? position : (int.MinValue, int.MinValue);
}
