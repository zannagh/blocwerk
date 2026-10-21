// <copyright file="BoulderCreate.DeployFlush.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Helpers;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The pre-deploy flush of an unsaved create (see <see cref="CircuitEditFlush"/>). A signed-in setter's boulder is
/// saved as a draft exactly as "Save as Draft" would save it, under the auto-generated name if the field is empty; the
/// circuit's cookie decides who that is, so a kiosk acting as a member drafts as the member. An anonymous kiosk create
/// is never drafted (a Ghost draft would be invisible to everyone): it is kept on the tablet and restored into the form
/// after the reload. A failed draft save falls back to the same.
/// </summary>
public partial class BoulderCreate
{
    private const string KeptNotice = "Your unsaved boulder was kept on this device before an update; it is back in the form.";

    private IDisposable? _flushRegistration;
    private Guid? _flushUserId;
    private string? _pristineSnapshot;
    private bool _restoreChecked;

    [Inject]
    private CircuitEditFlush EditFlush { get; set; } = default!;

    [Inject]
    private ILogger<BoulderCreate> Logger { get; set; } = default!;

    private string KeepKey => $"boulder-create:{WallId}";

    private string CreateUrl => $"/walls/{WallId}/boulders/create";

    private bool IsDirty => _pristineSnapshot is not null && SnapshotJson != _pristineSnapshot;

    // The form state worth keeping while the page is hidden (a sleeping laptop cannot answer a flush); null while pristine.
    private string? KeepPayload => IsDirty ? SnapshotJson : null;

    /// <summary>Called once the form is built: what it looks like now is "nothing to save".</summary>
    private void ArmDeployFlush()
    {
        _pristineSnapshot = SnapshotJson;
        _flushRegistration ??= EditFlush.Register(
            InvokeAsync, FlushForDeployAsync, () => new EditFlushTarget("boulder create", WallId, _flushUserId));
    }

    private async Task<EditFlushOutcome> FlushForDeployAsync()
    {
        var action = EditorFlushPolicy.BoulderCreate(IsDirty, _isSubmitting, _isAnonymous);
        if (action == EditFlushAction.Nothing)
        {
            return EditFlushOutcome.NothingToSave;
        }

        if (action == EditFlushAction.SaveOnServer && await TrySaveDraftAsync())
        {
            return EditFlushOutcome.SavedOnServer;
        }

        return await DeployDraftInterop.KeepAsync(JS, KeepKey, SnapshotJson, KeptNotice, CreateUrl)
            ? EditFlushOutcome.KeptOnDevice
            : EditFlushOutcome.NotSaved;
    }

    private async Task<bool> TrySaveDraftAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_name))
            {
                _name = BoulderNameGenerator.Generate(_existingNames);
            }

            _isSubmitting = true;
            var boulder = await BoulderService.CreateBoulderAsync(
                WallId, _name, _grade, _selectedHolds, isDraft: true,
                _allKickboardOn, EffectiveHandsFollowFeet, _footColorOnly, _clientId, _noMatch,
                _selectedSetters.ToList());

            // Saved. Everything below needs the browser, is bounded, and cannot undo the save.
            NavigateQuietly(DetailUrl);
            await DeployDraftInterop.TellSavedAsync(JS, KeepKey, $"Your boulder “{boulder.Name}” was saved as a draft before an update.", DetailUrl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _isSubmitting = false;
            Logger.LogWarning(ex, "Pre-deploy draft save of boulder {BoulderId} on wall {WallId} failed; keeping it on the device", _clientId, WallId);
            return false;
        }
    }

    /// <summary>Takes back a create kept on this device, once, after the wall has loaded.</summary>
    private async Task RestoreKeptAsync()
    {
        _restoreChecked = true;
        var snapshot = BoulderFormSnapshot.TryParse(await DeployDraftInterop.TakeAsync(JS, KeepKey));
        if (snapshot is null)
        {
            return;
        }

        // The flush saved it on the server while this browser could not hear about it: point there instead.
        if (!_isAnonymous && await BoulderService.GetBoulderAsync(snapshot.Id) is { } saved)
        {
            await DeployDraftInterop.ShowAsync(JS, $"Your boulder “{saved.Name}” was saved as a draft before an update.", $"/walls/{WallId}/boulders/{saved.Id}");
            return;
        }

        ApplySnapshot(snapshot);
        StateHasChanged();
    }

    private void ApplySnapshot(BoulderFormSnapshot snapshot)
    {
        var holdIds = _holds.Select(h => h.Id).ToHashSet();
        _clientId = snapshot.Id;
        _name = string.IsNullOrWhiteSpace(snapshot.Name) ? _name : snapshot.Name;
        _grade = snapshot.Grade;
        _allKickboardOn = snapshot.KickboardFootholdsOn;
        _footColorOnly = snapshot.FootColorOnly;
        _noMatch = snapshot.NoMatch;
        _selectedHolds = (snapshot.Holds ?? []).Where(h => holdIds.Contains(h.HoldId)).Select(h => h.ToInput()).ToList();
        _selectedSetters.Clear();
        foreach (var id in (snapshot.Setters ?? []).Where(id => _setterOptions.Any(o => o.UserId == id)))
        {
            _selectedSetters.Add(id);
        }
    }

    private void NavigateQuietly(string url)
    {
        try
        {
            Navigation.NavigateTo(url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NavigationException)
        {
            // The browser is unreachable; the reload after the deploy lands on the form, which points to the draft.
        }
    }

    private void DisposeDeployFlush()
    {
        _flushRegistration?.Dispose();

        // Leaving on purpose forgets a copy kept while the tab was hidden; being torn down by the deploy must not.
        if (!EditFlush.Stopping)
        {
            _ = DeployDraftInterop.DropAsync(JS, KeepKey);
        }
    }
}
