// <copyright file="WallLookOver.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.LookOver;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Code-behind of the look-over page: loads the wall's findings, answers them through
/// <see cref="IWallLookOverService"/> and finishes the look-over. Each group shows <see cref="PageSize"/> findings at
/// a time (a big wall can leave a hundred unsure matches); the first group with something open starts expanded. The
/// list keeps its order while the page is open (<see cref="LookOverListing"/>), and one answer runs at a time: a
/// second tap while one is on its way is dropped, so a double tap never answers the next card.
/// </summary>
public partial class WallLookOver
{
    private const int PageSize = 20;
    private const string NotAllowed = "Only the owner, admins and moderators of this wall can look it over.";
    private const string SomeoneElse = "Someone else just changed this. The list below is the current one; check it and answer again.";
    private const string NotOnKiosk = "The wall is looked over from your own device, not from this wall tablet.";

    private readonly Dictionary<LookOverFindingKind, int> shown = [];
    private readonly HashSet<LookOverFindingKind> toggled = [];
    private readonly LookOverListing listing = new();
    private WallLookOverState? state;
    private LookOverFindingKind? firstOpen;
    private Guid loadedWallId;
    private Guid? lastTouched;
    private int touches;
    private int loads;
    private bool onBouldersOnly = true;
    private bool loading = true;
    private bool busy;
    private bool finished;
    private string? error;
    private string? notice;
    private string? blocked;

    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Show only the findings on a hold of this boulder (the link on the boulder page).</summary>
    [SupplyParameterFromQuery(Name = "boulder")]
    public Guid? FilterBoulder { get; set; }

    [Inject]
    private IWallLookOverService LookOver { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    private IReadOnlyList<LookOverFindingKind> Kinds => listing.Kinds(onBouldersOnly, FilterBoulder);

    private int OnBoulderCount => listing.Cards.Count(listing.OnBoulder);

    private IReadOnlyList<LookOverFindingView> MissingQueue => listing.Missing(FilterBoulder);

    private IReadOnlyList<LookOverFindingView> MissingAnswered => listing.MissingAnswered(FilterBoulder);

    private int OpenCount => listing.Findings.Count(f => f.Status == LookOverFindingStatus.Open);

    private string OpenFindings => OpenCount == 1 ? "1 finding is" : $"{OpenCount} findings are";

    private string StayOpenLabel => OpenCount == 1 ? "Finish anyway — 1 finding stays open" : $"Finish anyway — {OpenCount} findings stay open";

    private string StatusLine
    {
        get
        {
            if (state is { Pending: true, PendingSince: { } since })
            {
                return LookOverCopy.PendingLine(since.ToLocalTime().ToString("d MMM yyyy, HH:mm"), listing.Findings.Count, OpenCount);
            }

            if (state is { CompletedAt: { } done })
            {
                var by = state.CompletedBy is { } name ? $" by {name}" : string.Empty;
                var rest = OpenCount == 0 ? string.Empty : $" {OpenFindings} still open.";
                return $"No look-over is waiting. The last one was finished{by} on {done.ToLocalTime():d MMM yyyy, HH:mm}.{rest}";
            }

            return "No look-over is waiting: the last panel update left nothing open.";
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (loadedWallId == WallId)
        {
            return;
        }

        loadedWallId = WallId;
        if (KioskContext.IsKiosk)
        {
            blocked = NotOnKiosk;
            loading = false;
            return;
        }

        await RunAsync(ReloadAsync);

        // With nothing on a boulder there is nothing to show first: open on everything.
        onBouldersOnly = OnBoulderCount > 0;
        firstOpen = Kinds.Cast<LookOverFindingKind?>()
            .FirstOrDefault(k => listing.Visible(k!.Value, onBouldersOnly, FilterBoulder).Any(f => f.Status == LookOverFindingStatus.Open));
        loading = false;
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

    private async Task ReloadAsync()
    {
        state = await LookOver.GetStateAsync(WallId);
        if (state is not { CanLookOver: true })
        {
            blocked = NotAllowed;
            return;
        }

        listing.Take(await LookOver.ListAsync(WallId));
        loads++;
    }

    private Task AnswerAsync(LookOverChoice choice) =>
        ActAsync(choice.FindingId, () => LookOver.AnswerAsync(WallId, choice.FindingId, choice.Answer, choice.ShownBoulderIds));

    private Task UndoAsync(Guid findingId) => ActAsync(findingId, () => LookOver.UndoAsync(WallId, findingId));

    private Task ActOnAsync(LookOverAction action) => ActAsync(action.FindingId, action.Run);

    /// <summary>Sends the very findings the button counted; what was answered meanwhile is said, not silently skipped.</summary>
    private async Task SettleAllAsync(LookOverFindingKind kind, bool onlyWithoutBoulders)
    {
        var ids = listing.BulkIds(kind, onBouldersOnly, onlyWithoutBoulders);
        LookOverBulkResult? result = null;
        await ActAsync(null, async () => result = await LookOver.SettleAllAsync(WallId, kind, ids, onlyWithoutBoulders));
        notice = result is null ? null : LookOverCopy.BulkShortfall(result);
    }

    private async Task FinishAsync(bool leaveOpen)
    {
        await ActAsync(null, () => LookOver.FinishAsync(WallId, leaveOpen));
        finished = error is null && state is { Pending: false };
    }

    /// <summary>
    /// Runs an answer, then shows the list as it is now: also after a refusal, which only the current list explains.
    /// While one is running every further tap is dropped.
    /// </summary>
    private async Task ActAsync(Guid? findingId, Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        (lastTouched, touches) = (findingId, touches + 1);
        notice = null;
        await RunAsync(async () =>
        {
            try
            {
                await action();
            }
            finally
            {
                await ReloadAsync();
            }
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        error = null;
        try
        {
            await action();
        }
        catch (KioskRestrictedException)
        {
            blocked = NotOnKiosk;
        }
        catch (DbUpdateException)
        {
            // Two people answering at once: the other one's change (a hold just deleted, say) got there first.
            error = SomeoneElse;
        }
        catch (InvalidOperationException ex)
        {
            // Not written for the user means a hold operation found something else than the list showed.
            error = ex is UserFacingException ? ex.Message : SomeoneElse;
        }
        catch (UnauthorizedAccessException)
        {
            // The look-over itself was allowed (the list loaded); a hold operation behind an answer said no.
            error = listing.Findings.Count > 0 ? "You may not do that on this wall." : null;
            blocked = listing.Findings.Count > 0 ? null : NotAllowed;
        }
        finally
        {
            busy = false;
        }
    }
}
