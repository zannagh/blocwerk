// <copyright file="WallDetail.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of wall edit mode (see <see cref="CircuitEditFlush"/>). Hold edits are a batch in the hold
/// editor until "Save Changes"; the flush saves that batch as the button would
/// (<see cref="Shared.WallPhotoEditor.FlushForDeployAsync"/>), in the scope <see cref="EditorFlushPolicy.WallFlush"/>
/// sets. Whatever that scope leaves (deletions, "Mark as changed") is kept on the device and re-drafted when edit mode
/// opens again.
/// </summary>
public partial class WallDetail
{
    private static readonly JsonSerializerOptions KeepJson = new(JsonSerializerDefaults.Web);

    private IDisposable? _flushRegistration;
    private bool _wallKeepChecked = true;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    private string WallKeepKey => KeptCopyKey.For(_currentUserId, KioskContext, $"wall-edit:{WallId}");

    /// <summary>Registers the flush while edit mode is on; called from <c>SyncEditLease</c>.</summary>
    private void SyncDeployFlush()
    {
        if (_isEditing)
        {
            _wallKeepChecked = _flushRegistration is not null && _wallKeepChecked;
            _flushRegistration ??= EditFlush.Register(
                InvokeAsync,
                FlushForDeployAsync,
                () => new EditFlushTarget("wall edit", WallId, _currentUserId == Guid.Empty ? null : _currentUserId));
        }
        else
        {
            _flushRegistration?.Dispose();
            _flushRegistration = null;
            _wallKeepChecked = true;
        }
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        if (_panelEditor is null)
        {
            return EditFlushOutcome.NothingToSave;
        }

        Shared.WallFlushResult result;
        try
        {
            result = await _panelEditor.FlushForDeployAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Pre-deploy save of hold edits on wall {WallId} failed", WallId);
            result = Shared.WallFlushResult.Plain(EditFlushOutcome.NotSaved);
        }

        var outcome = result.Outcome;
        if (result.HasKept && outcome != EditFlushOutcome.NotSaved)
        {
            var payload = JsonSerializer.Serialize(new WallEditKeep([.. result.KeepDeleted], [.. result.KeepMarked]), KeepJson);
            outcome = await DeployDraftInterop.KeepAsync(
                JS, WallKeepKey, payload, "Your hold edits were saved before an update; deletions and “changed” marks are back as drafts for you to confirm.", $"/walls/{WallId}")
                ? EditFlushOutcome.KeptOnDevice
                : EditFlushOutcome.NotSaved;
            return outcome;
        }

        var notice = outcome switch
        {
            EditFlushOutcome.SavedOnServer => SavedNotice(result),
            EditFlushOutcome.NotSaved => "Your unsaved hold edits or alignment could not be saved before an update; please check the wall.",
            _ => null,
        };
        if (notice is not null)
        {
            await DeployDraftInterop.NoticeAsync(JS, notice, $"/walls/{WallId}", AffectedLinks(result));
        }

        return outcome;
    }

    private static string SavedNotice(Shared.WallFlushResult result)
    {
        var parts = new List<string>();
        if (result.Retired.Count > 0)
        {
            parts.Add($"{result.Retired.Count} boulder(s) were retired by the deleted holds");
        }

        if (result.FlaggedForReview.Count > 0)
        {
            parts.Add($"{result.FlaggedForReview.Count} flagged for review by the \u201cchanged\u201d marks");
        }

        return parts.Count == 0
            ? "Your unsaved hold edits were saved before an update."
            : $"Your unsaved hold edits were saved before an update; {string.Join(", and ", parts)}. Open each to review it or revise it back:";
    }

    // Each links to the boulder's page: a retired one offers Revise there (for whoever may revise it) to bring it
    // back onto the current holds, a flagged one is reviewed there.
    private List<NoticeLink> AffectedLinks(Shared.WallFlushResult result) =>
    [
        .. result.Retired.Select(b => new NoticeLink($"{b.Name} (retired)", $"/walls/{WallId}/boulders/{b.Id}")),
        .. result.FlaggedForReview.Select(b => new NoticeLink($"{b.Name} (review)", $"/walls/{WallId}/boulders/{b.Id}")),
    ];

    /// <summary>Once per entry into edit mode: re-drafts decisions an adds-and-moves-only flush kept on the device.</summary>
    private async Task RestoreKeptDecisionsAsync()
    {
        _wallKeepChecked = true;
        var json = (await DeployDraftInterop.TakeAsync(JS, WallKeepKey))?.Payload;
        WallEditKeep? kept;
        try
        {
            kept = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WallEditKeep>(json, KeepJson);
        }
        catch (JsonException)
        {
            kept = null;
        }

        if (kept is null || _panelEditor is null)
        {
            return;
        }

        var (deleted, marked) = _panelEditor.RedraftKeptDecisions(kept.Deleted, kept.Marked);
        if (deleted.Count + marked.Count > 0)
        {
            // Holds on other panels: keep them for when those panels are edited.
            await DeployDraftInterop.KeepAsync(JS, WallKeepKey, JsonSerializer.Serialize(new WallEditKeep(deleted, marked), KeepJson), string.Empty);
        }
    }
}
