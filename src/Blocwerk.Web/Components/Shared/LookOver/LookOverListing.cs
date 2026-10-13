// <copyright file="LookOverListing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// What the look-over page shows of the findings it loaded, and where. The order and the "on a boulder" facts are
/// taken when the page opens and kept while it is open: a card that was just answered stays where it is (so the next
/// card does not slide under the finger) and stays in the view it was in, even when its answer took its boulders off
/// the hold. A reload sorts again. The holds the update could not find are not cards: they are asked about one by one
/// in their own section (<see cref="Missing"/>), in an order that is likewise fixed when they are first seen. Pure.
/// </summary>
public sealed partial class LookOverListing
{
    private const int RowsPerPhoto = 8;
    private readonly List<Guid> order = [];
    private readonly Dictionary<Guid, HashSet<Guid>> bouldersAtLoad = [];
    private readonly List<Guid> missingOrder = [];

    /// <summary>Gets the findings in the page's order: as first loaded, anything new at the end.</summary>
    public IReadOnlyList<LookOverFindingView> Findings { get; private set; } = [];

    /// <summary>Gets the findings shown as cards: all but the holds the update could not find.</summary>
    public IReadOnlyList<LookOverFindingView> Cards { get; private set; } = [];

    /// <summary>Takes a (re)loaded list, keeping every known finding in its place.</summary>
    /// <param name="loaded">The list as the service orders it.</param>
    public void Take(IReadOnlyList<LookOverFindingView> loaded)
    {
        // Asked first: what boulders use (the most used first), then what lies under a new hold, then the rest along
        // each photo, row by row, so that neighbours follow each other.
        missingOrder.AddRange(loaded
            .Where(f => f.Missing && !missingOrder.Contains(f.Id))
            .OrderByDescending(f => f.Boulders.Count)
            .ThenByDescending(f => f.Covered)
            .ThenBy(f => f.PanelId)
            .ThenBy(f => Math.Floor(f.SpotY * RowsPerPhoto))
            .ThenBy(f => f.SpotX)
            .Select(f => f.Id)
            .ToList());
        foreach (var finding in loaded.Where(f => !bouldersAtLoad.ContainsKey(f.Id)))
        {
            order.Add(finding.Id);
            bouldersAtLoad[finding.Id] = [];
        }

        foreach (var finding in loaded)
        {
            bouldersAtLoad[finding.Id].UnionWith(finding.Boulders.Select(b => b.Id));
        }

        var byId = loaded.ToDictionary(f => f.Id);
        Findings = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        Cards = Findings.Where(f => !InSection(f)).ToList();
    }

    /// <summary>The holds the update could not find that are still to be answered, in the order they are asked.</summary>
    /// <param name="boulderId">Only the holds of this boulder.</param>
    /// <returns>The findings.</returns>
    public IReadOnlyList<LookOverFindingView> Missing(Guid? boulderId)
    {
        var open = Findings.Where(f => f.Missing && Shown(f, false, boulderId)).ToDictionary(f => f.Id);
        return missingOrder.Where(open.ContainsKey).Select(id => open[id]).ToList();
    }

    /// <summary>The holds the update could not find that are answered (here or in the hold editor).</summary>
    /// <param name="boulderId">Only the holds of this boulder.</param>
    /// <returns>The findings.</returns>
    public IReadOnlyList<LookOverFindingView> MissingAnswered(Guid? boulderId) =>
        Findings.Where(f => InSection(f) && !f.Missing && Shown(f, false, boulderId)).Reverse().ToList();

    /// <summary>A current boulder uses the finding's hold, or did when the page opened.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>True when it belongs in the "on boulders" view.</returns>
    public bool OnBoulder(LookOverFindingView finding) => bouldersAtLoad.GetValueOrDefault(finding.Id) is { Count: > 0 };

    /// <summary>The findings of one kind in the chosen view.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="onBouldersOnly">Only findings on a boulder's hold.</param>
    /// <param name="boulderId">Only findings on a hold of this boulder (wins over <paramref name="onBouldersOnly"/>).</param>
    /// <returns>The findings, in the page's order.</returns>
    public IReadOnlyList<LookOverFindingView> Visible(LookOverFindingKind kind, bool onBouldersOnly, Guid? boulderId) =>
        Cards.Where(f => f.Kind == kind && Shown(f, onBouldersOnly, boulderId)).ToList();

    /// <summary>The kinds that have something to show in the chosen view, in the order they are worked through.</summary>
    /// <param name="onBouldersOnly">Only findings on a boulder's hold.</param>
    /// <param name="boulderId">Only findings on a hold of this boulder.</param>
    /// <returns>The kinds.</returns>
    public IReadOnlyList<LookOverFindingKind> Kinds(bool onBouldersOnly, Guid? boulderId) =>
        LookOverRules.Order.Where(k => Cards.Any(f => f.Kind == k && Shown(f, onBouldersOnly, boulderId))).ToList();

    /// <summary>How many findings of a kind are on a boulder's hold, and how many there are in all.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The two counts and how many of all are open.</returns>
    public (int OnBoulders, int All, int Open) Counts(LookOverFindingKind kind)
    {
        var ofKind = Cards.Where(f => f.Kind == kind).ToList();
        return (ofKind.Count(OnBoulder), ofKind.Count, ofKind.Count(f => f.Status == LookOverFindingStatus.Open));
    }

    /// <summary>The findings a bulk answer is about: exactly the ones its button counted, so the server settles those and no others.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="onBouldersOnly">The "on boulders" view: only what it lists.</param>
    /// <param name="withoutBoulders">Only the open findings no current boulder uses (nothing, in the "on boulders" view).</param>
    /// <returns>Their ids.</returns>
    public IReadOnlyList<Guid> BulkIds(LookOverFindingKind kind, bool onBouldersOnly, bool withoutBoulders) =>
        Cards
            .Where(f => f.Kind == kind && f.Status == LookOverFindingStatus.Open && Shown(f, onBouldersOnly, null)
                && (!withoutBoulders || (!onBouldersOnly && f.Boulders.Count == 0)))
            .Select(f => f.Id)
            .ToList();

    /// <summary>The name of a boulder the list is filtered to, as any finding on one of its holds knows it.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>The name, or null when no finding lists it.</returns>
    public string? BoulderName(Guid boulderId) =>
        Findings.SelectMany(f => f.Boulders).FirstOrDefault(b => b.Id == boulderId)?.Name;

    /// <summary>
    /// A hold the update could not find, while it is unanswered and once it is answered. One that is open but no
    /// longer such a hold (its photo was taken again, say) is a card like any other.
    /// </summary>
    private static bool InSection(LookOverFindingView finding) =>
        finding.Missing || (finding.KeptAtUpdate && finding.Status != LookOverFindingStatus.Open);

    private bool Shown(LookOverFindingView finding, bool onBouldersOnly, Guid? boulderId) =>
        boulderId is { } boulder ? bouldersAtLoad[finding.Id].Contains(boulder) : !onBouldersOnly || OnBoulder(finding);
}
