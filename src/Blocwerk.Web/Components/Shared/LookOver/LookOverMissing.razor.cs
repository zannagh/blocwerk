// <copyright file="LookOverMissing.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// Code-behind of "Holds the update could not find": which hold is asked about, what the user has pointed at on the
/// new photo, and the answers. The page runs every answer (<see cref="OnAct"/>, <see cref="OnAnswer"/>) and hands
/// back the list as it is then; the hold that was asked about has left the queue by that, so the next one is shown
/// without a tap. A tap next to a hold picks that hold (fingers are wider than small holds); nothing is done before
/// the user has seen which hold or spot was picked and confirmed it.
/// </summary>
public partial class LookOverMissing
{
    private const int AnsweredShown = 30;

    /// <summary>A tap this close to a hold's edge (as a share of the photo's width) still means the hold.</summary>
    private const double SnapMargin = 0.012;

    private readonly Dictionary<Guid, IReadOnlyList<PanelHold>> holdsByPanel = [];
    private Guid? currentId;
    private Guid? lastId;
    private Guid? returnTo;
    private Guid? targetId;
    private (double X, double Y)? movedTo;
    private int index;
    private int focusKey;
    private int loadedVersion = -1;
    private bool showAnswered;
    private string? notice;

    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>The holds still to be answered, in the order they are asked.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<LookOverFindingView> Queue { get; set; } = [];

    /// <summary>The holds of this section that are answered (here, or in the hold editor).</summary>
    [Parameter]
    public IReadOnlyList<LookOverFindingView> Answered { get; set; } = [];

    /// <summary>The page shows one boulder's findings: nothing is offered for many holds at once.</summary>
    [Parameter]
    public bool ForOneBoulder { get; set; }

    /// <summary>An answer is on its way.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Changes whenever the page has loaded the list again: the photos' holds are read again then.</summary>
    [Parameter]
    public int Version { get; set; }

    [Parameter]
    public EventCallback<LookOverChoice> OnAnswer { get; set; }

    [Parameter]
    public EventCallback<LookOverAction> OnAct { get; set; }

    [Parameter]
    public EventCallback<Guid> OnUndo { get; set; }

    [Inject]
    private IWallLookOverService LookOver { get; set; } = default!;

    private int Index => Math.Clamp(index, 0, Math.Max(0, Queue.Count - 1));

    private LookOverFindingView? Current => Queue.Count == 0 ? null : Queue[Index];

    private LookOverFindingView? LastAnswered => lastId is { } id ? Answered.FirstOrDefault(f => f.Id == id) : null;

    private List<LookOverFindingView> Unused => Queue.Where(f => f.Boulders.Count == 0).ToList();

    private List<LookOverFindingView> CoveredUnused => Unused.Where(f => f is { Covered: true, OtherHoldId: not null }).ToList();

    protected override async Task OnParametersSetAsync()
    {
        if (loadedVersion != Version)
        {
            loadedVersion = Version;
            holdsByPanel.Clear();
        }

        // The hold being asked about keeps its place while it is in the queue; once it has left, the one that took
        // its place is next. A hold whose answer was just taken back is turned to again.
        var queue = Queue.ToList();
        var back = returnTo is { } undone ? queue.FindIndex(f => f.Id == undone) : -1;
        var at = back >= 0 ? back : currentId is { } id ? queue.FindIndex(f => f.Id == id) : -1;
        index = at >= 0 ? at : Index;
        returnTo = back >= 0 ? null : returnTo;
        if (Current?.Id != currentId)
        {
            Show(Current);
        }

        if (Current?.PanelId is { } panelId && !holdsByPanel.ContainsKey(panelId))
        {
            try
            {
                holdsByPanel[panelId] = await LookOver.GetPanelHoldsAsync(WallId, panelId);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                // The page says why the look-over is not open to this user; the photo is then shown without holds.
                holdsByPanel[panelId] = [];
            }
        }
    }

    private static LookOverPicture? Before(LookOverFindingView finding) => finding.Pictures?.FirstOrDefault(p => p.View == CheckCropView.Old);

    private static bool CanMove(LookOverFindingView finding) => finding.Answers.Contains(LookOverAnswer.RemoveHold);

    private string PhotoUrl(Guid panelId) => $"/api/walls/{WallId}/panels/{panelId}/photo";

