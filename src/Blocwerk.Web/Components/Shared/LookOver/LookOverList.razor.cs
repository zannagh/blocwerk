// <copyright file="LookOverList.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// Code-behind of the look-over's list view: which groups are open, how much of each is shown (a big wall can leave a
/// hundred unsure matches, so <see cref="PageSize"/> at a time) and the bulk answers. A bulk answer sends the very
/// findings its button counted; the page runs it like every answer, and what was answered meanwhile is said, not
/// silently skipped.
/// </summary>
public partial class LookOverList
{
    private const int PageSize = 20;
    private const int AnsweredShown = 30;

    private readonly Dictionary<LookOverFindingKind, int> shown = [];
    private readonly HashSet<LookOverFindingKind> toggled = [];
    private LookOverFindingKind? firstOpen;
    private int missingShown = PageSize;
    private bool onBouldersOnly = true;
    private bool showAnswered;
    private bool opened;
    private string? notice;

    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>Show only the findings on a hold of this boulder.</summary>
    [Parameter]
    public Guid? FilterBoulder { get; set; }

    /// <summary>An answer is on its way.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>The card that was just answered or reopened takes the focus once per <see cref="Touches"/>.</summary>
    [Parameter]
    public Guid? LastTouched { get; set; }

    [Parameter]
    public int Touches { get; set; }

    [Parameter]
    public EventCallback<LookOverChoice> OnAnswer { get; set; }

    [Parameter]
    public EventCallback<LookOverAction> OnAct { get; set; }

    [Parameter]
    public EventCallback<Guid> OnUndo { get; set; }

    /// <summary>A hold the update could not find is to be asked about: the page turns to its card.</summary>
    [Parameter]
    public EventCallback<Guid> OnOpen { get; set; }

    /// <summary>What the page loaded, in the order it keeps.</summary>
    [Parameter]
    [EditorRequired]
    public LookOverListing Listing { get; set; } = default!;

    [Inject]
    private IWallLookOverService LookOver { get; set; } = default!;

    private IReadOnlyList<LookOverFindingKind> Kinds => Listing.Kinds(onBouldersOnly, FilterBoulder);

    private int OnBoulderCount => Listing.Cards.Count(Listing.OnBoulder);

    private IReadOnlyList<LookOverFindingView> MissingQueue => Listing.Missing(FilterBoulder);

    private IReadOnlyList<LookOverFindingView> MissingAnswered => Listing.MissingAnswered(FilterBoulder);

    private List<LookOverFindingView> CoveredUnused =>
        MissingQueue.Where(f => f is { Covered: true, OtherHoldId: not null, Boulders.Count: 0 }).ToList();

    protected override void OnParametersSet()
    {
        if (opened)
        {
            return;
        }

        // With nothing on a boulder there is nothing to show first: open on everything.
        opened = true;
        onBouldersOnly = OnBoulderCount > 0;
        firstOpen = Kinds.Cast<LookOverFindingKind?>()
            .FirstOrDefault(k => Listing.Visible(k!.Value, onBouldersOnly, FilterBoulder).Any(f => f.Status == LookOverFindingStatus.Open));
    }

    private int Shown(LookOverFindingKind kind) => shown.GetValueOrDefault(kind, PageSize);

    private void ShowMore(LookOverFindingKind kind) => shown[kind] = Shown(kind) + PageSize;

    /// <summary>One boulder's findings are all open to see; otherwise the first group with something open, until the user says otherwise.</summary>
    private bool IsExpanded(LookOverFindingKind kind) => (FilterBoulder is not null || kind == firstOpen) != toggled.Contains(kind);

    private void Toggle(LookOverFindingKind kind)
    {
        if (!toggled.Remove(kind))
        {
            toggled.Add(kind);
        }
    }

    private Task SettleAllAsync(LookOverFindingKind kind, bool onlyWithoutBoulders)
    {
        var ids = Listing.BulkIds(kind, onBouldersOnly, onlyWithoutBoulders);
        notice = null;
        return OnAct.InvokeAsync(new LookOverAction(
            null, async () => notice = LookOverCopy.BulkShortfall(await LookOver.SettleAllAsync(WallId, kind, ids, onlyWithoutBoulders))));
    }

    private Task RemoveUnusedAsync()
    {
        Guid[] ids = [.. CoveredUnused.Select(f => f.Id)];
        notice = null;
        return OnAct.InvokeAsync(new LookOverAction(
            null, async () => notice = LookOverCopy.BulkRemoved(await LookOver.RemoveUnusedAsync(WallId, ids, true))));
    }
}
