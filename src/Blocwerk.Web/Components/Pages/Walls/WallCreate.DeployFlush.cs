// <copyright file="WallCreate.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of an unsaved new-wall form (see <see cref="CircuitEditFlush"/>). Creating the wall on the
/// user's behalf would be a much bigger act than saving a draft (it starts photo processing and hold detection), so the
/// form is kept on the device instead. The staged photo (up to 10 MB) does not fit in browser storage; the notice says
/// to add it again.
/// </summary>
public partial class WallCreate
{
    private const string KeepKey = "wall-create";
    private static readonly JsonSerializerOptions KeepJson = new(JsonSerializerDefaults.Web);

    private IDisposable? _flushRegistration;
    private bool _restoreChecked;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    private bool IsDirty =>
        !string.IsNullOrWhiteSpace(_model.Name) || !string.IsNullOrWhiteSpace(_model.Description)
        || _model.Angle != 0 || _model.HasMarkers || _photoData is not null;

    private string? KeepPayload => IsDirty
        ? JsonSerializer.Serialize(
            new WallCreateKeep(_model.Name, _model.Description, _model.Angle, _model.HasMarkers, _model.MarkerSizeMm, _photoData is not null),
            KeepJson)
        : null;

    private void ArmDeployFlush()
    {
        _flushRegistration ??= EditFlush.Register(InvokeAsync, FlushForDeployAsync, () => new EditFlushTarget("new wall form", null, null));
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        if (EditorFlushPolicy.WallCreate(IsDirty, _isSubmitting) == EditFlushAction.Nothing || KeepPayload is not { } payload)
        {
            return EditFlushOutcome.NothingToSave;
        }

        var notice = _photoData is null
            ? "Your new wall's details were kept on this device before an update."
            : "Your new wall's details were kept on this device before an update; please add the photo again.";
        return await DeployDraftInterop.KeepAsync(JS, KeepKey, payload, notice, "/walls/create")
            ? EditFlushOutcome.KeptOnDevice
            : EditFlushOutcome.NotSaved;
    }

    private async Task RestoreKeptAsync()
    {
        _restoreChecked = true;
        var json = await DeployDraftInterop.TakeAsync(JS, KeepKey);
        WallCreateKeep? kept;
        try
        {
            kept = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WallCreateKeep>(json, KeepJson);
        }
        catch (JsonException)
        {
            kept = null;
        }

        if (kept is null)
        {
            return;
        }

        _model.Name = kept.Name ?? string.Empty;
        _model.Description = kept.Description;
        _model.Angle = kept.Angle;
        _model.HasMarkers = kept.HasMarkers;
        _model.MarkerSizeMm = kept.MarkerSizeMm;
        StateHasChanged();
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