    private IReadOnlyList<PanelHold> PanelHolds(Guid panelId) => holdsByPanel.GetValueOrDefault(panelId) ?? [];

    /// <summary>The hold asked about, as it lies on its photo (it is not among the holds drawn there).</summary>
    private PanelHold? Ghost(LookOverFindingView finding) =>
        finding.PanelId is { } panelId ? PanelHolds(panelId).FirstOrDefault(h => h.Id == finding.HoldId) : null;

    private PanelHold? Target(LookOverFindingView finding) =>
        targetId is { } id && finding.PanelId is { } panelId ? PanelHolds(panelId).FirstOrDefault(h => h.Id == id && !h.Kept) : null;

    private (double X, double Y) Spot(LookOverFindingView finding) => Ghost(finding) is { } ghost ? (ghost.X, ghost.Y) : (finding.SpotX, finding.SpotY);

    /// <summary>Turns to a hold: nothing is picked, except the new hold the update found on its very spot.</summary>
    private void Show(LookOverFindingView? finding)
    {
        currentId = finding?.Id;
        targetId = finding is { CanReassign: true } ? finding.OtherHoldId : null;
        movedTo = null;
        focusKey++;
    }

    private void Step(int by)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        index = (Index + by + Queue.Count) % Queue.Count;
        Show(Current);
    }

    private void ClearChoice() => (targetId, movedTo) = (null, null);

    private void ChooseHold(Guid holdId)
    {
        if (Current is { CanReassign: true })
        {
            (targetId, movedTo) = (holdId, null);
        }
    }

    /// <summary>A tap beside a hold means that hold; a tap on bare wall means "it is here".</summary>
    private void ChooseSpot((double X, double Y) at)
    {
        if (Current is not { PanelId: { } panelId } finding)
        {
            return;
        }

        var near = PanelHolds(panelId)
            .Where(h => !h.Kept)
            .Select(h => (Hold: h, Gap: Math.Sqrt(Math.Pow(h.X - at.X, 2) + Math.Pow(h.Y - at.Y, 2)) - h.Radius))
            .Where(c => c.Gap <= SnapMargin)
            .OrderBy(c => c.Gap)
            .Select(c => c.Hold)
            .FirstOrDefault();
        if (near is not null && finding.CanReassign)
        {
            (targetId, movedTo) = (near.Id, null);
        }
        else if (near is null && CanMove(finding))
        {
            (targetId, movedTo) = (null, at);
        }
    }

    /// <summary>Takes the last answer back and turns to that hold again: it returns to its place in the queue.</summary>
    private Task UndoLastAsync(Guid findingId)
    {
        returnTo = findingId;
        return OnUndo.InvokeAsync(findingId);
    }

    private static Guid[] Shown(LookOverFindingView finding) => [.. finding.Boulders.Select(b => b.Id)];

    private Task AnswerAsync(LookOverFindingView finding, LookOverAnswer answer)
    {
        (lastId, notice) = (finding.Id, null);
        return OnAnswer.InvokeAsync(new LookOverChoice(finding.Id, answer, Shown(finding)));
    }

    private Task ReassignAsync(LookOverFindingView finding, Guid holdId)
    {
        (lastId, notice) = (finding.Id, null);
        return OnAct.InvokeAsync(new LookOverAction(finding.Id, () => LookOver.ReassignAsync(WallId, finding.Id, holdId, Shown(finding))));
    }

    private Task MoveAsync(LookOverFindingView finding, (double X, double Y) to)
    {
        (lastId, notice) = (finding.Id, null);
        return OnAct.InvokeAsync(new LookOverAction(finding.Id, () => LookOver.MoveAsync(WallId, finding.Id, to.X, to.Y)));
    }

    /// <summary>Sends the very holds the button counted; what was answered meanwhile is said, not silently skipped.</summary>
    private Task RemoveUnusedAsync(bool onlyCovered)
    {
        Guid[] ids = [.. (onlyCovered ? CoveredUnused : Unused).Select(f => f.Id)];
        (lastId, notice) = (null, null);
        return OnAct.InvokeAsync(new LookOverAction(
            null, async () => notice = LookOverCopy.BulkRemoved(await LookOver.RemoveUnusedAsync(WallId, ids, onlyCovered))));
    }
}
