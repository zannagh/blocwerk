// <copyright file="WallDetail.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of wall edit mode (see <see cref="CircuitEditFlush"/>). Hold edits are a batch in the hold
/// editor until "Save Changes"; the flush saves that batch as the button would (<see cref="Shared.WallPhotoEditor.FlushForDeployAsync"/>).
/// </summary>
public partial class WallDetail
{
    private IDisposable? _flushRegistration;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    /// <summary>Registers the flush while edit mode is on; called from <c>SyncEditLease</c>.</summary>
    private void SyncDeployFlush()
    {
        if (_isEditing)
        {
            _flushRegistration ??= EditFlush.Register(
                InvokeAsync,
                FlushForDeployAsync,
                () => new EditFlushTarget("wall edit", WallId, _currentUserId == Guid.Empty ? null : _currentUserId));
        }
        else
        {
            _flushRegistration?.Dispose();
            _flushRegistration = null;
        }
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        if (_panelEditor is null)
        {
            return EditFlushOutcome.NothingToSave;
        }

        EditFlushOutcome outcome;
        try
        {
            outcome = await _panelEditor.FlushForDeployAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Pre-deploy save of hold edits on wall {WallId} failed", WallId);
            outcome = EditFlushOutcome.NotSaved;
        }

        var notice = outcome switch
        {
            EditFlushOutcome.SavedOnServer => "Your unsaved hold edits were saved before an update.",
            EditFlushOutcome.NotSaved => "Your unsaved hold edits or alignment could not be saved before an update; please check the wall.",
            _ => null,
        };
        if (notice is not null)
        {
            await DeployDraftInterop.NoticeAsync(JS, notice, $"/walls/{WallId}");
        }

        return outcome;
    }
}
