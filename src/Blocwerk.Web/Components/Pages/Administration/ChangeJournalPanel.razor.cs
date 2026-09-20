// <copyright file="ChangeJournalPanel.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Administration;

/// <summary>
/// Behaviour behind <c>ChangeJournalPanel.razor</c>: lists journal batches, previews a revert, and —
/// only for a batch that passes every gate — performs one, attributed to the acting admin.
/// <para>
/// The gates are deliberately layered so no button exists whose blast radius the service cannot
/// already contain: a non-<see cref="ChangeJournalStatus.Recorded"/> batch, an unsealed (still
/// appendable) batch and a wall-update promote batch never get a revert control at all, and the
/// control that does appear is armed separately and then confirmed by typing the wall's name.
/// <c>force</c> is never passed.
/// </para>
/// </summary>
public partial class ChangeJournalPanel
{
    private const string RevertLabelPrefix = "revert:";
    private const int PageSize = 25;

    private readonly List<ChangeJournalConflict> outcomeConflicts = [];

    private IReadOnlyList<AdminWallStat> walls = [];
    private ChangeJournalBatchPage? page;
    private ChangeJournalRevertPreview? preview;
    private ChangeJournalBatchSummary? selected;
    private Guid? selectedBatchId;
    private Guid? wallFilter;
    private string confirmInput = string.Empty;
    private string? outcomeMessage;
    private bool outcomeIsFailure;
    private bool armed;
    private bool busy;
    private bool loadFailed;

    [Inject]
    private ChangeJournalBrowser Browser { get; set; } = null!;

    [Inject]
    private ChangeJournalReverter Reverter { get; set; } = null!;

    [Inject]
    private IAdminDashboardService AdminDashboard { get; set; } = null!;

    [Inject]
    private ICurrentUserService CurrentUserService { get; set; } = null!;

    [Inject]
    private ILogger<ChangeJournalPanel> Logger { get; set; } = null!;

    private string WallFilterValue => wallFilter?.ToString() ?? string.Empty;

    /// <summary>
    /// What the operator has to type out: the wall's (or boulder's) own name, so a revert cannot be
    /// confirmed against the wrong wall by muscle memory. Falls back to the label for an unscoped
    /// batch, or one whose scope row is gone.
    /// </summary>
    private string ConfirmationPhrase =>
        string.IsNullOrWhiteSpace(selected?.ScopeName) ? selected?.Label ?? string.Empty : selected.ScopeName;

    private bool CanSubmit =>
        !busy
        && armed
        && !string.IsNullOrWhiteSpace(ConfirmationPhrase)
        && string.Equals(confirmInput.Trim(), ConfirmationPhrase, StringComparison.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            walls = (await AdminDashboard.GetOverviewAsync()).Walls;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Change journal panel could not load the wall list for its filter.");
        }

        await ReloadAsync(0);
    }

    private async Task OnWallFilterChangedAsync(ChangeEventArgs args)
    {
        wallFilter = Guid.TryParse(args.Value?.ToString(), out var id) ? id : null;
        await ReloadAsync(0);
    }

    private async Task ReloadAsync(int skip)
    {
        busy = true;
        loadFailed = false;
        ClearSelection();

        try
        {
            page = wallFilter is { } wallId
                ? await Browser.ListForScopeAsync(ChangeJournalScopeKind.Wall, wallId, skip, PageSize)
                : await Browser.ListRecentAsync(skip, PageSize);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to list change journal batches.");
            page = null;
            loadFailed = true;
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>Read-only dry run of the exact guards a revert would run. Writes nothing.</summary>
    private async Task CheckAsync(ChangeJournalBatchSummary batch)
    {
        ClearSelection();
        selected = batch;
        selectedBatchId = batch.BatchId;
        busy = true;

        try
        {
            preview = await Reverter.PreviewRevertAsync(batch.BatchId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to preview revert of batch {BatchId}.", batch.BatchId);
            outcomeMessage = "That check couldn't be run just now. Try again.";
            outcomeIsFailure = true;
        }
        finally
        {
            busy = false;
        }
    }

    private void Arm()
    {
        armed = true;
        confirmInput = string.Empty;
        outcomeMessage = null;
        outcomeConflicts.Clear();
    }

    private void Disarm()
    {
        armed = false;
        confirmInput = string.Empty;
    }

    private async Task RevertAsync(ChangeJournalBatchSummary batch)
    {
        if (!CanSubmit || preview is null || !CanOfferRevert(batch, preview))
        {
            return;
        }

        busy = true;
        armed = false;
        outcomeConflicts.Clear();

        try
        {
            var actingUserId = (await CurrentUserService.GetCurrentUserAsync()).Id;
            var result = await Reverter.RevertBatchAsync(batch.BatchId, actingUserId);
            ApplyOutcome(batch, result);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Revert of batch {BatchId} threw.", batch.BatchId);
            outcomeMessage = "Something went wrong and nothing was reverted. Try the check again.";
            outcomeIsFailure = true;
        }
        finally
        {
            busy = false;
            confirmInput = string.Empty;
        }

        // Re-read both the list and this batch's preview so a second click sees the new state
        // (Reverted, no longer revertable) rather than the stale one it was rendered from.
        await RefreshAfterRevertAsync(batch);
    }

    private void ApplyOutcome(ChangeJournalBatchSummary batch, ChangeJournalRevertResult result)
    {
        outcomeIsFailure = result.Outcome != ChangeJournalRevertOutcome.Reverted;
        outcomeMessage = result.Outcome switch
        {
            ChangeJournalRevertOutcome.Reverted =>
                $"Reverted: {DescribeCounts(batch)} put back on {ScopeLabel(batch)}. "
                + $"This revert was itself journalled as batch {FormatShortId(result.RevertBatchId)}, "
                + "so it can be undone in turn.",
            ChangeJournalRevertOutcome.AlreadyReverted =>
                "This batch had already been reverted (or another admin reverted it just now). "
                + "Nothing was changed.",
            ChangeJournalRevertOutcome.BatchNotFound =>
                "That batch is no longer in the journal. Nothing was changed.",
            _ =>
                "Blocked: the live data no longer matches what the batch recorded, so the revert was "
                + "rolled back and nothing was changed.",
        };

        if (result.Outcome == ChangeJournalRevertOutcome.Blocked)
        {
            outcomeConflicts.AddRange(result.Conflicts);
        }
    }

    private async Task RefreshAfterRevertAsync(ChangeJournalBatchSummary batch)
    {
        var message = outcomeMessage;
        var failure = outcomeIsFailure;
        var conflicts = outcomeConflicts.ToList();
        var skip = page?.Skip ?? 0;

        await ReloadAsync(skip);

        selected = page?.Items.FirstOrDefault(i => i.BatchId == batch.BatchId) ?? batch;
        selectedBatchId = batch.BatchId;
        outcomeMessage = message;
        outcomeIsFailure = failure;
        outcomeConflicts.AddRange(conflicts);

        try
        {
            preview = await Reverter.PreviewRevertAsync(batch.BatchId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to re-read batch {BatchId} after a revert.", batch.BatchId);
        }
    }

    private void ClearSelection()
    {
        selected = null;
        selectedBatchId = null;
        preview = null;
        armed = false;
        confirmInput = string.Empty;
        outcomeMessage = null;
        outcomeIsFailure = false;
        outcomeConflicts.Clear();
    }
}
