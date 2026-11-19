// <copyright file="BoulderDetail.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of the inline grade proposal (see <see cref="CircuitEditFlush"/>): a proposal not yet sent acts
/// on a LIVE boulder at once, so it is not sent on the user's behalf; the open form and its value are kept on the
/// device and reopened after the reload.
/// </summary>
public partial class BoulderDetail
{
    private static readonly JsonSerializerOptions KeepJson = new(JsonSerializerDefaults.Web);

    private IDisposable? _flushRegistration;
    private bool _restoreChecked = true;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    private string KeepKey => KeptCopyKey.For(_currentUserId, KioskContext, $"boulder-edit:{BoulderId}");

    // What the inline editor started from; a restore compares it with the boulder now.
    private string InlineBase => $"{_boulder?.Name}\n{_boulder?.Grade}";

    private bool InlineDirty => _showGradeForm && !string.IsNullOrEmpty(_proposedGrade);

    private string? KeepPayload => InlineDirty
        ? new BaseKeep(JsonSerializer.Serialize(new InlineEditKeep(false, null, null, _showGradeForm, _proposedGrade), KeepJson), InlineBase).ToJson()
        : null;

    /// <summary>Registers the flush while the inline form is open; called from <c>SyncBoulderEditLease</c>.</summary>
    private void SyncDeployFlush()
    {
        if (_showGradeForm)
        {
            _flushRegistration ??= EditFlush.Register(
                InvokeAsync,
                FlushForDeployAsync,
                () => new EditFlushTarget("inline grade proposal", WallId, _currentUserId == Guid.Empty ? null : _currentUserId));
        }
        else if (_flushRegistration is not null)
        {
            // The form closed by the user (saved or cancelled): a copy kept while hidden is obsolete.
            _flushRegistration.Dispose();
            _flushRegistration = null;
            _ = DeployDraftInterop.DropAsync(JS, KeepKey);
        }
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        if (EditorFlushPolicy.InlineBoulderEdit(InlineDirty) == EditFlushAction.Nothing || KeepPayload is not { } payload)
        {
            return EditFlushOutcome.NothingToSave;
        }

        var notice = $"Your unsaved edit of “{_boulder?.Name}” was kept on this device before an update; it is open again.";
        return await DeployDraftInterop.KeepAsync(JS, KeepKey, payload, notice, $"/walls/{WallId}/boulders/{BoulderId}")
            ? EditFlushOutcome.KeptOnDevice
            : EditFlushOutcome.NotSaved;
    }

    /// <summary>Reopens an inline editor kept on this device, once per loaded boulder.</summary>
    private async Task RestoreKeptAsync()
    {
        _restoreChecked = true;
        if (_boulder is null || _isAnonymous)
        {
            return;
        }

        var wrapper = BaseKeep.TryParse((await DeployDraftInterop.TakeAsync(JS, KeepKey))?.Payload);
        var kept = Parse(wrapper?.State);
        if (wrapper is null || kept is null)
        {
            return;
        }

        if (wrapper.Base != InlineBase)
        {
            await DeployDraftInterop.ShowAsync(JS, BaseKeep.ChangedSince, $"/walls/{WallId}/boulders/{BoulderId}");
        }

        // A copy kept by an older build may carry an open rename (kept.Meta): that editor is gone, the edit page replaced it.
        if (kept.Propose && !_isCreator)
        {
            _proposedGrade = kept.ProposedGrade;
            _showGradeForm = true;
        }

        SyncBoulderEditLease();
        StateHasChanged();
    }

    private static InlineEditKeep? Parse(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<InlineEditKeep>(json, KeepJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void DisposeDeployFlush()
    {
        _flushRegistration?.Dispose();
        if (!EditFlush.Stopping)
        {
            _ = DeployDraftInterop.DropAsync(JS, KeepKey);
        }
    }
}
