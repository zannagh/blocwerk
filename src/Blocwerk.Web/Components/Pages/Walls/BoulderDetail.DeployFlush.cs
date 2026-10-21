// <copyright file="BoulderDetail.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of the inline editors (see <see cref="CircuitEditFlush"/>): a half-typed rename/regrade or a
/// grade proposal not yet sent. Both act on a LIVE boulder at once, so neither is sent on the user's behalf; the open
/// editor and its values are kept on the device and reopened after the reload.
/// </summary>
public partial class BoulderDetail
{
    private static readonly JsonSerializerOptions KeepJson = new(JsonSerializerDefaults.Web);

    private IDisposable? _flushRegistration;
    private bool _restoreChecked = true;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    private string KeepKey => $"boulder-edit:{BoulderId}";

    private bool InlineDirty =>
        (_editMeta && _boulder is not null && (_editName != _boulder.Name || _editGrade != _boulder.Grade))
        || (_showGradeForm && !string.IsNullOrEmpty(_proposedGrade));

    private string? KeepPayload => InlineDirty
        ? JsonSerializer.Serialize(new InlineEditKeep(_editMeta, _editName, _editGrade, _showGradeForm, _proposedGrade), KeepJson)
        : null;

    /// <summary>Registers the flush while an inline editor is open; called from <c>SyncBoulderEditLease</c>.</summary>
    private void SyncDeployFlush()
    {
        if (_editMeta || _showGradeForm)
        {
            _flushRegistration ??= EditFlush.Register(
                InvokeAsync,
                FlushForDeployAsync,
                () => new EditFlushTarget("inline boulder edit", WallId, _currentUserId == Guid.Empty ? null : _currentUserId));
        }
        else if (_flushRegistration is not null)
        {
            // Both editors closed by the user (saved or cancelled): a copy kept while hidden is obsolete.
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

        var kept = Parse(await DeployDraftInterop.TakeAsync(JS, KeepKey));
        if (kept is null)
        {
            return;
        }

        if (kept.Meta && (_isCreator || _isAdmin || _isSetter))
        {
            _editName = kept.Name ?? _boulder.Name;
            _editGrade = kept.Grade;
            _editMeta = true;
        }

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
