// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Views;

/// <summary>
/// A wall's photos as the Server's MultiPanelViewer steps through them: one live panel at a time on its grid, each
/// with the holds drawn in its own image space (a hold of no panel only on a wall without panels, as the Server draws
/// it). A wall without panels shows its single photo.
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

    /// <summary>The photo carrying most of <paramref name="holdIds"/>; the first one when none carries any.</summary>
    /// <param name="holdIds">A boulder's holds.</param>
    /// <returns>The photo, or null.</returns>
    public PanelView? StartFor(IReadOnlySet<Guid> holdIds) => Panels
        .Select(p => (Panel: p, Count: HoldsOn(p).Count(h => holdIds.Contains(h.Id))))
        .Where(x => x.Count > 0)
        .OrderByDescending(x => x.Count).ThenBy(x => x.Panel.Row).ThenBy(x => x.Panel.Col)
        .Select(x => x.Panel)
        .FirstOrDefault() ?? First();

    /// <summary>The holds drawn on <paramref name="panel"/>.</summary>
    /// <param name="panel">The photo.</param>
    /// <returns>Its holds, bases before what is stacked on them.</returns>
    public IReadOnlyList<HoldsDocumentHold> HoldsOn(PanelView panel) => holds
        .Where(h => h.PanelId == panel.Id)
        .OrderBy(h => h.StackedOn is null ? 0 : 1)
        .ToList();

    /// <summary>The photo next to <paramref name="panel"/> on the grid, or null.</summary>
    /// <param name="panel">The photo shown.</param>
    /// <param name="dCol">The column step.</param>
    /// <param name="dRow">The row step.</param>
    /// <returns>The neighbour, or null.</returns>
    public PanelView? Neighbour(PanelView panel, int dCol, int dRow) =>
        panel.Id is null ? null : Panels.FirstOrDefault(p => p.Col == panel.Col + dCol && p.Row == panel.Row + dRow);
}
