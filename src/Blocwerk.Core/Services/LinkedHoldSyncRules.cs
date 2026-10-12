// <copyright file="LinkedHoldSyncRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The pure reconciliation rule for ONE group of linked holds (the same physical hold on several panels).
/// The physical, shared properties are name, colour, material, usage (<see cref="Hold.Category"/>), grip type and the
/// kickboard flag. Position, shape, radius and the per-panel measurements are per photograph and never touched.
/// <para>
/// Per property, the value of the FIRST hold in <c>ordered</c> that has a non-default one is the group's value, and every
/// member takes it. "Default" is blank / null / Hand / not on the kickboard, so a default is filled from whichever side has a
/// value. When members hold DIFFERENT non-default values (only colour, material, grip type and name can), the order decides:
/// the winner panel's hold first, then the most central panel. The conflict is recorded in the report, never silent.
/// </para>
/// </summary>
public static class LinkedHoldSyncRules
{
    /// <summary>Applies the rule to one group, most-authoritative hold first.</summary>
    /// <param name="ordered">The group's holds, winner panel first and then most central first.</param>
    /// <param name="report">Receives the counts and conflicts.</param>
    public static void Apply(IReadOnlyList<Hold> ordered, HoldSyncReport report)
    {
        if (ordered.Count < 2)
        {
            return;
        }

        report.Groups++;
        Sync(ordered, report, "Name", h => h.Name, (h, v) => h.Name = v, v => !string.IsNullOrWhiteSpace(v));
        SyncColor(ordered, report);
        Sync(ordered, report, "Material", h => h.Material, (h, v) => h.Material = v, v => v is not null);
        Sync(ordered, report, "GripType", h => h.HandType, (h, v) => h.HandType = v, v => v is not null);
        Sync(ordered, report, "Usage", h => h.Category, (h, v) => h.Category = v, v => v != HoldCategory.Hand);
        Sync(ordered, report, "Kickboard", h => h.IsOnKickboard, (h, v) => h.IsOnKickboard = v, v => v);
    }

    /// <summary>
    /// Colour, with one addition to the rule: a colour a person set outranks one the detection only proposed
    /// (<see cref="Hold.HasProposedColor"/>), whatever the panel order, and a hold that takes its colour from a proposal
    /// is a proposal too. So a proposal never replaces a set colour, and one physical hold has one proposal.
    /// </summary>
    private static void SyncColor(IReadOnlyList<Hold> ordered, HoldSyncReport report)
    {
        var setFirst = ordered.OrderBy(h => h.HasProposedColor ? 1 : 0).ToList();
        var before = ordered.ToDictionary(h => h.Id, h => h.Color);
        var shown = ordered.ToDictionary(h => h.Id, h => h.HasProposedColor);
        var winner = setFirst.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.Color));
        var proposed = winner is { HasProposedColor: true } ? winner.Color : null;
        Sync(setFirst, report, "Color", h => h.Color, (h, v) => h.Color = v, v => !string.IsNullOrWhiteSpace(v));

        // Every member now has the winner's colour (or none), and the mark follows the winner. A mark left over from an
        // earlier proposal is dropped, so a hold later set to that same colour does not read as suggested again.
        foreach (var hold in ordered)
        {
            var shownBefore = shown[hold.Id];
            hold.ProposedColor = string.IsNullOrWhiteSpace(hold.Color) ? null : proposed;
            if (shownBefore != hold.HasProposedColor && before[hold.Id] == hold.Color)
            {
                report.Updated("Color", hold.Id);
            }
        }
    }

    private static void Sync<T>(
        IReadOnlyList<Hold> ordered,
        HoldSyncReport report,
        string property,
        Func<Hold, T> get,
        Action<Hold, T> set,
        Func<T, bool> isSet)
    {
        var winner = ordered.FirstOrDefault(h => isSet(get(h)));
        if (winner is null)
        {
            return;
        }

        var value = get(winner);
        var replaced = ordered.Select(get).Where(isSet).Where(v => !Equals(v, value)).Distinct().ToList();
        if (replaced.Count > 0)
        {
            report.Conflicted(new HoldSyncConflict(property, winner.Id, $"{value}", [.. replaced.Select(v => $"{v}")]));
        }

        foreach (var hold in ordered.Where(h => !Equals(get(h), value)))
        {
            set(hold, value);
            report.Updated(property, hold.Id);
        }
    }
}
