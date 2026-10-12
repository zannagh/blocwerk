// <copyright file="WallLookOver.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.Components.Shared.LookOver;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Code-behind of the look-over page: loads the wall's findings, answers them through
/// <see cref="IWallLookOverService"/> and finishes the look-over. Each group shows <see cref="PageSize"/> findings at
/// a time (a big wall can leave a hundred unsure matches); the first group with something open starts expanded.
/// </summary>
public partial class WallLookOver
{
    private const int PageSize = 20;
    private const string NotAllowed = "Only the owner, admins and moderators of this wall can look it over.";
    private const string SomeoneElse = "Someone else just changed this. The list below is the current one; check it and answer again.";
    private const string NotOnKiosk = "The wall is looked over from your own device, not from this wall tablet.";

    private readonly Dictionary<LookOverFindingKind, int> shown = [];
    private readonly HashSet<LookOverFindingKind> expanded = [];
    private WallLookOverState? state;
    private IReadOnlyList<LookOverFindingView> findings = [];
    private Guid loadedWallId;
    private bool loading = true;
    private bool busy;
    private bool finished;
    private string? error;
    private string? blocked;

    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IWallLookOverService LookOver { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    private IEnumerable<IGrouping<LookOverFindingKind, LookOverFindingView>> Groups => findings.GroupBy(f => f.Kind);

    private int OpenCount => findings.Count(f => f.Status == LookOverFindingStatus.Open);

    private string OpenFindings => OpenCount == 1 ? "1 finding is" : $"{OpenCount} findings are";

    private string StayOpenLabel => OpenCount == 1 ? "Finish anyway — 1 finding stays open" : $"Finish anyway — {OpenCount} findings stay open";

    private string StatusLine
    {
        get
        {
            if (state is { Pending: true, PendingSince: { } since })
            {
                return LookOverCopy.PendingLine(since.ToLocalTime().ToString("d MMM yyyy, HH:mm"), findings.Count, OpenCount);
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
        if (Groups.FirstOrDefault(g => g.Any(f => f.Status == LookOverFindingStatus.Open)) is { } first)
        {
            expanded.Add(first.Key);
        }

        loading = false;
    }

    private int Shown(LookOverFindingKind kind) => shown.GetValueOrDefault(kind, PageSize);

    private void ShowMore(LookOverFindingKind kind) => shown[kind] = Shown(kind) + PageSize;

    private void Toggle(LookOverFindingKind kind)
    {
        if (!expanded.Remove(kind))
        {
            expanded.Add(kind);
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

        findings = await LookOver.ListAsync(WallId);
    }

    private Task AnswerAsync(LookOverChoice choice) =>
        ActAsync(() => LookOver.AnswerAsync(WallId, choice.FindingId, choice.Answer, choice.ShownBoulderIds));

    private Task SettleUnsureAsync() => ActAsync(() => LookOver.SettleUnsureMatchesAsync(WallId));

    private async Task FinishAsync(bool leaveOpen)
    {
        await ActAsync(() => LookOver.FinishAsync(WallId, leaveOpen));
        finished = error is null && state is { Pending: false };
    }

    /// <summary>Runs an answer, then shows the list as it is now: also after a refusal, which only the current list explains.</summary>
    private async Task ActAsync(Func<Task> action)
    {
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
            error = findings.Count > 0 ? "You may not do that on this wall." : null;
            blocked = findings.Count > 0 ? null : NotAllowed;
        }
        finally
        {
            busy = false;
        }
    }
}
