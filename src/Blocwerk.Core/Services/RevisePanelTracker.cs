// <copyright file="RevisePanelTracker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which panel each hold of the Revise editor is drawn on, which panels the reviser has had on screen, and so which
/// selected holds are off screen or were never seen. The editor's selection spans every panel while the picker
/// draws one, so this is what keeps a selection on another panel from being saved unseen.
/// </summary>
/// <remarks>
/// A hold with no panel id draws on the centre panel (0, 0), the same fallback the picker uses. A hold added inside
/// the picker (a new virtual hold) is not in the wall's hold list, and while the wall is staged it is stored without
/// a panel id, so <see cref="AddHold"/> records the panel it was placed on; without that it would count as a centre
/// hold and send the reviser to the wrong panel.
/// </remarks>
public sealed class RevisePanelTracker
{
    private readonly IReadOnlyList<WallPanelInfo> panels;
    private readonly Dictionary<Guid, Guid?> holdPanels;
    private readonly HashSet<Guid> seen = [];
    private readonly Guid? centre;

    /// <summary>Initializes a new instance of the <see cref="RevisePanelTracker"/> class over the wall's current holds and live panels.</summary>
    /// <param name="holds">The current holds.</param>
    /// <param name="panels">The live panels, in display order.</param>
    public RevisePanelTracker(IEnumerable<Hold> holds, IReadOnlyList<WallPanelInfo> panels)
    {
        this.panels = panels;
        centre = panels.FirstOrDefault(p => p is { Col: 0, Row: 0 })?.Id;
        holdPanels = [];
        foreach (var hold in holds)
        {
            holdPanels[hold.Id] = hold.WallPanelId;
        }
    }

    /// <summary>Cross-panel twins (the same physical hold on another panel): a selection shows on its twins' panels too.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>? Twins { get; set; }

    /// <summary>Whether <paramref name="holdId"/> is a hold the tracker knows.</summary>
    /// <param name="holdId">The hold.</param>
    /// <returns>True when known.</returns>
    public bool Knows(Guid holdId) => holdPanels.ContainsKey(holdId);

    /// <summary>Records a hold added in the picker: on its own panel when it has one, else on the panel it was placed on.</summary>
    /// <param name="hold">The new hold.</param>
    /// <param name="shownPanelId">The panel on screen when it was placed.</param>
    public void AddHold(Hold hold, Guid? shownPanelId) => holdPanels[hold.Id] = hold.WallPanelId ?? shownPanelId;

    /// <summary>The panel a hold draws on (a hold with no panel draws on the centre), or null when unknown.</summary>
    /// <param name="holdId">The hold.</param>
    /// <returns>The panel id.</returns>
    public Guid? PanelOf(Guid holdId) => holdPanels.TryGetValue(holdId, out var panel) ? panel ?? centre : null;

    /// <summary>Records that <paramref name="panelId"/> has been on screen.</summary>
    /// <param name="panelId">The panel.</param>
    public void MarkSeen(Guid panelId) => seen.Add(panelId);

    /// <summary>Selected holds the panel <paramref name="currentPanelId"/> does not draw, per panel they are on, in panel order.</summary>
    /// <param name="selected">The selected hold ids.</param>
    /// <param name="currentPanelId">The panel on screen.</param>
    /// <returns>The off-screen selections per panel.</returns>
    public List<(WallPanelInfo Panel, int Count)> OffScreen(IEnumerable<Guid> selected, Guid currentPanelId) =>
        HiddenFrom(selected, holdId => IsVisibleOn(holdId, currentPanelId));

    /// <summary>The first panel with a selected hold no panel seen so far draws, if any.</summary>
    /// <param name="selected">The selected hold ids.</param>
    /// <returns>That panel and how many such holds it carries.</returns>
    public (WallPanelInfo Panel, int Count)? FirstUnseen(IEnumerable<Guid> selected) =>
        HiddenFrom(selected, holdId => seen.Any(panel => IsVisibleOn(holdId, panel)))
            .Select(x => ((WallPanelInfo Panel, int Count)?)x)
            .FirstOrDefault();

    private bool IsVisibleOn(Guid holdId, Guid panelId) =>
        PanelOf(holdId) == panelId
        || (Twins?.TryGetValue(holdId, out var twins) == true && twins.Any(t => PanelOf(t) == panelId));

    private List<(WallPanelInfo Panel, int Count)> HiddenFrom(IEnumerable<Guid> selected, Func<Guid, bool> shown)
    {
        var hidden = selected.Where(id => !shown(id)).Select(PanelOf).ToList();
        return panels
            .Select(p => (Panel: p, Count: hidden.Count(id => id == p.Id)))
            .Where(x => x.Count > 0)
            .ToList();
    }
}
