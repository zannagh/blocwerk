// <copyright file="NewHoldTriageSwaps.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Settles the detections inside a carried hold whose colour says another hold is there now (a swap, see
/// <see cref="NewHoldTriageGeometry.InsideCarried"/>). Colour alone is not enough: a dark bolt hole or facet inside a strongly
/// coloured hold differs in colour too. A swap must also look changed on the old photo, asked with the detection's own radius
/// whatever its confidence; without that answer (no old photo, no probe, a spot at the frame) it is an inside detection.
/// </summary>
internal static class NewHoldTriageSwaps
{
    /// <summary>
    /// Below this presence score the spot looks changed enough to be another hold. Looser than the "unchanged" bar
    /// (<see cref="NewHoldTriage.SameAsOldScore"/>), because a hold of the same shape in another colour still correlates in
    /// grey: on The Attic the purple hold that replaced an orange one scored 0.65, every second outline and part 0.81 or more.
    /// </summary>
    public const double SwapMaxPresence = 0.75;

    /// <summary>Keeps (and reports) the confirmed swaps; every other swap candidate is discarded as inside its carried hold.</summary>
    /// <param name="input">The panel's triage input.</param>
    /// <param name="aligned">Whether the staged photo is aligned with the old one.</param>
    /// <param name="candidates">The detections the colour rule took for swaps, with the carried hold each lies in.</param>
    /// <param name="presence">The presence probe, or null.</param>
    /// <param name="result">The discards, added to.</param>
    /// <param name="swaps">Collects the confirmed swaps.</param>
    public static void Resolve(
        NewHoldTriageInput input,
        bool aligned,
        IReadOnlyList<(TriageCandidate Candidate, TriageOutline Carried)> candidates,
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence,
        Dictionary<Guid, NewHoldDiscardReason> result,
        ICollection<TriageSwap>? swaps)
    {
        var asked = candidates.Select(c => (c.Candidate, c.Carried, Query: aligned ? Query(input, c.Candidate) : null)).ToList();
        var queries = asked.Where(a => a.Query is not null).Select(a => a.Query!.Value).ToList();
        var scores = presence is null || queries.Count == 0 ? [] : presence(queries);
        var next = 0;
        foreach (var (candidate, carried, query) in asked)
        {
            var score = query is null || next >= scores.Count ? null : scores[next++];
            if (score is { } s && s < SwapMaxPresence)
            {
                swaps?.Add(new TriageSwap(candidate.Id, carried.OldHoldId));
            }
            else
            {
                result[candidate.Id] = NewHoldDiscardReason.InsideCarriedHold;
            }
        }
    }

    private static PresenceQuery? Query(NewHoldTriageInput input, TriageCandidate c)
    {
        if (LocalAffine.Predict(input.NewToOld, c.X, c.Y) is not { } old)
        {
            return null;
        }

        var scale = LocalAffine.Scale(input.NewToOld, c.X, c.Y) ?? 1;
        return new PresenceQuery(c.X, c.Y, old.X, old.Y, scale, c.RadiusPx);
    }
}
