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
/// Code-behind of the deck's card for a hold the update could not find: what the user has pointed at on the new
/// photo, and the answers. The page runs every answer (<see cref="OnAct"/>, <see cref="OnAnswer"/>) and hands back
/// the list as it is then; the deck turns to the next card. A tap next to a hold picks that hold (fingers are wider
/// than small holds); nothing is done before the user has seen which hold or spot was picked and confirmed it.
/// </summary>
public partial class LookOverMissing
{
    /// <summary>A tap this close to a hold's edge (as a share of the photo's width) still means the hold.</summary>
    private const double SnapMargin = 0.012;

    private readonly Dictionary<Guid, IReadOnlyList<PanelHold>> holdsByPanel = [];
    private ConfirmAction? remove;
    private Guid? shownId;
    private Guid? targetId;
    private (double X, double Y)? movedTo;
    private int focusKey;
    private int loadedVersion = -1;

    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>The hold that is asked about.</summary>
    [Parameter]
    [EditorRequired]
    public LookOverFindingView Finding { get; set; } = default!;

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

    [Inject]
    private IWallLookOverService LookOver { get; set; } = default!;

    private Guid[] Shown => [.. Finding.Boulders.Select(b => b.Id)];

    /// <summary>Gives the answer with this number: 1 is "still here", 2 asks whether the hold is not there any more.</summary>
    /// <param name="number">The number key that was pressed.</param>
    /// <returns>A task that ends when the answer is sent or its question is open.</returns>
    public Task PressAsync(int number)
    {
        if (Disabled || targetId is not null || movedTo is not null)
        {
            return Task.CompletedTask;
        }

        if (number == 1)
        {
            return AnswerAsync(LookOverAnswer.Settle);
        }

        if (number == 2)
        {
            remove?.Open();
        }

        return Task.CompletedTask;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (loadedVersion != Version)
        {
            loadedVersion = Version;
            holdsByPanel.Clear();
        }

        if (Finding.Id != shownId)
        {
            Show();
        }

        if (Finding.PanelId is { } panelId && !holdsByPanel.ContainsKey(panelId))
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

    /// <summary>Turns to the hold: nothing is picked, except the new hold the update found on its very spot.</summary>
    private void Show()
    {
        shownId = Finding.Id;
        targetId = Finding is { CanReassign: true } ? Finding.OtherHoldId : null;
        movedTo = null;
        focusKey++;
    }

    private void ClearChoice() => (targetId, movedTo) = (null, null);

    private void ChooseHold(Guid holdId)
    {
        if (Finding is { CanReassign: true })
        {
            (targetId, movedTo) = (holdId, null);
        }
    }

    /// <summary>A tap beside a hold means that hold; a tap on bare wall means "it is here".</summary>
    private void ChooseSpot((double X, double Y) at)
    {
        if (Finding is not { PanelId: { } panelId })
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
        if (near is not null && Finding.CanReassign)
        {
            (targetId, movedTo) = (near.Id, null);
        }
        else if (near is null && CanMove(Finding))
        {
            (targetId, movedTo) = (null, at);
        }
    }

    private Task AnswerAsync(LookOverAnswer answer) => OnAnswer.InvokeAsync(new LookOverChoice(Finding.Id, answer, Shown));

    private Task ReassignAsync(Guid holdId)
    {
        var (findingId, shown) = (Finding.Id, Shown);
        return OnAct.InvokeAsync(new LookOverAction(findingId, () => LookOver.ReassignAsync(WallId, findingId, holdId, shown)));
    }

    private Task MoveAsync((double X, double Y) to)
    {
        var findingId = Finding.Id;
        return OnAct.InvokeAsync(new LookOverAction(findingId, () => LookOver.MoveAsync(WallId, findingId, to.X, to.Y)));
    }
}
