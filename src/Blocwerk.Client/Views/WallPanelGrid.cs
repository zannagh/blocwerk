// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Views;

/// <summary>
/// A wall's photos as the Server's MultiPanelViewer steps through them: one live panel at a time on its grid, each
/// with the holds drawn in its own image space. A wall without panels shows its single photo. A hold with no panel (a
/// virtual hold, which was never stamped with one, or a legacy hold) is drawn on the wall view only on that single
/// photo; a boulder's own such hold goes on the centre panel, as the Server's BoulderDetail places it
/// (<c>RenderPanelId</c>), so a virtual top never vanishes from a multi-panel wall.
/// </summary>
public sealed class WallPanelGrid
{
    private readonly IReadOnlyList<HoldsDocumentHold> holds;

    /// <summary>Initializes a new instance of the <see cref="WallPanelGrid"/> class.</summary>
    /// <param name="wall">The wall document.</param>
    /// <param name="holds">The holds document, or null when it is not cached.</param>
    public WallPanelGrid(WallDocument wall, HoldsDocument? holds)
    {
        this.holds = holds?.Holds ?? [];
        Panels = wall.Panels.Count > 0
            ? wall.Panels.OrderBy(p => p.Row).ThenBy(p => p.Col)
                .Select(p => new PanelView(p.Id, p.Col, p.Row, p.Sha, p.Generation < wall.Generation)).ToList()
            : wall.PhotoSha is { } sha ? [new PanelView(null, 0, 0, sha, false)] : [];
    }

    /// <summary>Gets the photos, row by row.</summary>
    public IReadOnlyList<PanelView> Panels { get; }

    /// <summary>The first photo the wall view shows: the origin cell, else the first one.</summary>
    /// <returns>The photo, or null when the wall has none.</returns>
    public PanelView? First() => Panels.FirstOrDefault(p => p.Col == 0 && p.Row == 0) ?? Panels.FirstOrDefault();

    /// <summary>The live centre panel (the origin cell), where a boulder's holds of no panel are drawn; null without one.</summary>
    public PanelView? Centre => Panels.FirstOrDefault(p => p.Id is not null && p.Col == 0 && p.Row == 0);

    /// <summary>The photo carrying most of <paramref name="holdIds"/>; the first one when none carries any.</summary>
    /// <param name="holdIds">A boulder's holds.</param>
    /// <returns>The photo, or null.</returns>
    public PanelView? StartFor(IReadOnlySet<Guid> holdIds) => Panels
        .Select(p => (Panel: p, Count: BoulderHoldsOn(p, holdIds).Count))
        .Where(x => x.Count > 0)
        .OrderByDescending(x => x.Count).ThenBy(x => x.Panel.Row).ThenBy(x => x.Panel.Col)
        .Select(x => x.Panel)
        .FirstOrDefault() ?? First();

    /// <summary>The panels a boulder's holds are drawn on (its holds of no panel on the centre panel).</summary>
    /// <param name="holdIds">A boulder's holds.</param>
    /// <returns>The panel ids.</returns>
    public IReadOnlySet<Guid> PanelsOf(IReadOnlySet<Guid> holdIds) => Panels
        .Where(p => p.Id is not null && BoulderHoldsOn(p, holdIds).Count > 0)
        .Select(p => p.Id!.Value)
        .ToHashSet();

    /// <summary>The holds the wall view draws on <paramref name="panel"/>.</summary>
    /// <param name="panel">The photo.</param>
    /// <returns>Its holds, bases before what is stacked on them.</returns>
    public IReadOnlyList<HoldsDocumentHold> HoldsOn(PanelView panel) => Ordered(holds.Where(h => h.PanelId == panel.Id));

    /// <summary>
    /// A boulder's holds drawn on <paramref name="panel"/>, and nothing else (the Server's boulder view draws only them):
    /// those of that panel, and on the centre panel also those of no panel.
    /// </summary>
    /// <param name="panel">The photo.</param>
    /// <param name="holdIds">The boulder's holds.</param>
    /// <returns>Its holds there, bases first.</returns>
    public IReadOnlyList<HoldsDocumentHold> BoulderHoldsOn(PanelView panel, IReadOnlySet<Guid> holdIds)
    {
        var centre = panel.Id is not null && Centre is { } c && c.Id == panel.Id;
        return Ordered(holds.Where(h => holdIds.Contains(h.Id) && (h.PanelId == panel.Id || (centre && h.PanelId is null))));
    }

    /// <summary>The photo next to <paramref name="panel"/> on the grid, or null.</summary>
    /// <param name="panel">The photo shown.</param>
    /// <param name="dCol">The column step.</param>
    /// <param name="dRow">The row step.</param>
    /// <returns>The neighbour, or null.</returns>
    public PanelView? Neighbour(PanelView panel, int dCol, int dRow) =>
        panel.Id is null ? null : Panels.FirstOrDefault(p => p.Col == panel.Col + dCol && p.Row == panel.Row + dRow);

    private static List<HoldsDocumentHold> Ordered(IEnumerable<HoldsDocumentHold> holds) =>
        holds.OrderBy(h => h.StackedOn is null ? 0 : 1).ToList();
}
