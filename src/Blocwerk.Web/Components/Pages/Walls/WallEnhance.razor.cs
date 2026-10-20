// <copyright file="WallEnhance.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Code-behind of the enhancement review: loads the current generation's run, pages its items per kind and answers
/// them through <see cref="IWallEnhancementService"/>. One answer runs at a time: a second tap while one is on its way
/// is dropped, so a double tap never answers the next card. An answered card stays in place with its answer until the
/// list is loaded again.
/// </summary>
public partial class WallEnhance
{
    private const int PageSize = 20;
    private const string NotOnKiosk = "The enhancement is reviewed from your own device, not from this wall tablet.";

    private readonly List<WallEnhancementItemView> items = [];
    private WallEnhancementView? view;
    private WallEnhancementItemKind kind;
    private Guid loadedWallId;
    private bool pendingOnly;
    private bool busy;
    private int total;
    private string? blocked;
    private string? error;
    private string? message;

    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IWallEnhancementService Enhancement { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    private WallEnhancementKindCount? Current => view?.Counts.FirstOrDefault(c => c.Kind == kind);

    private string StatusLine => view?.State switch
    {
        WallEnhancementState.Review when view.Pending > 0 => $"{WallEnhancementKinds.Count(view.Pending, "change", "changes")} to look over.",
        WallEnhancementState.Review => "Every change is answered. Finalize to record the enhancement.",
        WallEnhancementState.Finalized => $"Finalized on {view.FinalizedAt?.ToLocalTime().ToString("d MMM yyyy, HH:mm")}"
                                          + (view.FinalizedBy is { } by ? $" by {by}." : "."),
        WallEnhancementState.Running => "The enhancement is still running. Its changes can be reviewed when it is done.",
        WallEnhancementState.Failed => $"The enhancement stopped: {view.Error}",
        _ => "This wall has not been enhanced in its current state yet. Start it from the wall's settings.",
    };

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
            return;
        }

        await RunAsync(async () =>
        {
            view = await Enhancement.GetAsync(WallId);
            kind = view.Counts.FirstOrDefault(c => c.Pending > 0)?.Kind ?? view.Counts.FirstOrDefault()?.Kind ?? WallEnhancementItemKind.Shape;
            await LoadItemsAsync(reset: true);
        });
    }

    private static string AcceptAllLabel(WallEnhancementKindCount count) => count.Kind switch
    {
        WallEnhancementItemKind.Duplicate => $"Merge all {count.Pending}",
        WallEnhancementItemKind.Link => $"Link all {count.Pending}",
        WallEnhancementItemKind.NewHold => $"Add all {count.Pending}",
        _ => $"Keep all {count.Pending}",
    };

    private static string AcceptAllQuestion(WallEnhancementKindCount count) => count.Kind switch
    {
        WallEnhancementItemKind.Duplicate => $"Merge all {count.Pending} pairs the way each one suggests? Boulders move over to the hold that stays.",
        WallEnhancementItemKind.Link => $"Link all {count.Pending} pairs?",
        WallEnhancementItemKind.NewHold => $"Add all {count.Pending} holds to their panels?",
        WallEnhancementItemKind.Colour => $"Keep all {count.Pending} suggested colours? They count as confirmed afterwards.",
        _ => $"Keep all {count.Pending} changes?",
    };

    private Task PickAsync(WallEnhancementItemKind picked)
    {
        kind = picked;
        message = null;
        return RunAsync(() => LoadItemsAsync(reset: true));
    }

    private Task OnPendingOnlyAsync(ChangeEventArgs e)
    {
        pendingOnly = e.Value is true;
        return RunAsync(() => LoadItemsAsync(reset: true));
    }

    private Task MoreAsync() => RunAsync(() => LoadItemsAsync(reset: false));

    private async Task LoadItemsAsync(bool reset)
    {
        if (reset)
        {
            items.Clear();
        }

        var page = await Enhancement.ListItemsAsync(WallId, kind, pendingOnly, items.Count, PageSize);
        total = page.Total;
        items.AddRange(page.Items.Where(i => items.All(known => known.Id != i.Id)));
    }

    private Task DecideAsync((WallEnhancementItemView Item, bool Accept) answer) => RunAsync(async () =>
    {
        var result = await Enhancement.DecideAsync(WallId, answer.Item.Id, answer.Accept);
        var index = items.FindIndex(i => i.Id == answer.Item.Id);
        if (index >= 0)
        {
            items[index] = answer.Item with { Decision = result.Decision, Note = result.Decision == WallEnhancementDecision.Skipped ? result.Message : null };
        }

        message = result.Message;
        view = await Enhancement.GetAsync(WallId);
    });

    private Task AcceptAllAsync(WallEnhancementItemKind picked) => RunAsync(async () =>
    {
        var (accepted, skipped) = await Enhancement.AcceptAllAsync(WallId, picked);
        message = skipped == 0
            ? $"{WallEnhancementKinds.Count(accepted, "change", "changes")} accepted."
            : $"{WallEnhancementKinds.Count(accepted, "change", "changes")} accepted; {skipped} could not be carried out any more and were left as they are.";
        view = await Enhancement.GetAsync(WallId);
        await LoadItemsAsync(reset: true);
    });

    private Task FinalizeAsync() => RunAsync(async () =>
    {
        view = await Enhancement.FinalizeAsync(WallId);
        message = "The enhancement is finalized for this state of the wall. It runs again only after the next wall update.";
    });

    /// <summary>Runs one action with the busy flag set; failures become the error line.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UserFacingException or InvalidOperationException or KioskRestrictedException)
        {
            error = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            blocked = "Only the owner and the admins of this wall can enhance it.";
        }
        finally
        {
            busy = false;
        }
    }
}
