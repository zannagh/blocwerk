// <copyright file="BoulderRevise.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of an unsaved revise (see <see cref="CircuitEditFlush"/>). There is no "draft revision" of a
/// live or historic boulder: <c>ReviseBoulderAsync</c> rewrites it in place and, for a historic one, brings it back
/// live, so saving half a revise would publish it. Only a DRAFT boulder is saved on the server (its revise keeps it a
/// draft). Everything else is kept on the device and restored into this page after the reload.
/// </summary>
public partial class BoulderRevise
{
    private IDisposable? _flushRegistration;
    private string? _pristineSnapshot;
    private bool _restoreChecked;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    private string KeepKey => $"boulder-revise:{BoulderId}";

    private string ReviseUrl => $"/walls/{WallId}/boulders/{BoulderId}/revise";

    private bool IsDirty => _pristineSnapshot is not null && SnapshotJson != _pristineSnapshot;

    private string? KeepPayload => IsDirty ? SnapshotJson : null;

    /// <summary>Called once a boulder this user may revise has loaded: what it looks like now is "nothing to save".</summary>
    private void ArmDeployFlush()
    {
        _pristineSnapshot = SnapshotJson;
        _restoreChecked = false;
        _flushRegistration ??= EditFlush.Register(InvokeAsync, FlushForDeployAsync);
    }

    private void DisarmDeployFlush()
    {
        _pristineSnapshot = null;
        _restoreChecked = true;
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        var action = _boulder is null
            ? EditFlushAction.Nothing
            : EditorFlushPolicy.BoulderRevise(IsDirty, _isSubmitting, _boulder.IsDraft);
        if (action == EditFlushAction.Nothing)
        {
            return EditFlushOutcome.NothingToSave;
        }

        if (action == EditFlushAction.SaveOnServer && await TrySaveDraftAsync())
        {
            return EditFlushOutcome.SavedOnServer;
        }

        var notice = $"Your changes to “{_name}” were kept on this device before an update; they are back in Revise.";
        return await DeployDraftInterop.KeepAsync(JS, KeepKey, SnapshotJson, notice, ReviseUrl)
            ? EditFlushOutcome.KeptOnDevice
            : EditFlushOutcome.NotSaved;
    }

    private async Task<bool> TrySaveDraftAsync()
    {
        try
        {
            _isSubmitting = true;
            await BoulderService.ReviseBoulderAsync(
                BoulderId, _selectedHolds, _name, _grade, _allKickboardOn, EffectiveHandsFollowFeet,
                _footColorOnly ?? string.Empty, _noMatch, _selectedSetters.ToList());
            await DeployDraftInterop.DropAsync(JS, KeepKey);
            await DeployDraftInterop.NoticeAsync(JS, $"Your draft “{_name}” was saved before an update.", DetailUrl);
            try
            {
                Navigation.NavigateTo(DetailUrl);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NavigationException)
            {
                // The browser is unreachable; the saved draft is what this page reloads with.
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _isSubmitting = false;
            Logger.LogWarning(ex, "Pre-deploy save of draft {BoulderId} failed; keeping it on the device", BoulderId);
            return false;
        }
    }

    /// <summary>Takes back a revise kept on this device for this boulder, once per loaded route.</summary>
    private async Task RestoreKeptAsync()
    {
        _restoreChecked = true;
        var snapshot = BoulderFormSnapshot.TryParse(await DeployDraftInterop.TakeAsync(JS, KeepKey));
        if (snapshot is null || snapshot.Id != BoulderId)
        {
            return;
        }

        var displayIds = _displayHolds.Select(h => h.Id).ToHashSet();
        _name = string.IsNullOrWhiteSpace(snapshot.Name) ? _name : snapshot.Name;
        _grade = snapshot.Grade;
        _allKickboardOn = snapshot.KickboardFootholdsOn;
        _handsFollowFeet = snapshot.HandsFollowFeet;
        _footColorOnly = string.IsNullOrEmpty(snapshot.FootColorOnly) ? null : snapshot.FootColorOnly;
        _noMatch = snapshot.NoMatch;
        _selectedHolds = (snapshot.Holds ?? []).Where(h => displayIds.Contains(h.HoldId)).Select(h => h.ToInput()).ToList();
        _selectedSetters.Clear();
        foreach (var id in (snapshot.Setters ?? []).Where(id => _members.Any(m => m.UserId == id)))
        {
            _selectedSetters.Add(id);
        }

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
